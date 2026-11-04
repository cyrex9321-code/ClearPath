# ClearPath documentation

ClearPath is a Windows 11 system cleaner and maintenance tool (C# / .NET 10 / WPF). These documents are the M1 planning deliverables. The approved, frozen plan is the single source of truth; where a document and the plan disagree, the plan wins and the document is a bug.

Scope of the current approved work: **Phase 1 (foundation + dashboard) and Phase 2 (safe scanning, quarantine, restore, history, reporting) only.** Everything else is documented as "later".

| # | Document | Purpose |
|---|---|---|
| 01 | [Product requirements](01-prd.md) | Vision, users, goals, MVP vs later, modules A-K |
| 02 | [Feature specs](02-feature-specs.md) | Per-module behaviour and acceptance criteria |
| 03 | [Reference repo analysis](03-reference-repo-analysis.md) | Why `ChenZuren/ccleaner-pro-plus` is feature-checklist only |
| 04 | [Stack](04-stack.md) | Technology choices, pinned packages, compatibility status |
| 05 | [Architecture](05-architecture.md) | Project graph, mutation ownership, runtime flow |
| 06 | [Folder structure](06-folder-structure.md) | Repository layout |
| 07 | [Data and config design](07-data-config-design.md) | SQLite schema, settings JSON, file locations |
| 08 | [Screen specs](08-screen-specs.md) | UI screens, states, themes |
| 09 | [Module plan](09-module-plan.md) | Implementation order and dependencies (M0-M4) |
| 10 | [Interface specs](10-interface-specs.md) | Core interface contracts (some DRAFT) |
| 11 | [Security assessment](11-security-assessment.md) | Threat model, mitigations, residual risks |
| 12 | [Test strategy](12-test-strategy.md) | Test projects, categories, sandbox rules |
| 13 | [Roadmap](13-roadmap.md) | Milestones and later phases |
| 14 | [Release strategy](14-release.md) | Platforms, packaging, signing, checklist |

Planned later documents (not yet written):

- `15-safety-design.md` - M2 safety design package (final interfaces, handle-operation API, state diagrams, recovery matrix, cross-volume policy, adversarial test plan). Required and independently reviewed before any Cleaning/Backup code.
- `phase2-report.md` - M4 completion report.

## Conventions

- "MVP" means Phase 1 + Phase 2 as approved. "Later" means Phase 3 and beyond; nothing in "later" is implemented or promised for a date.
- No document contains performance numbers, user counts or other metrics that have not been measured.
- Status markers: **DRAFT** = contract not final; **TBC** = to be confirmed by a build or test.
