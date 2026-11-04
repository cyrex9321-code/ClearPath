# M1 Report – Phase 1 (Foundation & Dashboard)

Date: 2026-11-05 · Branch: `main` · Status: **complete; stopped for review**

## 1. Scope delivered

| Item | Status | Evidence |
|---|---|---|
| M0: .NET 10 SDK 10.0.403 installed (with your authorization), git repo, solution, central package pinning | Done | `dotnet --list-sdks`; `Directory.Packages.props` |
| Docs 01–14 + index | Done | `docs/01…14`, `docs/README.md` (written by the Sonnet docs agent, reviewed and corrected by the lead) |
| Core contracts (read-only services only) | Done | `src/ClearPath.Core` – Scanner/Cleaner/Quarantine/Journal/PathGuard contracts deliberately **not** written (M2) |
| Windows integration: CPU (`GetSystemTimes`), RAM (`GlobalMemoryStatusEx`), drives, OS, Recycle Bin (query only), privilege/integrity | Done | `src/ClearPath.Windows/Metrics` |
| Race-resistant directory provisioning (volume-root handle walk, `NtCreateFile` relative opens, reparse rejection, `FILE_CREATE`, final-path check, no fallback) | Done | `src/ClearPath.Windows/FileSystem/HandleDirectoryProvisioner.cs` |
| Infrastructure: app paths, JSON settings, SQLite journal schema v1 (WAL, `synchronous=FULL`), read-only history queries, Serilog | Done | `src/ClearPath.Infrastructure` |
| Bootstrap composition root | Done | `src/ClearPath.Bootstrap/ClearPathServices.cs` |
| WPF shell, design system, dark/light/system theme, navigation (12 brief entries + Safety Center + History) | Done | `src/ClearPath.App` (Sonnet UI agent, reviewed by the lead) |
| Dashboard with real data only, 2 s polling only while visible | Done | Screenshot `screenshots/m1/Dashboard.png` |
| Safety Center, History, Settings, Quick Clean (information only), honest "Not available" pages | Done | Screenshots in `screenshots/m1/` |

Not in M1 (by design): scanning, cleaning, quarantine, restore, purge, capability self-test, quarantine-location changes.

## 2. Build, test, supply chain

| Check | Result |
|---|---|
| `dotnet build -c Release` | 0 warnings, 0 errors (TreatWarningsAsErrors on) |
| `build/test.ps1` | **292 total – 291 passed, 0 failed, 1 skipped** |
| Skipped | `Rejects_directory_symlink` – creating a directory symlink needs Developer Mode or admin on this machine; the junction variants of the same check all pass |
| `build/check-vulns.ps1` | No vulnerable packages (direct + transitive) |
| Smoke run (started from Explorer, non-admin) | Started, all pages navigated, log has no ERR/WRN entries |

Test projects: Core (LocalPath parser incl. device/UNC/ADS/traversal/long paths), Windows (CPU maths, metrics, provisioner vs junctions/files/long paths), Infrastructure (schema, WAL, migrations refuse newer DB, history queries, settings incl. corrupt-file preservation), Bootstrap (all services resolve), Architecture (project layering, IL scan for forbidden file-mutation calls, NetArchTest).

## 3. Architecture decisions taken in M1

1. **External vs handle path parsing in Core** (`LocalPath.TryParse` / `TryParseHandlePath`) – structured parsing, no prefix stripping; reused by PathGuard in M3.
2. **Handle-based provisioning of ClearPath's own directories now**, not path-based `CreateDirectory` – meets the "never fall back to path-based directory creation" amendment from day one.
3. **Journal schema v1** created from the plan's columns, versioned by `PRAGMA user_version`; M2 changes ship as a new migration.
4. **Retention default 30 days** (the plan gave no number).
5. **AwesomeAssertions** in place of FluentAssertions (license).
6. `global.json` selects Microsoft.Testing.Platform (required by xunit.v3 on the .NET 10 SDK).

## 4. Issues found and fixed during M1

| Issue | Found by | Fix |
|---|---|---|
| `LocalPath` accepted `C:` + two backslashes as the drive root | Test agent | Rejected as empty component; test un-skipped |
| `LocalPath.Append("")` returned the path unchanged; `Append("x\y")` added two components | Test agent | `Append` now accepts exactly one non-empty component; tests added |
| App refused to start when launched from the Claude desktop app | Smoke run (your screenshot) | Root cause: MSIX file-system virtualization redirected `%LOCALAPPDATA%\ClearPath`. Check kept strict (fail closed); error message now names the cause |
| CPU chart had a white background in dark theme; "1 items" | Lead review of screenshots | Transparent chart background; singular wording |
| Docs 07/10 described paths and settings keys that differ from the code | Lead review | Docs aligned with the implementation |

## 5. Security review findings (lead review; the independent Opus review is scheduled for M2 and M3)

- No filesystem mutation exists outside Infrastructure (own settings/DB/logs) and the directory provisioner; enforced by the IL-scan architecture tests.
- No elevation: manifest is `asInvoker`; Safety Center shows "Not elevated / Medium integrity".
- **Open for M2:** Infrastructure creates `settings.json`, `clearpath.db` and log files by path inside the handle-validated directory (documented in docs/07 and docs/11).
- **Open for M2:** the IL scanner does not see handle-based deletes (`NtSetInformationFile`/`SetFileInformationByHandle`) or reflection; M3 needs rules that allow these only in Cleaning/Backup.
- Leftover from the failed virtualized launch: an empty folder at `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\ClearPath`. Not deleted (outside the project); harmless.

## 6. Agent use

| Agent | Model | Work | Isolation |
|---|---|---|---|
| Lead | Opus | Core, Windows interop, provisioner, Infrastructure, Bootstrap, review, fixes, integration | main |
| Docs | Sonnet | docs 01–14 | docs/ only (before the repo existed, so no worktree) |
| UI | Sonnet | `src/ClearPath.App` | git worktree branch, reviewed and merged |
| Tests | Sonnet | `tests/`, `build/` | git worktree branch, reviewed and merged |

## 7. Next: M2 (needs your approval to start)

`docs/15-safety-design.md`: final Core interfaces, handle-operation API design, PathGuard flow, quarantine/restore state diagrams, full crash-recovery matrix, cross-volume metadata policy (incl. ChangeTime and security-descriptor capability tests), adversarial test plan. Independent Opus review, then stop for your approval. No Cleaning/Backup code before that.
