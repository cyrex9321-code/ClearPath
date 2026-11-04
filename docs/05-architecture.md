# 05 - Architecture

## 1. Project dependency graph

Implementation projects depend on **Core only**. Core holds models, interfaces and result types. Bootstrap is the single composition root; App holds only views and view models.

```mermaid
graph TD
    App[ClearPath.App<br/>WPF views / viewmodels] --> Core[ClearPath.Core<br/>models, interfaces, results]
    App --> Bootstrap[ClearPath.Bootstrap<br/>DI composition root AddClearPath]
    Bootstrap --> Core
    Bootstrap --> Scanning[ClearPath.Scanning<br/>read-only enumeration]
    Bootstrap --> Cleaning[ClearPath.Cleaning<br/>PathGuard + handle-based source ops]
    Bootstrap --> Backup[ClearPath.Backup<br/>quarantine store]
    Bootstrap --> Windows[ClearPath.Windows<br/>P/Invoke primitives]
    Bootstrap --> Infra[ClearPath.Infrastructure<br/>settings, SQLite, Serilog]
    Scanning --> Core
    Cleaning --> Core
    Backup --> Core
    Windows --> Core
    Infra --> Core
```

Rules:
- App references only Core + Bootstrap.
- Scanning, Cleaning, Backup, Windows, Infrastructure reference only Core.
- Cleaning and Backup interact solely through Core interfaces (`IQuarantineStore`); no project reference between them.
- Enforced by architecture tests (NetArchTest + IL call scan), see [12](12-test-strategy.md).

## 2. Project responsibilities

| Project | Responsibility |
|---|---|
| Core | Models, interfaces (`IScanner`, `ICleaner`, `IQuarantineStore`, `IOperationJournal`, `ISystemMetrics`, `IVolumeInfo`, `IRecycleBinInfo`, `IPrivilegeInfo`, `IPathGuard`, `ISettingsStore`, `IHistoryRepository`), result types |
| Scanning | Read-only handle-based enumeration; no mutation calls |
| Cleaning | PathGuard; handle-based operations on original source files; space accounting |
| Backup | Quarantine store, manifests, verified copies; writes only under the quarantine root via `QuarantineRootGuard` |
| Windows | P/Invoke: metrics, volumes, known folders, Recycle Bin query, handle-operation primitives |
| Infrastructure | Settings JSON, SQLite journal/history, Serilog; touches only its own data dir |
| Bootstrap | `AddClearPath()` DI registration |
| App | WPF views and view models only |

## 3. Mutation ownership

| Actor | May mutate | May not mutate |
|---|---|---|
| **Cleaning** | Original user source files: move to quarantine, restore, permanent delete (sole authority) | Anything outside approved rule roots / restore roots |
| **Backup** | Only inside the configured quarantine root: quarantine files, manifests, partial copies, purge of quarantine items; every write through `QuarantineRootGuard` | Original source files |
| **Infrastructure** | Its own data dir only: settings, DB, logs | User files |
| **App, Core, Scanning** | Nothing on the filesystem | - |
| **Windows** | Provides handle primitives, invoked only by Cleaning / Backup | Acts on no path by itself |

Additional constraints:
- Prohibited for source files and quarantine artifacts: `File.Delete/Move`, `Directory.Delete/Move`, `MoveFileEx`, any path-based fallback. Unsupported operation => Skipped("unsupported operation: ...").
- Concurrency: single-instance mutex plus an `active_operation` row in SQLite; a second operation is rejected.
- Manifests are advisory; SQLite is the sole source of truth.

## 4. Runtime flow

```mermaid
flowchart TD
    A[User starts Quick Scan] --> B[Scanner: handle-based read-only enumeration]
    B --> C[Scan results streamed to SQLite<br/>per-category aggregates + top-N]
    C --> D[Review: categories, risk notes, file selection]
    D --> E[Confirmation dialog: exact categories / files / bytes, mode = Quarantine]
    E -->|Cancel| D
    E -->|Confirm| F[Create operation + items in journal]
    F --> G[Per item: PathGuard + open share-none handle + validate vs scan]
    G -->|mismatch / locked / unsupported| S[Skipped with reason]
    G --> H[Hash SHA-256 through handle, journal BEFORE mutation]
    H --> I{Same volume?}
    I -->|yes| J[Handle rename to quarantine<br/>no-replace]
    I -->|no| K[Verified copy: partial, flush, re-hash, rename, then delete source]
    J --> L[Quarantined]
    K --> L
    L --> M[Report: logical bytes, retained, skipped by reason,<br/>observed free-space change; same-volume reclaimed = 0]
    M --> N[History]
    N --> O{User action}
    O -->|Restore| P[Validate, no-overwrite restore, verify hash -> Restored]
    O -->|Purge| Q[Irreversible confirmation -> Purging -> Purged]
    S --> M
```

## 5. Startup recovery flow

```mermaid
flowchart LR
    A[App start] --> B[Mutex + active_operation check]
    B --> C[Capability self-test<br/>%LOCALAPPDATA%\ClearPath\selftest]
    C -->|fails| X[Scanning/cleaning disabled with reason]
    C -->|passes| D[Read incomplete operations from journal]
    D --> E[Inspect source, partial, final, quarantine artifacts<br/>hash-verify everything referenced]
    E --> F{In recovery table?}
    F -->|yes| G[Apply table action<br/>verified deletes only]
    F -->|no| H[Preserve all copies -> NeedsAttention]
    G --> I[Safety Center + recovery banner]
    H --> I
```

## 6. Concurrency and threading

- Scan: bounded parallelism (`Parallel.ForEachAsync`, MaxDOP = min(4, cores)) across rule roots / top-level directories; per-directory and per-file try/catch; cancellation checked per entry.
- Memory: per-category aggregates + top-N in memory; the full list is paged from SQLite.
- Cleaning: sequential per item inside one operation; state written before and after every filesystem step.
- UI: dashboard polling (2 s) only while visible; filesystem work never on the UI thread.

## 7. Design principles

1. Never trust paths: validate, open a handle, then act through that handle.
2. Metadata (ID, size, mtime) detects change; the SHA-256 is the integrity proof.
3. The rename/delete itself is the guarantee (no-replace), never a prior existence check.
4. One verified copy of user data always exists, except in an explicitly confirmed purge.
5. Unknown state => preserve everything and ask.
