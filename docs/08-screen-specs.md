# 08 - Screen Specifications

## 1. Global rules

- **No fake data.** Every number comes from a real source; otherwise show "Unavailable", "Measuring...", "No scans yet", or an empty state.
- **Themes:** dark and light, persisted in settings (`theme`). Both themes must be checked for contrast on every screen.
- **Design system:** tokens (color, typography, spacing), cards, stat tiles, progress bars; WPF-UI controls.
- **States** every data screen defines: Loading, Empty, Error, Populated (and Disabled where relevant).
- **No fake controls:** unavailable functions are disabled with a visible reason or absent.
- **Accessibility:** keyboard reachable, visible focus, text not conveyed by color alone, destructive actions never default-focused.

Shell navigation: Dashboard, Quick Clean, Safety Center, History, Settings, plus remaining brief modules as "Not available in this version" entries.

## 2. Dashboard

| Element | Content | States |
|---|---|---|
| CPU tile | % from `GetSystemTimes` | "Measuring..." first sample; "Unavailable" on failure |
| RAM tile | used / total | "Unavailable" on failure |
| Disks | One card per fixed, ready drive: used / free / total, progress bar | Empty: "No fixed drives found"; failure: "Unavailable" |
| OS | ProductName, DisplayVersion, build | "Unavailable" on read failure |
| Last scan / cleanup | From SQLite | "No scans yet" |
| Recycle Bin | Size (display only) | "Unavailable" on failure |
| Quick Scan button | Starts real scanner | Disabled while another operation is active (with reason) |
| Deep Scan button | Disabled; reason shown ("Not available in this version") | Disabled |

No health score, no sparkline of invented history. Polling every 2 s only while the view is visible.

## 3. Quick Clean

Flow: Idle -> Scanning (progress, cancel) -> Results -> Confirm -> Cleaning -> Report.

| Element | Content |
|---|---|
| Category list | Name, size, file count, risk note, include checkbox; admin-only categories visible but disabled with reason |
| Expandable file list | Paged from SQLite; per-file checkbox; "Open containing folder" (Explorer `/select`) |
| Skipped section | Items skipped during scan with reasons (e.g., reparse point root) |
| Actions | Scan, Cancel, Review & Clean (disabled until selection non-empty) |

States: Loading (scanning progress, current rule), Empty ("Nothing eligible found" - real result), Error (rule skipped with reason; capability self-test failure disables scanning with reason), Disabled (another operation active).

## 4. Confirmation dialog

Shows the exact categories, number of files, total bytes, **mode = Quarantine** ("files are moved to quarantine and can be restored"). Shows a note when same-volume quarantine will reclaim 0 bytes. Buttons: Confirm (not default-focused) and Cancel. No "Delete permanently" option here.

**Purge confirmation** (from History): separate dialog stating the action is irreversible, listing item names and sizes, requiring the user to type the confirmation text; the text is recorded in the journal.

## 5. Report

Fields per plan 4.7, shown honestly:

| Field | Notes |
|---|---|
| Logical bytes quarantined | |
| Logical bytes permanently deleted (verified) | Purge only |
| Bytes retained in quarantine | |
| Files skipped by reason | Grouped, expandable |
| Per-volume observed free-space change | Labelled "observed - other processes may affect this" |
| Reclaimed | **0** for same-volume quarantine, with explanation |
| Items needing attention | Link to Safety Center |

States: Populated only after an operation; Error variants for failed items.

## 6. History

| Element | Content |
|---|---|
| Operations list | Kind, mode, state, created/finished |
| Items | Original path, size, state, reason |
| Actions | Restore (Skip / Keep both on conflict - no Replace), Purge (confirmed) |
| Badges | Expired (retention), NeedsAttention |

States: Empty ("No operations yet"), Loading, Error (database unreadable -> message + log link).

## 7. Safety Center

| Section | Content |
|---|---|
| Quarantine | Size, item count, expired count, location |
| Pending recovery | Operations reconciled/awaiting; NeedsAttention items with reasons |
| Manifest status | Discrepancies; explicit "Rebuild manifest" action |
| Privilege | Non-admin (from `WindowsIdentity` + manifest `asInvoker`) |
| Capability self-test | Result of handle-operation self-test; failure disables scanning/cleaning with reason |
| Last scan | From SQLite or "No scans yet" |
| Exclusions | Current list (edited in Settings) |
| Warnings | Irreversible-operation warnings (purge) |

A recovery banner appears app-wide when unresolved items exist.

## 8. Settings

| Setting | Control | Validation |
|---|---|---|
| Theme | Dark / Light | Persisted |
| Exclusions | List with add/remove | External-path validation (PathGuard rules) |
| Retention days | Number | Positive integer; only affects Expired badge |
| Quarantine location | Folder chooser, "Use default" | FIXED drive; not UNC/removable; not in rule root; no silent drive choice |
| Log folder | Link "Open log folder" | - |

No toggles for elevation, auto-purge or Replace.

## 9. "Not available in this version" pages

For Browser Privacy, Registry, Startup, Disk Analyzer, Duplicates, Uninstaller, Privacy Protection, Cookie Manager, AI Advisor.

- Title, one-sentence description of the planned module, and "Not available in this version".
- No buttons, toggles, sample results or progress.
- May state the planned safety constraint (e.g., "will never delete saved passwords") as a commitment, not as an implemented feature.
