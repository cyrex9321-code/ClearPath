# 13 - Roadmap

Only M0-M4 (Phase 1 + 2) are approved. Phases 3-6 are the original brief's later scope: **not approved, not scheduled, no dates**. Each later phase needs its own spec, threat-model update and user approval.

## 1. Approved work (MVP)

| Milestone | Scope | Exit |
|---|---|---|
| M0 | Authorization -> .NET 10 SDK install; `git init`; solution scaffold; central packages | Solution builds; stop + report |
| M1 | Phase 1: docs 01-14, design system, shell, real dashboard, Safety Center / History shells | Build/test results, package compat recorded in 04; stop + report |
| M2 | Safety design package `docs/15-safety-design.md`; independent Opus review; findings addressed | User approval before any Cleaning/Backup code |
| M3 | Phase 2 implementation: scanner, Windows interop, PathGuard, Cleaning, Backup, recovery, UI, tests; independent diff review | Review findings addressed; stop |
| M4 | Completion gate: build/tests/vuln check, kill-test, real-world Subset A/B (with approval), screenshots, `phase2-report.md` | STOP. No Phase 3 |

## 2. Later phases (indicative grouping of the brief's modules)

The grouping below is a proposal for discussion, not a commitment.

| Phase | Modules | Key safety constraints |
|---|---|---|
| 3 | Browser Privacy (C), Cookie Manager (J) | Never delete saved passwords; reuse PathGuard + quarantine; browsers must be closed or files skipped |
| 4 | Startup (E), Registry (D) | Startup: never disable security services. Registry: opt-in, `.reg` backup before any change, verified restorable. May require elevation decisions - separate security review |
| 5 | Disk Analyzer (F), Duplicates (G), Uninstaller (H) | Analyzer read-only; duplicates SHA-256, never auto-deleted; uninstaller launches vendor uninstallers only |
| 6 | Privacy Protection (I), AI Advisor (K) | Opt-in per setting, reversible; AI advisory only, no autonomous actions, explicit consent for any off-device data |

Other deferred items from the plan: Windows Temp, Windows Update cache, Delivery Optimization, Recycle Bin emptying ("revisit after Phase 2 review"), driver updates, admin elevation.

## 3. MVP vs later

| Capability | MVP | Later |
|---|---|---|
| Dashboard, Quick Clean (6 rules), quarantine, restore (Skip / Keep both), purge, history, Safety Center, settings subset | Yes | - |
| Browser/Registry/Startup/Disk/Duplicates/Uninstaller/Privacy/Cookie/AI | No | Phases 3-6 |
| Elevation, Replace-on-restore, auto purge, scheduled cleaning | No | Not planned |

## 4. Dependencies between phases

- Every later module that deletes or moves files must go through PathGuard, handle-based operations and quarantine established in Phase 2.
- Any module needing elevation requires a design review that revisits "no elevation in MVP".
- Distribution (MSIX/Inno Setup, signing) setup is detailed in [14](14-release.md) and is needed before any public release.
