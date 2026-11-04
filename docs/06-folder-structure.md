# 06 - Folder Structure

Repository root: `<project-root>\`

```
ClearPath/
  src/
    ClearPath.Core/            models, interfaces (IScanner, ICleaner, IQuarantineStore, IOperationJournal,
                               ISystemMetrics, IVolumeInfo, IPathGuard, ISettingsStore, ...), result types
    ClearPath.Scanning/        -> Core   read-only enumeration
    ClearPath.Cleaning/        -> Core   PathGuard + handle-based source-file ops + accounting
    ClearPath.Backup/          -> Core   quarantine store, manifests, verified copies (IQuarantineStore impl)
    ClearPath.Windows/         -> Core   P/Invoke: metrics, volumes, known folders, Recycle Bin query,
                                         handle-operation primitives
    ClearPath.Infrastructure/  -> Core   settings JSON, SQLite journal/history, Serilog
    ClearPath.Bootstrap/       -> Core + all impl projects   DI composition root (AddClearPath())
    ClearPath.App/             -> Core + Bootstrap           WPF views / viewmodels only
  tests/
    ClearPath.Core.Tests/
    ClearPath.Scanning.Tests/
    ClearPath.Cleaning.Tests/
    ClearPath.Backup.Tests/
    ClearPath.Windows.Tests/
    ClearPath.Infrastructure.Tests/
    ClearPath.Bootstrap.Tests/
    ClearPath.Architecture.Tests/
    ClearPath.CrashHarness/    console app (not a test project): kills itself mid-cleanup, asserts recovery
  docs/                        planning deliverables 01-14 (+ 15-safety-design.md in M2)
  build/                       scripts, e.g. check-vulns.ps1
  Directory.Packages.props     central package versions
  Directory.Build.props        shared TFM, analyzers, NuGetAudit settings
  .gitignore                   includes CLAUDE*.md, bin/, obj/
```

## Notes

- Project reference rules and mutation ownership: [05](05-architecture.md).
- `CLAUDE.md` and `CLAUDE-*.md` are excluded from version control.
- Exact test project names above follow the plan's list (Core, Scanning, Cleaning, Backup, Windows, Infrastructure, Bootstrap, Architecture, CrashHarness); the `.Tests` suffix convention is applied for the xunit projects.
- Runtime data is **not** in the repository; see [07](07-data-config-design.md).
