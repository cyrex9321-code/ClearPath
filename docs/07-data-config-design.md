# 07 - Data and Configuration Design

## 1. File locations

All ClearPath-owned data lives under `%LOCALAPPDATA%\ClearPath`. This tree is on PathGuard's deny-list: ClearPath never cleans its own data.

```
%LOCALAPPDATA%\ClearPath\
  data\            settings.json, clearpath.db (+ -wal, -shm)
  logs\            Serilog rolling files (folder configurable: logFolder)
  Quarantine\      default quarantine root (configurable: quarantineLocation)
    <opId>\        per-operation directory
      <seq>.bin            quarantined file
      <seq>.partial        in-flight cross-volume copy (op-specific, CREATE_NEW)
      manifest.<rev>.json  immutable versioned manifests
  selftest\        sandbox for the startup handle-operation capability self-test
```

| Path | Writer | Notes |
|---|---|---|
| `data\` | Infrastructure | Settings JSON, SQLite DB |
| `logs\` | Infrastructure (Serilog) | No file contents or secrets logged |
| `Quarantine\` | Backup (via `QuarantineRootGuard`) | Default; alternative only by explicit user choice, FIXED drive, never inside a rule root |
| `selftest\` | Capability self-test | Created/removed by the test; never touches user files |

Every directory/file ClearPath creates is created relative to a validated parent handle (no check-then-create) - see [11](11-security-assessment.md).

## 2. Settings JSON

File: `%LOCALAPPDATA%\ClearPath\data\settings.json`. Written atomically (temp file + handle rename). Settings are validated on load; invalid values fall back to defaults and are logged.

```json
{
  "theme": "dark",
  "exclusions": [],
  "retentionDays": 0,
  "quarantineLocation": null,
  "logFolder": null
}
```

| Key | Type | Meaning |
|---|---|---|
| `theme` | `"dark"` \| `"light"` | Persisted UI theme |
| `exclusions` | string[] | Absolute paths PathGuard must never touch (external-path validation applies) |
| `retentionDays` | integer | Days after which quarantined items get an **Expired** badge; never triggers purge |
| `quarantineLocation` | string \| null | `null` = default. Otherwise validated: FIXED drive, not UNC/removable/network, not inside a rule root, not overlapping data dirs |
| `logFolder` | string \| null | `null` = default `logs\`. Settings shows a link to open the folder |

Notes: the JSON above is a shape example; the placeholder `retentionDays` value (0) is **not** a chosen default. The actual default retention period is an open decision to be fixed in M1 (the plan specifies no number). `settings_kv` in SQLite may hold internal state (for example active manifest revision pointers) and is separate from user settings JSON.

## 3. SQLite database

File: `data\clearpath.db`. Pragmas: `journal_mode=WAL`, `synchronous=FULL`. SQLite is the **sole source of truth**; manifests never override it.

State is written **before and after** every filesystem step.

### operations
| Column | Type | Notes |
|---|---|---|
| id | TEXT (GUID) PK | |
| kind | TEXT | quarantine / restore / purge |
| mode | TEXT | e.g. Quarantine |
| state | TEXT | operation state |
| created | TEXT (UTC ISO-8601) | |
| finished | TEXT NULL | |

### operation_items
| Column | Type | Notes |
|---|---|---|
| op_id | TEXT FK -> operations | |
| seq | INTEGER | PK (op_id, seq) |
| state | TEXT | Pending, Validated, Moving, Quarantined, Copying, Copied, Verified, Committed, SourceRemoved, RestoreValidated, RestoreMoving, RestoreCopying, RestoreCopied, RestoreVerified, RestoreCommitted, Restored, Purging, Purged, Skipped, Failed, NeedsAttention |
| error | TEXT NULL | Reason for Skipped / Failed / NeedsAttention |
| updated | TEXT | |
| source_path | TEXT | |
| src_volume_serial | INTEGER | |
| src_file_id128 | BLOB(16) | 128-bit file ID |
| src_size | INTEGER | |
| src_creation_time, src_lastwrite_time, src_lastaccess_time, src_change_time | INTEGER | FILETIME ticks, captured via `FileBasicInfo` before hashing/copying |
| src_attributes | INTEGER | |
| src_sd_hash | BLOB/TEXT | Hash of canonical security descriptor |
| original_sha256 | BLOB(32) | Stored **before** any mutation |
| quarantine_path | TEXT | |
| q_volume_serial | INTEGER | |
| q_file_id128 | BLOB(16) | Same-volume rename: preserved ID. Cross-volume: new ID after creation + verification |
| restore_path | TEXT NULL | |
| r_volume_serial | INTEGER NULL | |
| r_file_id128 | BLOB(16) NULL | Identity of restore temp / final |
| metadata_verification_json | TEXT | Cross-volume metadata verification result |

Recovery verifies path, volume serial, file ID and SHA-256 wherever applicable; a file is never identified by pathname alone.

### Other tables
| Table | Columns (as planned) | Notes |
|---|---|---|
| scans | id, kind, started, finished, state (and aggregate totals) | Exact columns finalized in M1/M2 |
| scan_items | scan id, rule, path, size, mtime, attributes, volume serial, file ID (128-bit) | Records opened read-attributes only; paged by UI |
| volume_measurements | op_id, volume, free_before, free_after | `GetDiskFreeSpaceEx` before/after, after all handles are closed |
| settings_kv | key, value | Internal key/value state |
| active_operation | single-row marker | Used with the single-instance mutex to reject concurrent operations (listed in plan section 2) |

Columns for `scans`, `scan_items` and `active_operation` beyond those named are **not specified in the frozen plan**; they are finalized in M1/M2 (`15-safety-design.md`) and must not contradict the plan.

### Manifests
- `manifest.<rev>.json` per quarantine operation: immutable, versioned, created with `FILE_CREATE` under the validated op directory handle, written and flushed; SQLite then records the active revision.
- Old revisions are never overwritten; a crash mid-write leaves an orphan revision ignored by the journal.
- Mismatch / missing / corrupt manifest => reported in Safety Center; regenerated from the journal only via explicit "rebuild manifest" after filesystem state is independently verified.

## 4. Space accounting data (`CleanupResult`)

Logical bytes quarantined; logical bytes permanently deleted (verified); bytes retained in quarantine; skipped counts by reason; per-volume observed free-space change; discrepancy note. Persisted via `operations` / `operation_items` / `volume_measurements`.
