# 11 - Security and Safety Assessment

Scope: the MVP (Phase 1 + 2). Authoritative detail lives in the frozen plan (sections 4.2-4.8) and, from M2, `15-safety-design.md`. This document is the assessment summary; it is not a proof.

## 1. Security posture

- **No elevation in the MVP.** Manifest `asInvoker`; no UAC prompts; no service; no scheduled elevated tasks. Admin-only categories are visible but disabled.
- Operates only on user-owned, allow-listed locations.
- No network access and no telemetry in the MVP.
- Destructive tests run only in `%TEMP%\ClearPathTests\<guid>` sandboxes.

## 2. Assets

| Asset | Why it matters |
|---|---|
| User files in rule roots | Must never be lost or overwritten |
| Quarantine contents | Last copy of user data after quarantine |
| SQLite journal | Sole source of truth for recovery |
| Settings (exclusions, quarantine location) | Misconfiguration could widen blast radius |
| Build/supply chain | Compromised dependency could act with user rights |

## 3. Threat model

| # | Threat | Example | Mitigation (plan ref.) |
|---|---|---|---|
| T1 | TOCTOU / races | File replaced or modified between scan, validation and operation | Handle-based ops: open share-none handle, re-validate (final path, file ID, size, mtime) on that handle, operate through that same handle (4.4). Metadata is a change detector only; SHA-256 is the integrity proof |
| T2 | Junction / symlink / reparse escape | Directory swapped for a junction pointing at `Documents` | Root + ancestors checked for reparse points; root resolved by walking components with relative `NtCreateFile` and `FILE_OPEN_REPARSE_POINT`; children opened relative to parent handle; entries rejected if reparse; per-subtree identity re-check (4.2, 4.3, 4.4) |
| T3 | Path traversal | `..`, mixed separators, 8.3 names, trailing dots/spaces | External-path canonicalization; strictly-beneath component-wise check; handle final-path comparison (4.3) |
| T4 | Device / namespace paths | `\\.\`, `\\?\GLOBALROOT`, `\??\`, `\Device\...`, UNC, volume GUID, reserved names (CON, NUL, COM1) | Rejected for external paths; trusted handle paths accepted only as exact `\\?\X:\...`, parsed structurally, never by blind prefix stripping (4.3) |
| T5 | Alternate data streams | `file.txt:stream`; `Zone.Identifier` | ADS (`:` after drive letter) rejected in paths; cross-volume copy skips any file with named streams (4.3, 4.6b) |
| T6 | Data loss on crash | Power loss / `Process.Kill` mid-copy | Journal (WAL, `synchronous=FULL`) written before and after every step; invariant of one hash-verified copy; startup recovery table; CrashHarness verifies (4.6) |
| T7 | Overwrite on restore | Destination exists or appears during restore | Atomic no-replace rename is the guarantee (`FileRenameInfoEx` Flags=0 / `ReplaceIfExists=FALSE`); Skip or Keep both only; no Replace option; adversarial race tests (4.4, 4.8) |
| T8 | Silent corruption | Bit flip in quarantine copy | Original SHA-256 stored pre-mutation; verified at copy, restore, recovery; mismatch => original kept, NeedsAttention (4.6) |
| T9 | Credential exposure | Passwords/tokens in rules, logs or DB | MVP has no credential-bearing categories (no browser, no registry); logs exclude file contents; later modules must never delete passwords; AI keys never logged (02, later modules) |
| T10 | Over-broad deletion | Rule matches user documents | Explicit allow-list of exact roots; minimum ages; deny-list (profile root, Documents, Desktop, Pictures, Videos, Music, Downloads, OneDrive, ClearPath data/quarantine, Windows, Program Files*, ProgramData); no broad `%APPDATA%` / `%LOCALAPPDATA%` exception (4.1, 4.3) |
| T11 | Quarantine location abuse | Quarantine on removable/network drive, or inside a rule root | FIXED drives only; never inside a rule root; no silent drive choice; free-space and volume identity checks (4.5) |
| T12 | Hard links | Deleting one name of a multi-linked file | `NumberOfLinks > 1` => Skipped for same- and cross-volume (4.4) |
| T13 | Metadata loss on cross-volume copy | Lost ACL, ADS, EA, timestamps | Cross-volume allowed only for NTFS -> NTFS with full SD, timestamps, attributes preserved and verified; otherwise Skipped with reason (4.6b) |
| T14 | Concurrent operations | Two cleanups at once, second app instance | Single-instance mutex + `active_operation` row; second operation rejected (section 2) |
| T15 | Manifest tampering / corruption | Edited or truncated manifest | Manifests are immutable versioned files and never authoritative; journal wins; rebuild only by explicit action after verification (4.6) |
| T16 | Accidental purge | Permanent deletion by mistake or automation | No automatic purge; explicit irreversible confirmation recorded in journal; recovery and retention never purge; NeedsAttention items ineligible (4.6, 4.8) |
| T17 | Supply chain | Malicious or vulnerable package; hostile reference repo | Pinned central packages; NuGetAudit with high/critical as errors; `check-vulns.ps1`; no code copied from the reference repo ([03](03-reference-repo-analysis.md)); signed releases ([14](14-release.md)) |
| T18 | Privilege misuse | Attempt to use SeSecurityPrivilege / ACCESS_SYSTEM_SECURITY | Only if the token already holds it enabled; otherwise SACL undeterminable => cross-volume skip; no privilege adjustment (4.4) |

## 4. Key mitigations summary

1. **Handle-based everything**: validate, open, re-validate on the handle, operate through the handle. No `File.Delete/Move`, `Directory.Delete/Move`, `MoveFileEx` for source files or quarantine artifacts.
2. **No check-then-create**: new files and directories are created relative to a validated parent handle with `FILE_CREATE`.
3. **Verified deletion**: persist in-progress state -> validate handle -> set delete disposition -> close -> independently verify absence (and no entry with the stored file ID) -> only then commit final state. Recovery uses actual filesystem state plus stored identities, not API return codes.
4. **Ordinary delete semantics**: `FileDispositionInfoEx` with `FILE_DISPOSITION_FLAG_DELETE` only; POSIX semantics are not used without a documented requirement and adversarial tests on every supported Windows version.
5. **Capability self-test** at startup (`%LOCALAPPDATA%\ClearPath\selftest`): if the required handle operations are unsupported, scanning/cleaning is disabled with a reason instead of falling back to path-based APIs.
6. **Mutation ownership** enforced by architecture tests ([05](05-architecture.md), [12](12-test-strategy.md)).

## 5. Residual risks

| Risk | Why it remains | Handling |
|---|---|---|
| Purge destroys the last copy | Intentional, user-confirmed | Explicit irreversible confirmation, journaled; documented exception to the invariant |
| Observed free space differs from logical bytes | Other processes, filesystem behaviour | Reported separately with an explanatory label |
| Same-volume quarantine reclaims nothing | By design | Reported as 0 |
| Cross-volume items skipped | Metadata faithfulness cannot be proven for all properties/filesystems | Skipped with reason rather than risk loss |
| Windows behaviour differences | Handle API semantics (for example rename flags, ChangeTime) may differ by version | M2 capability tests on Windows 11 and Windows 10 22H2; undocumented differences => skip, not assume |
| Correctness of low-level P/Invoke code | Complex and safety-critical | M2 design review, M3 independent diff review, adversarial tests, CrashHarness; not formally verified |
| Packages on net10 | Compatibility TBC | Build/smoke gates in M0/M1 ([04](04-stack.md)) |
| Process-level attacker with same user rights | A same-user attacker can already modify user files | Out of scope; ClearPath defends against races and misconfiguration, not a fully compromised user session |
| Windows 10 without security updates | OS-level mitigations absent | Compatibility only; no security assurance for unsupported OS installs ([14](14-release.md)) |

## 6. Not in scope for the MVP security surface

Registry writes, browser data, network calls, AI/API keys, elevated operations, scheduled/automatic cleaning. Each later module requires its own threat-model update before implementation.
