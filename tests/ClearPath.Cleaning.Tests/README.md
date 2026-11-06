# ClearPath.Cleaning.Tests

Adversarial / integration tests for `PathGuard`, `Cleaner` and `RecoveryService` (docs/15 safety design,
§10 adversarial test plan). All tests run against **real native handles** - no mocks for
`IFileSystemReader`/`IFileSystemWriter`/`IQuarantineStore`.

## Sandboxing choice

`PathGuard`'s rule roots are fixed to the real `%TEMP%` and `%LOCALAPPDATA%` (`RuleRoot.UserTemp` /
`RuleRoot.LocalAppData`) - there is no way to point a `CleanupRule` at an arbitrary sandbox path outside
those. This suite uses **both** options described in the M3 test brief, chosen per test:

- **(a) Scoped rule root.** `tests/Shared/ClearPathHarness.cs` builds a custom, mutable `IRuleCatalog` whose
  rules use `RuleRoot.UserTemp` with a `RootSubPath` that always points *into* the harness's own sandbox
  (`%TEMP%\ClearPathTests\<guid>\scanroot\...`). `PathGuard.ResolveRuleRoot`'s real, unmodified logic runs
  (the resolved root is always strictly inside the real `%TEMP%`, so nothing is special-cased), but the root
  it resolves to is scoped to the sandbox, so tests never scan or touch real temp files. Used for
  `Cleaner`/`Scanner` end-to-end tests (quarantine/restore/purge/recovery through the full pipeline).
- **(b) Direct component tests.** For `PathGuard.ValidateForMutation` adversarial cases (symlink, junction
  ancestor swap, replaced/changed-since-scan, hard link, locked, read-only/system, outside-root), tests open
  a `ResolvedRoot`/`ScanItem` **by hand** via `IFileSystemReader.OpenChain` directly against a sandbox
  directory, bypassing rule resolution entirely (`ClearPathHarness.OpenResolvedRoot`/`MakeScanItem`). This
  lets every adversarial PathGuard case run against the real native code with no dependency on which
  rule roots exist.

## Quarantine location

The quarantine location can never be inside `%TEMP%` (`QuarantineStore.OpenRoot` rejects that - it is the
"user-temp" rule root, by design). This suite points it at a second sandbox,
`%LOCALAPPDATA%\ClearPathTestsQuarantine\<guid>`, the only directory the suite creates outside `%TEMP%`.
`ClearPathHarness.CreateAsync` verifies this sandbox round-trips a marker file at its exact path immediately
after creation; if that fails (e.g. this directory was virtualized by MSIX AppData redirection applied to
the test process), the harness throws `QuarantineSandboxUnavailableException` and the affected tests call
`Assert.Skip` with that message rather than failing or silently passing.

## Matrix

Each destructive operation (quarantine, restore, purge) is tested across: normal, invalid input, concurrent
modification/race, cancellation, recovery, and integrity verification. Access-denied and process-termination
cells of the full docs/15 matrix are **not** separately covered here (see the top-level test report for
what was skipped and why - mainly because a non-admin test process cannot reliably construct an
access-denied native handle failure without an elevated second process, and true process-kill testing is
`tools/m3-smoke-test`/a `CrashHarness`, not unit/integration tests).

## Safety

Every test in this project uses a sandbox under `%TEMP%\ClearPathTests\<guid>` (deleted in `DisposeAsync`)
plus, where a quarantine location is needed, `%LOCALAPPDATA%\ClearPathTestsQuarantine\<guid>` (also deleted).
No test ever points a rule root, exclusion, or quarantine location at a real user file outside these
sandboxes. `ClearPathHarness.AssertNotElevated()` is asserted on every harness creation.
