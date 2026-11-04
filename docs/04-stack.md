# 04 - Technology Stack

## 1. Choice

| Layer | Choice |
|---|---|
| Language / runtime | C# on **.NET 10 LTS** |
| UI | WPF with WPF-UI (Fluent styling) |
| Charts | LiveChartsCore.SkiaSharpView.WPF |
| MVVM | CommunityToolkit.Mvvm |
| Composition | Microsoft.Extensions.Hosting (DI) |
| Persistence | SQLite via Microsoft.Data.Sqlite; JSON for settings |
| Logging | Serilog (file sink) |
| Tests | xunit.v3, Microsoft.NET.Test.Sdk, AwesomeAssertions, NetArchTest.Rules |
| TFM / RID | `net10.0-windows10.0.19041.0`, `win-x64` |

## 2. Why native WPF rather than Tauri or Electron

| Concern | WPF / .NET | Tauri | Electron |
|---|---|---|---|
| Win32 interop (handle-based file ops, `NtCreateFile`, `GetSystemTimes`, `SHQueryRecycleBin`) | Direct P/Invoke in the same process | Needs Rust side or a sidecar; split trust boundary | Needs native addon or sidecar |
| Safety-critical code in one verifiable language | Yes (C#) | Split Rust/JS | Split JS/native |
| Runtime footprint and attack surface | .NET runtime only | WebView2 + Rust | Bundled Chromium |
| Web content in a tool that deletes files | None | WebView | WebView (Chromium) |
| Windows-only product | Fits | Cross-platform benefit unused | Cross-platform benefit unused |

The product is Windows-only and its core is low-level filesystem safety; a single-process native stack keeps that code small and testable. Web-view stacks add an IPC boundary and a large dependency surface without benefit here.

## 3. Why .NET 10 LTS

- .NET 8 LTS reaches **end of support on 2026-11-10** - unacceptable for a new product.
- .NET 10 LTS (GA Nov 2025) is supported to **Nov 2028**.
- .NET 11 is release-candidate only and not a basis for a product.
- SDK install (`winget install Microsoft.DotNet.SDK.10`, 10.0.403 available at planning time) happens **only after explicit user authorization** (M0 gate).

## 4. Pinned packages

Versions are centrally pinned in `Directory.Packages.props`. Stable versions were verified on nuget.org at planning time.

| Package | Version | net10 note |
|---|---|---|
| WPF-UI | 4.3.0 | Targets net8+-windows; expected to load on net10 (forward-compatible TFM) - verify by build + smoke run |
| LiveChartsCore.SkiaSharpView.WPF | 2.0.5 | Stable 2.x; same verification |
| CommunityToolkit.Mvvm | 8.4.2 | netstandard2.1 / net8 - OK |
| Microsoft.Extensions.Hosting | 10.0.14 | Native net10 |
| Microsoft.Data.Sqlite | 10.0.14 | Native net10 |
| Serilog | 4.4.0 | OK |
| Serilog.Sinks.File | 7.0.0 | OK |
| Serilog.Extensions.Hosting | 10.0.0 | OK |
| xunit.v3 | 4.0.2 | OK |
| Microsoft.NET.Test.Sdk | 18.11.0 | OK |
| NetArchTest.Rules | 1.3.2 | OK |
| AwesomeAssertions | 9.6.0 | Replaces FluentAssertions |

### Assertion library decision
FluentAssertions v8 moved to a commercial license. **AwesomeAssertions** (community fork, API-compatible) is used instead to avoid license obligations in a project of unknown commercial status.

## 5. Dependency hygiene

- `NuGetAudit` enabled; **high/critical vulnerabilities are build errors** (warnings-as-errors for those severities).
- `build/check-vulns.ps1` runs `dotnet list package --vulnerable --include-transitive`; part of the completion gate and the release checklist ([14](14-release.md)).
- All versions central; no floating ranges; no per-project version overrides.
- If a package fails on net10 during build, the incompatibility **and its resolution are documented in this file before proceeding** - no silent downgrade.

## 6. App manifest

`asInvoker`. No elevation anywhere in the MVP ([11](11-security-assessment.md)).

## 7. Package compatibility on net10

Status: **all confirmed in M1** (2026-11-05). `dotnet list package --vulnerable --include-transitive`: no vulnerable packages.

| Package | Expected | Status | Evidence / resolution |
|---|---|---|---|
| WPF-UI 4.3.0 | Loads on net10-windows via forward-compatible TFM | **Confirmed** | Release build 0 warnings; app launched, FluentWindow/Mica/NavigationView rendered, all pages navigated (2026-11-05) |
| LiveChartsCore.SkiaSharpView.WPF 2.0.5 | Works on net10 | **Confirmed** | CPU line chart rendered live in smoke run; needed `Background="Transparent"` for dark theme |
| CommunityToolkit.Mvvm 8.4.2 | Works (source generators) | **Confirmed** | Build (generated properties/commands used by all view models) |
| Microsoft.Extensions.Hosting 10.0.14 | Native | **Confirmed** | Build + Bootstrap DI resolution tests (ValidateOnBuild) pass |
| Microsoft.Data.Sqlite 10.0.14 | Native; native SQLite binary resolves for win-x64 | **Confirmed** | Infrastructure tests (schema, WAL, queries) pass; app migrated DB to v1 at startup |
| Serilog 4.4.0 / Sinks.File 7.0.0 / Extensions.Hosting 10.0.0 | Native/compatible | **Confirmed** | Log file written in smoke runs, no ERR/WRN |
| xunit.v3 4.0.2 + Test.Sdk 18.11.0 | Works | **Confirmed** | Requires `global.json` `test.runner = Microsoft.Testing.Platform` on the .NET 10 SDK; 292 tests run |
| NetArchTest.Rules 1.3.2 | Works on net10 assemblies | **Confirmed** | Architecture tests run, incl. a positive control proving the rule can fail |
| AwesomeAssertions 9.6.0 | Works | **Confirmed** | `dotnet test` |
