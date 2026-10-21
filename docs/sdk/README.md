# DarkCipher AgentControl SDK 1.1

Build extensions that add stack templates, model catalogues, commands and dashboard panels to DarkCipher AgentControl.
The SDK is the assembly `DarkCipher.AgentControl.Sdk` (semantic version 1.1.0; 1.0 extensions still load). It has no dependency on application
internals, and the loader refuses extensions that reference them.

Status:
- **Implemented and verified**: declarative extensions, and code-extension loading in Development Mode (tested end to end with the samples).
- **Planned**: out-of-process isolation, agent-provider and runtime registration through the SDK, publisher signing.

## 1. Overview and architecture
An extension is a `.dcxext` package (a zip) containing:
- `extension.json`: the manifest.
- `files.sha256`: the integrity list. Every file must be listed, and nothing unlisted may be present.
- Contributed data files: stack templates and model catalogues (JSON).
- Code extensions only: one or more `.dll` files and an `entryPoint`.

The host flow:
1. Validates the manifest with the same rules as `dcx-ext validate`.
2. Verifies integrity.
3. Shows a pre-install review: permissions with meanings and risk, network endpoints, trust, isolation.
4. Installs atomically.
5. Re-verifies integrity at every start.
6. Loads the extension according to policy.

## 2. Security model
- **Least privilege.** Request only the permissions you need. Users may grant fewer, and the broker enforces the grant on every call (`CapabilityDeniedException` plus an audit entry).
- **Isolation tiers.**
  - **Declarative** extensions run no code (Enforced).
  - **Code** extensions run in-process in a collectible AssemblyLoadContext. They are **not isolated**, so they load only when the user turns on Development Mode, and a warning stays visible.
- **No secrets.** Extensions receive `SdkCredentialHandle` references, and only for credentials the user created for that extension (`kind = extension:<id>`). Values are never exposed.
- **Processes.** `RunApprovedProcessAsync` runs through the host's secure executor, evaluated as an *extension*:
  - Stricter protected-path rules.
  - Captured output, no window.
  - At most 60 s.
  - Shells refused, approvals never granted.
- **Network.** Declare every endpoint in `networkEndpoints` (https, or http on loopback) together with the `Network` permission. No SDK network API is exposed in 1.0.
- **Trust levels.** Built-In, Verified, Signed, Community, Unsigned, Development, Blocked, Incompatible. With no trusted publisher keys configured, all third-party packages are Unsigned or Community.
- **User switches.** "Disable third-party extensions" (default ON) and `--safe-mode` stop all non-built-in extensions.

## 3. Manifest reference (`extension.json`)
Schema: [`schemas/extension-manifest.schema.json`](../../schemas/extension-manifest.schema.json).

| Field | Required | Rules |
|---|---|---|
| `id` | yes | Reverse-DNS, lower case (`com.example.my-ext`). The prefixes `darkcipher.` and `com.darkciphersystems.` are reserved. |
| `name`, `publisher` | yes | 1–80 characters. DarkCipher publisher names are reserved (anti-spoofing). |
| `version`, `sdkVersion`, `minAppVersion` | yes | `MAJOR.MINOR.PATCH`. |
| `maxTestedVersion` | no | Semver. The UI warns when the app is newer. |
| `description` | no | ≤ 1000 characters. |
| `entryPoint` | code only | `Assembly.dll!Namespace.Type`. The type implements `IExtension`. |
| `types` | yes | One or more of: AgentProvider, Runtime, ModelSource, HardwareDetector, Gateway, StackTemplate, RecommendationProvider, CompatibilityTester, ProjectIntegration, ObservabilityIntegration, DashboardPanel, Command, Importer, Exporter. |
| `permissions` | yes (may be empty) | Network, FileReads, ProjectReads, ProjectWrites, ProcessExecution, ModelManagement, RuntimeManagement, CredentialReferences, UI, Settings, Logging, Hardware, Sessions. |
| `networkEndpoints` | with Network | https URLs, or http on loopback. |
| `dependencies` | no | `{ "id", "minVersion" }`. |
| `license` | yes | SPDX id, or `Proprietary`. |
| `source` | no | https URL. |
| `signature` | no | Reserved. Not verified in 1.0 (no publisher key infrastructure yet). |
| `contributes.stackTemplates` | no | Relative paths to JSON files (array of `SdkStackTemplate`, max 20 per file, 64 KB). |
| `contributes.modelCatalogs` | no | Relative paths to JSON catalogue files (validated; surfaced read-only). |

## 4. Permissions reference

| Permission | Allows | Risk |
|---|---|---|
| Network | Contacting declared endpoints | Medium |
| FileReads | Reading files the host hands over | Medium |
| ProjectReads / ProjectWrites | Project file access (reserved; no 1.0 API) | High |
| ProcessExecution | `RunApprovedProcessAsync` | High |
| ModelManagement | `GetInstalledModelsAsync`, `SubmitModelDownloadAsync` | Low |
| RuntimeManagement | Reserved | Medium |
| CredentialReferences | `GetCredentialReference` | Medium |
| UI | `RegisterCommand`, `RegisterDashboardPanel` | Low |
| Settings | `GetSetting` / `SetSetting` (scoped to the extension; 100 keys, 4 KB values) | Low |
| Logging | `Log` (redacted by the host) | Low |
| Hardware | `GetHardwareSummaryAsync` | Low |
| Sessions | Reserved | Medium |
| CatalogueContributions (1.1) | `RegisterAgentTool`, `RegisterModelSource` (also needs Network), `RegisterCompatibilityRule`. Data only: the host performs every install with winget after the user approves a plan; contributed tools are Unverified. | Medium |

