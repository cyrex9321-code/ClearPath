# 01 - Product Requirements (PRD)

## 1. Vision

A Windows maintenance application whose first priority is that it never loses user data. Cleaning is reversible by default (quarantine, not deletion), every action is explained before it happens, and everything shown on screen comes from the real system - no fabricated scores, history or "expected results".

Working name: ClearPath.

## 2. Users

| User | Need |
|---|---|
| Everyday Windows 11 user | Reclaim space from safe, regenerable caches without risk of breaking apps or losing files |
| Cautious / technical user | See exactly which files, why they are safe, and be able to undo |
| Maintainer / reviewer | Auditable behaviour: journal, history, logs, deterministic recovery |

## 3. Goals and non-goals

### Goals (MVP)
- Real-time dashboard with honest system data (CPU, RAM, disks, OS).
- Scan an explicit allow-list of user-owned cache locations, read-only.
- Review, confirm, then **quarantine** selected files (not delete).
- Restore from quarantine without ever overwriting existing files.
- Persistent history and honest space accounting.
- Crash-safe: kill the process at any point, relaunch, and no user data is lost.
- Run as a standard (non-elevated) user.

### Non-goals (MVP)
- Registry cleaning, browser cleaning, startup management, driver updates, AI features.
- Admin elevation of any kind.
- Windows Temp, Windows Update cache, Delivery Optimization cache.
- Emptying the Recycle Bin (size display only).
- Automatic or scheduled purging.
- Any "health score", fabricated trend data or marketing claims.
- Use of CCleaner branding, assets or code (see [03](03-reference-repo-analysis.md)).

## 4. Scope: MVP vs later

| Phase | Scope | Status |
|---|---|---|
| 1 | Foundation: solution, design system, shell, real dashboard, Safety Center/History shells | MVP |
| 2 | Quick Clean (6 rules), scanner, PathGuard, quarantine, restore, purge, journal/recovery, history, report, settings subset | MVP |
| 3+ | See [13-roadmap.md](13-roadmap.md) | Later |

After Phase 2 the project **stops for review**. No Phase 3 work starts without a new approval.

## 5. Modules from the original brief

| ID | Module | Disposition | Notes |
|---|---|---|---|
| A | Dashboard | **MVP** | Real data only; Quick Scan wired; Deep Scan disabled with reason |
| B | Junk Cleaner | **MVP** | Delivered as Quick Clean: allow-listed rules, quarantine, restore, purge |
| C | Browser Privacy | Later | Never deletes saved passwords |
| D | Registry | Later | Opt-in, `.reg` backup before any change |
| E | Startup | Later | Never disables security services |
| F | Disk Analyzer | Later | Read-only analysis |
| G | Duplicates | Later | SHA-256; never auto-deleted |
| H | Uninstaller | Later | Lists and launches vendor uninstallers |
| I | Privacy Protection | Later | Opt-in toggles only |
| J | Cookie Manager | Later | Never deletes passwords |
| K | AI Advisor | Later | Advisory only; no autonomous actions |

Later modules appear in the shell as honest "Not available in this version" pages with no fake controls or data ([08](08-screen-specs.md)).

## 6. Functional requirements (MVP summary)

Detailed, testable criteria are in [02-feature-specs.md](02-feature-specs.md).

1. Dashboard shows CPU, RAM, fixed-disk usage, OS, last scan/cleanup, or an explicit "Unavailable" / "No scans yet" state.
2. Quick Clean scans only the allow-listed rules, user-owned, with minimum ages and risk notes.
3. Every cleanup is preceded by a confirmation listing exact categories, files and bytes; mode is Quarantine.
4. Quarantined items can be restored (Skip or Keep both on conflict; no Replace).
5. Purge (permanent delete) only from quarantine, only with explicit irreversible confirmation.
6. History lists operations and items, with Expired badges (retention expiry never purges automatically).
7. Safety Center surfaces quarantine state, pending recovery, privilege level, capability self-test result, exclusions.
8. Settings: theme (dark/light), exclusions, retention days, quarantine location, log folder link.

## 7. Non-functional requirements

| Area | Requirement |
|---|---|
| Safety | At least one hash-verified copy of user data exists in every journal state and through recovery (except documented purge) |
| Privilege | `asInvoker` manifest; no elevation; admin-only categories visible but disabled |
| Platform | Windows 11 23H2+ x64 primary; Windows 10 22H2 compatibility-qualified ([14](14-release.md)) |
| Responsiveness | Scans cancellable per entry; UI never blocks on filesystem work |
| Privacy | No telemetry; no network access in MVP |
| Honesty | No fabricated data; failures show "Unavailable" and are logged |
| Supply chain | Pinned central packages; `NuGetAudit` on; vulnerability check script |

## 8. Success criteria

Success is binary and verifiable (Phase 2 completion gate, plan section 6):

- `dotnet build -c Release` clean; `dotnet test` all green; vulnerability check clean.
- App runs non-admin; dashboard CPU/RAM/disk agree with Task Manager.
- Process killed mid-cleanup (sandbox data), relaunched: recovery reconciles and nothing is lost.
- Real-world Subset A (quarantine -> restore -> verify hashes) and Subset B (quarantine -> explicit purge -> verify removal) performed **only** with the user's explicit approval at that moment.
- Same-volume quarantine reports reclaimed = 0 (honest accounting).
- Screenshots and `docs/phase2-report.md` delivered with limitations and package compatibility notes.

## 9. Risks (summary)

See [11-security-assessment.md](11-security-assessment.md). Principal product risks: data loss from races/crashes (mitigated by handle-based operations and journal), over-aggressive cache deletion (mitigated by allow-list, min ages, quarantine), library compatibility on .NET 10 (mitigated by build gate in M0/M1).
