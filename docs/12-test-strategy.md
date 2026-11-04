# 12 - Test Strategy

## 1. Principles

- Safety first: every destructive operation has tests for normal, invalid input, concurrent modification, access denied, cancellation, process termination, recovery and integrity verification (the **destructive-op matrix**).
- **Sandbox rule:** destructive tests run only under `%TEMP%\ClearPathTests\<guid>` and the sandbox is removed afterwards. Tests never touch real user data or real rule roots.
- Tests run under an **ordinary non-admin token**, so access-right assumptions (no `SeSecurityPrivilege`, no elevation) are exercised for real.
- Tests that need a second fixed volume run on the real second volume (D: is present on the dev machine) and are **skipped with a reason** if none exists - never silently passed.
- Adversarial tests use real races (racing thread + barrier), not mocks, where the property depends on OS behaviour.

## 2. Test projects

| Project | Focus |
|---|---|
| ClearPath.Core.Tests | Models, result types, pure logic |
| ClearPath.Scanning.Tests | Scanner behaviour, reparse handling, no-write guarantee |
| ClearPath.Cleaning.Tests | PathGuard, handle operations, state machines |
| ClearPath.Backup.Tests | Quarantine, restore, recovery, root confinement |
| ClearPath.Windows.Tests | Metrics math, known folders, Recycle Bin query, capability self-test |
| ClearPath.Infrastructure.Tests | Journal fault injection, WAL durability, settings |
| ClearPath.Bootstrap.Tests | DI resolution and lifetimes |
| ClearPath.Architecture.Tests | Dependency and API-usage rules (NetArchTest + IL call scan) |
| ClearPath.CrashHarness | Console process (not a unit-test project) for kill/recovery runs |

Stack: xunit.v3, Microsoft.NET.Test.Sdk, AwesomeAssertions, NetArchTest.Rules ([04](04-stack.md)).

## 3. Test categories (from plan section 5)

### Cleaning
Traversal (`..`, mixed separators, 8.3 names, trailing dots/spaces); symlink file; junction escape; junction in an ancestor; ADS; device/UNC paths; deny-list; profile exceptions only for exact allow-listed roots; file modified after scan; file replaced after scan (new ID); swap between validation and operation (handle held => swap fails); locked file; missing file; cancellation; concurrent operation rejected; unsupported handle op => safe skip (injected capability); Cleaning cannot mutate outside rule roots.

### PathGuard paths
Long paths (> 260 chars) round-trip through the handle `\\?\X:\` form and validate; rejected: `\\.\`, `\\?\GLOBALROOT`, `\??\`, `\Device\HarddiskVolume1\...`, `\\?\UNC\`, volume-GUID, reserved names; the prefix-stripping trick (`\\?\` + traversal) is rejected.

### Scanner
Directory replaced by a junction mid-scan => not followed, skipped with reason; directory deleted/recreated mid-scan => skipped; root ancestor reparse point => rule skipped; scan performs no writes.

### Attributes
ReadOnly / System / locked => skipped, attributes unchanged.

### No-overwrite (adversarial)
Destination exists; another process creates the destination between validation and rename (racing thread + barrier); a destination with a different file ID appears during restore. In all cases the existing destination is byte-identical with the same ID afterwards.

### Metadata
Files with ADS, EAs, sparse, compressed, EFS-encrypted, hard links, custom DACL, odd timestamps => cross-volume skipped with reason or preserved and verified; same-volume keeps all metadata.

### Manifest
Corrupted, missing, or disagreeing with the journal => journal wins, reported, rebuild only via explicit action.

### Purge
Ineligible items (NeedsAttention) refused; partial purge reported; recovery never purges.

### Hashing
Hash stored before mutation; a corrupted quarantine copy (injected bit flip) is never Quarantined/Restored, original kept, NeedsAttention.

### Backup
Quarantine -> restore byte-equal (same- and cross-volume); restore onto an existing destination (Skip, Keep both; no overwrite even under race); recovery scenarios A-D plus same-volume crash points, each asserting a verified copy survives; interrupted restore; Backup cannot write outside the quarantine root; destination validation (overlaps rule root, UNC, removable => rejected); cross-volume path including insufficient space and each state's recovery preserving a verified copy.

### Infrastructure
Fault injection at every journal/filesystem step, crash, reconcile, assert journal == filesystem; WAL durability.

### Accounting
Same-volume quarantine => 0 reclaimed; purge => logical vs observed reported separately.

### Windows
CPU from `GetSystemTimes` within 0-100 and the interval math unit-tested with fixed inputs; RAM; known folders; Recycle Bin query; handle-operation capability self-test.

### Bootstrap
Every Core interface resolves; singleton/transient lifetimes correct.

### Architecture
App references only Core + Bootstrap; implementation projects reference only Core; no `File.Delete/Move`, `Directory.Delete/Move`, `MoveFileEx` in any assembly for source operations; Scanning has no mutation calls.

### CrashHarness
Console process cleaning 1,000 sandbox files with `Process.Kill` at random points, repeated N times; recovery asserts every file exists exactly once, at source or in quarantine, with a matching hash. (N is chosen in M3; no figure is promised here.)

## 4. Destructive-operation matrix

Applied to: quarantine (same-volume, cross-volume), restore (same-, cross-volume), purge, partial-artifact cleanup, manifest write.

| Dimension | Required evidence |
|---|---|
| Normal | Happy path, hash verified |
| Invalid input | Rejected by PathGuard with reason |
| Concurrent modification | Skipped / NeedsAttention; no data lost |
| Access denied | Skipped with reason; no escalation |
| Cancellation | Current item finishes; remainder Skipped("cancelled") |
| Process termination | Recovery table outcome; verified copy survives |
| Recovery | Journal == filesystem after reconcile |
| Integrity verification | Hash mismatch never promoted to success |

## 5. Phase 2 completion gate (checks)

1. `dotnet build -c Release` clean; `dotnet test` all green; `build/check-vulns.ps1` clean.
2. App runs non-admin; dashboard CPU/RAM/disk match Task Manager (manual).
3. Kill mid-cleanup on sandbox data -> relaunch -> reconciled, nothing lost.
4. Real-world Subset A and B only with explicit user OK at that moment.
5. Screenshots (dashboard, Quick Clean results, confirm dialog, report, History, Safety Center) and `docs/phase2-report.md`.

## 6. What tests cannot show

- They demonstrate the tested scenarios, not absence of all races. Residual risk is recorded in [11](11-security-assessment.md).
- Windows-version behaviour is verified only on the versions actually tested (Windows 11 and Windows 10 22H2 for the capability tests).
