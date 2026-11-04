# 09 - Module Implementation Plan

Milestones stop and report after each: files changed, decisions, build/test results, open problems, security findings.

## 1. Gates

- M0/M1 begin only after the user authorizes the .NET 10 SDK install.
- **No Cleaning/Backup implementation until the M2 safety design package passes independent Opus review and the user explicitly approves.**
- Real-world cleanup steps in M4 only with the user's explicit OK at that moment.

## 2. Order and dependencies

| Milestone | Deliverable | Depends on |
|---|---|---|
| M0 | Ask authorization -> install .NET 10 SDK; `git init`; scaffold solution; central packages; `.gitignore` | User authorization |
| M1 | Phase 1: docs 01-14, design system, shell, real dashboard, Safety Center / History shells (backed by real empty data). STOP | M0 |
| M2 | Safety design package `docs/15-safety-design.md`; independent review; STOP for approval | M1 |
| M3 | Phase 2 implementation + independent diff review. STOP | M2 approval |
| M4 | Completion gate: build, tests, vuln check, kill-test, real-world Subset A/B (approved), screenshots, `phase2-report.md`. STOP; no Phase 3 | M3 |

## 3. Module-by-module plan

| Order | Project / module | Contents | Depends on | Owner |
|---|---|---|---|---|
| 1 | Core | Models, interfaces, result types (frozen before fan-out; safety-critical ones finalized in M2) | - | Lead |
| 2 | Windows | `GetSystemTimes`, `GlobalMemoryStatusEx`, volumes, known folders, Recycle Bin query, `IPrivilegeInfo`; handle primitives (M3) | Core | Lead |
| 3 | Infrastructure | Settings JSON store, SQLite schema/journal/history, Serilog | Core | Lead |
| 4 | Bootstrap | `AddClearPath()` registration | Core + impls | Lead |
| 5 | App (M1) | Design system, shell navigation, Dashboard, Safety Center / History / Settings shells, "Not available" pages | Core, Bootstrap | UI agent |
| 6 | Scanning (M3) | Handle-based read-only scanner, rules, batching to SQLite | Core, Windows primitives via Core interfaces | Lead |
| 7 | Cleaning (M3) | PathGuard, handle-based operations, accounting | Core | Lead |
| 8 | Backup (M3) | Quarantine store, manifests, cross-volume verified copy, `QuarantineRootGuard` | Core | Lead |
| 9 | Recovery (M3) | Startup reconciliation per crash-point table | Cleaning, Backup, Infrastructure via Core | Lead |
| 10 | App (M3) | Quick Clean, confirmation, report, History actions, Safety Center, Settings subset | Frozen Core | UI agent |
| 11 | Tests (continuous) | Per project; adversarial, architecture, CrashHarness | Respective project | Test/docs agent |

## 4. Agent orchestration

| Role | Model | Scope |
|---|---|---|
| Lead (session) | Opus | Core interfaces, Scanning, Windows interop, PathGuard, Cleaning, Backup, recovery, integration, acceptance. Sole owner of safety-critical code |
| UI agent | Sonnet, worktree `ui/*` | WPF views / view models / themes / dashboard against frozen Core; no filesystem mutation; no Core changes; no placeholder data |
| Test/docs agent | Sonnet, worktree `tests/*` | Test projects, adversarial tests, docs, build scripts; read-only review of safety code, never edits it |
| Independent reviewer | Opus, read-only | Reviews M2 package and M3 diff |

Constraints: max 3 active dev agents; no two agents on the same project or file; Core frozen before fan-out; every agent branch merged only after the lead reviews the diff; Haiku only for trivial utilities.

## 5. M1 detail

1. Design system: tokens, typography, spacing, cards / stat tiles / progress; dark and light, persisted.
2. Shell navigation with five working areas and honest "Not available" pages.
3. Dashboard with real data (CPU, RAM, disks, OS, last scan/cleanup, Recycle Bin size).
4. Safety Center and History shells backed by real, empty data.
5. Package compatibility results recorded in [04](04-stack.md).

## 6. M2 detail (`docs/15-safety-design.md`)

1. Final Core interfaces.
2. Handle-operation API design.
3. PathGuard flow.
4. Quarantine/restore state diagrams (Mermaid).
5. Crash-recovery matrix for every transition.
6. Cross-volume metadata policy (full SD, timestamp/ChangeTime findings).
7. Adversarial test plan.
Plus: race-resistant root resolution, handle-relative creation, delete-disposition choice, versioned manifests, Windows support policy.

## 7. M3 / M4 detail

See plan sections 4-6: scanner, Windows interop, PathGuard, Cleaning, Backup, recovery, UI, tests; then the Phase 2 completion gate.
