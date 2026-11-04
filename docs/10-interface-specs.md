# 10 - Interface Specifications (Core)

Contract-level descriptions of the interfaces in `ClearPath.Core`. These are not final signatures. Method names are illustrative; the semantics, ownership and error behaviour are the contract.

Conventions for all interfaces:
- Failures that are expected (access denied, file locked, changed since scan) are returned as **result values with a reason**, not thrown, so callers can record `Skipped(reason)` / `Failed(error)`.
- Async operations accept a `CancellationToken`; cancellation is checked per entry/item.
- Interfaces perform no UI work. Implementations must not reference other implementation projects.

## 1. Stable contracts (used in M1)

### ISystemMetrics
Provides real-time system metrics. Implemented in Windows.
- CPU usage sample: returns percent in [0, 100] or "measuring" for the first sample; computed from `GetSystemTimes` deltas (kernel includes idle) with the interval measured by `Stopwatch`.
- Memory: total / used from `GlobalMemoryStatusEx`.
- OS description: ProductName, DisplayVersion, build from the `CurrentVersion` registry key (build >= 22000 => Windows 11).
- Failure: returns an "unavailable" result, never a made-up value; failure is logged.

### IVolumeInfo
Lists fixed, ready volumes and reports per-volume total/free bytes (`DriveInfo` / `GetDiskFreeSpaceEx`). Also used for before/after free-space measurement in accounting. Read-only.

### IRecycleBinInfo
Returns Recycle Bin size and item count via `SHQueryRecycleBin`. Query only; the interface has **no** empty/delete method in the MVP.

### IPrivilegeInfo
Reports whether the process token is elevated, the user identity, and whether specific privileges (for example `SeSecurityPrivilege`) are enabled. Read-only. Used by Safety Center and by the cross-volume SACL decision.

### ISettingsStore
Loads and saves the settings document ([07](07-data-config-design.md)): Theme, Exclusions, QuarantineRetentionDays (default 30), QuarantineLocation. Validates on load (invalid => defaults + log). Saves atomically. Raises a change notification. Writes only inside the ClearPath data directory.

### IHistoryRepository
Read/write access to operations, items, scans and volume measurements in SQLite. Provides paged item queries (full lists are never held in memory), last scan/cleanup lookup (or "none"), and expired-item queries. Does not itself mutate user files.

## 2. Safety contracts - DRAFT - finalized in M2 `docs/15-safety-design.md`

> **DRAFT.** The following contracts are placeholders for scheduling and dependency design only. No Cleaning/Backup code is written against them until the M2 package is independently reviewed and approved.

### IScanner - DRAFT
- Input: a rule (root, filter, minimum age, risk note) and a cancellation token.
- Behaviour: read-only, handle-based enumeration (root validated and opened via directory handle, children opened relative to the parent handle). Requests no write/delete access. Rejects reparse points, verifies file ID and volume serial per entry, discards subtrees whose directory identity changed.
- Output: streamed batches of scan records (path, size, mtime, attributes, volume serial, 128-bit file ID) plus skip records with reasons; category aggregates.
- Failure: per-directory / per-file isolation; capability self-test failure => scanning disabled with reason (no path-based fallback).

### ICleaner - DRAFT
- Input: a set of scan record references selected by the user and an operation mode (MVP: Quarantine; Restore; Purge with authorization).
- Behaviour: for each item, PathGuard validation, share-none handle open, validation against the scan record, hash-before-mutate, then the state machine in plan 4.6; all source mutation through the validated handle; never overwrites; honours cancellation (finish current item, remaining Skipped("cancelled")).
- Output: `CleanupResult` with the accounting fields in [02](02-feature-specs.md) section G and per-item outcome.
- Sole authority for mutating original user files.

### IQuarantineStore - DRAFT
- Behaviour: provides the quarantine root, free-space/volume identity checks, creation of per-operation directories, acceptance of verified content (same-volume rename target or cross-volume copy destination), manifest revision writing, removal of purged/partial artifacts. Every write is confined to the quarantine root (`QuarantineRootGuard`).
- Does not touch original source files; interacts with Cleaning only through this interface.

### IOperationJournal - DRAFT
- Behaviour: creates operations/items; records state transitions **before and after** each filesystem step; stores original hash, volume serials, file IDs, restore identities, metadata verification; exposes incomplete operations for startup recovery; enforces the single `active_operation`. SQLite (WAL, `synchronous=FULL`) is the source of truth.
- Failure: if a journal write fails the corresponding filesystem step must not proceed.

### IPathGuard - DRAFT
- Behaviour: validates two input kinds - **external paths** (rule roots, settings, scan records, user input) and **trusted handle paths** (from `GetFinalPathNameByHandle(VOLUME_NAME_DOS)`, accepted only as `\\?\X:\...`) - using the evaluation order in plan 4.3: canonical root resolution, strictly-beneath check, exclusions, deny-list, ancestor reparse-point checks, final handle-based validation.
- Output: allow with structured (drive, components), or deny with a reason code.
- Stateless with respect to the filesystem apart from the handle checks it is given.

## 3. Cross-cutting result types (Core)

| Type | Purpose |
|---|---|
| Result with reason | Success / Skipped(reason) / Failed(error) / NeedsAttention(reason) |
| CleanupResult | Accounting fields (logical bytes quarantined/deleted, retained, skipped by reason, per-volume observed change) |
| Scan record / skip record | Immutable identity snapshot used to detect change |

## 4. Dependency rules for interfaces

- UI consumes interfaces via Bootstrap DI only.
- Cleaning and Backup reference each other only through Core interfaces.
- Windows primitives for handle operations are invoked only by Cleaning and Backup.
- Core stays free of P/Invoke, SQLite, WPF and Serilog types.
