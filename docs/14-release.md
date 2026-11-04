# 14 - Release Strategy

## 1. Platform support

| Platform | Status |
|---|---|
| Windows 11 (23H2+), x64 | **Primary supported platform** |
| Windows 10 22H2 (build 19045) | **Qualified / compatibility-tested only** |
| ARM64, x86, other Windows versions | Not targeted |

Rules for the Windows 10 qualification:
- Compatibility is not security servicing. Security assurances apply only where the OS still receives security updates (for example ESU). An unsupported Windows installation gets no equivalent assurance, regardless of whether ClearPath runs on it.
- The **exact supported editions and versions are fixed at release time** from Microsoft's then-current servicing lifecycle, and recorded in the release notes. This document does not hard-code lifecycle dates.
- The M2 capability tests run on Windows 11 and Windows 10 22H2; behaviour differences are documented in `15-safety-design.md` and unverified combinations are not claimed as supported.

Target: TFM `net10.0-windows10.0.19041.0`, RID `win-x64`.

## 2. Versioning

Semantic Versioning (`MAJOR.MINOR.PATCH`, pre-release tags such as `-alpha.1`). Version defined once (build props) and surfaced in the app and logs. Phase 2 completion is a pre-1.0 build; no 1.0 promise is made here.

## 3. Packaging

| Option | Role |
|---|---|
| MSIX | Primary packaging candidate (clean install/uninstall, per-user, no elevation required) |
| Inno Setup | Optional alternative installer |
| Portable zip | Developer/test convenience only |

Packaging choice is finalized after M4; both must preserve `asInvoker` and must **not** remove `%LOCALAPPDATA%\ClearPath` quarantine data on uninstall without an explicit user choice (it may contain the only copy of user files).

## 4. Code signing

- Sign executables and packages with **Authenticode**; **Azure Trusted Signing** is the preferred service if eligible, otherwise a standard code-signing certificate.
- Timestamp all signatures.
- Signing keys/credentials are never stored in the repository; build secrets are handled by the CI secret store.
- Unsigned builds are marked development builds and not distributed.

## 5. Vulnerability and dependency checks

- `NuGetAudit` enabled; high/critical = build error ([04](04-stack.md)).
- `build/check-vulns.ps1` (`dotnet list package --vulnerable --include-transitive`) must be clean before any release.
- Re-check .NET runtime servicing level and package updates before each release; update pins deliberately via `Directory.Packages.props`.
- Produce a dependency inventory (package names and versions) with each release.

## 6. Release checklist

- [ ] All milestone gates passed; independent review findings resolved (M2, M3).
- [ ] `dotnet build -c Release` clean (warnings reviewed).
- [ ] `dotnet test` all green on Windows 11; capability and compatibility tests run on Windows 10 22H2 where claimed.
- [ ] CrashHarness run passed; recovery verified.
- [ ] `build/check-vulns.ps1` clean.
- [ ] Package compatibility table in [04](04-stack.md) fully resolved (no TBC).
- [ ] Manifest is `asInvoker`; app runs non-admin.
- [ ] Supported OS editions/versions re-checked against Microsoft's current lifecycle and written into release notes.
- [ ] Version number set (SemVer); changelog written with known limitations.
- [ ] Artifacts signed (Authenticode / Trusted Signing) and timestamped; signature verified.
- [ ] Install, upgrade and uninstall tested; uninstall preserves quarantine data by default.
- [ ] Clean-machine smoke test: dashboard values match Task Manager; theme persists.
- [ ] No debug data, test sandboxes or secrets in the package.
- [ ] Documentation (README, docs/) matches the shipped behaviour; no fabricated claims.
- [ ] Hashes (SHA-256) of release artifacts published alongside them.

## 7. Updates and distribution

No auto-update or network access in the MVP. Update mechanism (if any) is a later decision and must not introduce unsigned code paths.