`RegisterStackTemplate` and (1.1) `RegisterTemplate` need no permission: templates are data, and the host resets their permission profile to Ask Before Changes. `RegisterHealthCheck` (1.1) needs UI; `RegisterSettingsPanel` (1.1) needs Settings.

## 5. Lifecycle
- **States**: Discovered → Validated → Installed → (Disabled | Loading → Ready | Degraded | Failed) → Removed. Blocked and Incompatible are terminal until you fix the cause.
- **Loading**:
  - `InitializeAsync` must finish within 10 s.
  - Exceptions mark the extension Failed. Three consecutive failed starts auto-disable it.
  - Tampered files mark it Blocked.
  - `ReportHealth` lets an extension report Degraded or Failed itself.

## 6. Versioning, compatibility and deprecation
- SDK 1.x is additive only. An extension built for 1.x loads on any host with SDK 1.y where y ≥ x. A major bump breaks compatibility; hosts refuse other majors.
- Deprecated members stay for at least one minor version, are marked `[Obsolete]`, and are listed in release notes before removal at the next major.
- Negotiation at load:
  - The manifest `sdkVersion` and the assembly's SDK reference are both checked.
  - `minAppVersion` is enforced.
  - `maxTestedVersion` produces a warning.

## 7. API reference (summary)
- `IExtension.InitializeAsync(IExtensionContext, CancellationToken)`.
- `IExtensionContext`: `ExtensionId`, `HostVersion`, `HostSdkVersion`, `GrantedPermissions`, `Broker`.
- `ISdkCapabilityBroker`:
  - `RegisterStackTemplate`, `RegisterCommand`, `RegisterDashboardPanel`.
  - `GetInstalledModelsAsync`, `GetHardwareSummaryAsync`.
  - `RunApprovedProcessAsync`, `SubmitModelDownloadAsync`.
  - `GetCredentialReference`, `GetSetting`, `SetSetting`.
  - `Log`, `ReportHealth`.
  - 1.1: `RegisterTemplate(string json)` (schema 2, validated, unknown fields refused), `RegisterAgentTool(SdkAgentToolEntry)`, `RegisterModelSource(ISdkModelSource)`, `RegisterCompatibilityRule(SdkCompatibilityRule)`, `RegisterHealthCheck(SdkHealthCheck)` (5 s, isolated), `RegisterSettingsPanel(SdkSettingsPanel)`.
- 1.1 records: `SdkAgentToolEntry`, `SdkModelSearchResult`, `SdkModelFile`, `SdkModelDetails`, `SdkCompatibilityRule` (effect `warn` or `block`, `*` wildcards), `SdkHealthCheck`, `SdkHealthResult`, `SdkSettingsPanel`, `SdkSettingField`; interface `ISdkModelSource`.
- Not offered (by design): runtime, installation and package-verification providers from extensions. Code extensions run in-process without isolation, so the host keeps every install and verification step itself.
- Records: `SdkStackTemplate`, `SdkCommand`, `SdkDashboardPanel`, `SdkModelInfo`, `SdkProcessRequest`, `SdkProcessResult`, `SdkCredentialHandle`.
- Enums: `ExtensionType`, `ExtensionPermission`, `SdkLogLevel`, `SdkHealth`.

## 8. Tutorials
**A stack template (no code):**
1. `dcx-ext new stack-template my-stacks`
2. Edit `templates/stacks.json`.
3. `dcx-ext validate my-stacks`
4. `dcx-ext pack my-stacks my-stacks.dcxext`
5. In the app: Security → turn off "Disable third-party extensions", then Extensions → Install from file.

**A dashboard panel (code):** see `samples/dashboard-panel`.
1. Reference the SDK with `Private=false` so the host supplies it.
2. Implement `IExtension`.
3. Call `broker.RegisterDashboardPanel(...)`.
4. Build, then `dcx-ext pack bin/Debug/net8.0 out.dcxext`.
5. Turn on Development Mode to load it.

## 9. Testing
- `dcx-ext validate`, `analyse` and `check-compat` run locally.
- The repository's test suite shows the mock-host pattern: `FakeServices`, `MemoryStore`, `FixedPolicy` and `MemoryAudit` in `tests/.../Extensions`. `CodeExtensionTests` loads the compiled samples through the real host.
- Contract tests (`SdkContractTests`) pin the public surface: only interfaces, records, enums, static classes and exceptions; records are init-only.

## 10. Packaging
- `dcx-ext pack` copies only the manifest, contributed files and (for code) top-level DLLs, excluding the SDK.
- It generates `files.sha256` in a staging copy (the source folder is never modified), zips the result, and re-verifies it exactly as the host would.

## 11. Signing
**Not implemented.** The manifest has a `signature` field, but no publisher key infrastructure exists yet. Signed packages are treated as unsigned and labelled as such. Planned: ECDSA publisher keys, with a trust store managed in Security.

## 12. Debugging
- Use Development Mode, the Logs screen (component `ext:<id>`), the Extensions screen state and reason, and the Security → Audit log (capability denials).
- An extension that fails to start shows the exception message there.

## 13. Publishing
There is no public catalogue yet (Planned). Distribute `.dcxext` files together with their SHA-256 (printed by `pack`), so users can compare them before installing.
