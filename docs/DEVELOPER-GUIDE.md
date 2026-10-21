# Developer guide

## Build and test
```powershell
$env:TEMP = "$env:USERPROFILE\.dcac-dev\tmp"; $env:TMP = $env:TEMP   # scratch outside the repo (see TESTING.md)
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
dotnet build .\DarkCipherAgentControl.sln
dotnet test .\tests\DarkCipher.AgentControl.Tests
.\tools\run-selftest.ps1                             # headless app self-test (scratch data folder, no windows)
.\build.ps1                                          # full release pipeline -> dist\<version>\
```
- **Toolchain**: .NET SDK 8.0.426 (pinned in `global.json`). NuGet packages restore into `.nuget\packages` (`nuget.config`). WiX 5.0.2 is a local tool (`.config\dotnet-tools.json`).
- **Lock files**: `packages.lock.json` per project. `build.ps1` restores in locked mode. After intentionally changing a package, run `dotnet restore` once to update the lock files and commit them.
- **Analyzers**: .NET analyzers are on. Nullable warnings and NuGet audit findings are errors.

## Live tests (optional, against this PC)
```powershell
$env:DCAC_LIVE_OLLAMA = "1"; $env:DCAC_LIVE_MODEL = "gpt-oss-64k"   # suite, gateway probe, model pipeline through the queue (re-pull of an installed model)
$env:DCAC_LIVE_AGENT  = "1"                                          # headless real Claude Code launch + start/stop
$env:DCAC_LIVE_WINGET = "1"                                          # winget through the secure executor, source lookups, install PLANS (installs nothing)
dotnet test .\tests\DarkCipher.AgentControl.Tests --filter Live
```

## Icon
`tools\build-icons.ps1` regenerates `src\DarkCipher.AgentControl.App\Assets\AppIcon\` (SVG master, PNGs 16–512 and a
9-frame ICO) from `tools\DarkCipher.AgentControl.IconBuilder`. Tests check the frames, transparency and embedding.

## Add an agent tool to the catalogue (install, update, repair, uninstall)
1. Derive from `WingetToolProvider` in `Integrations/Tools` and fill an `AgentToolDescriptor`: the pinned `PackageSource`
   (winget id, `winget` source), publisher, licence, the facts the plan shows (files, PATH/environment, services,
   configuration, disk), and the latest version this app verified. Implement `DetectAsync` (reuse the agent provider's detection).
2. Register it as an `IAgentToolProvider` in the Bootstrapper. The plan → approval → queue → re-plan fingerprint →
   Testing stage pipeline is shared; do not add install logic anywhere else.
3. Never use another package source or an installer URL, and never run a script.

## Templates
Templates are schema-2 JSON (`Domain/Templates/TemplateTypes.cs`), validated by `TemplateValidationService`, migrated by
`TemplateMigrationService`, evaluated by `TemplateCompatibilityService` (pure, over an `EnvironmentSnapshot`) and turned
into configuration by `ConfigurationBuilderService` (deterministic; fingerprinted). Options a template may set are listed in
`TemplateOptions.Allowed`; providers interpret them in their typed launch-plan builders. A new option needs a provider
implementation and an allow-list entry, never free-form arguments.

## Configuration changes to files the app does not fully own
Use `ConfigurationTransactionService`:
1. `Prepare` or `PrepareFromGenerated`.
2. Show the diff and any conflicts.
3. `Resolve`, if needed.
4. `CommitAsync`, with an optional health check and integration test.

Never write such files directly. Formats: JSON, or the TOML subset (anything outside it is refused).

## Where things go
See ARCHITECTURE.md.
- Domain holds ports only.
- Security holds every privileged implementation.
- Integrations hold adapters.
- Application holds use cases.
- App holds views and view models.
- No business logic in views.

## Add an agent provider
1. Implement `IAgentProvider` in `Integrations/Agents`:
   - `DetectAsync`: use `ExecutableLocator` plus `IProcessExecutor` for `--version`, wrapped in `DetectionCache`.
   - `BuildLaunchPlan`: pure. Return a `ProcessSpec` with `Actor = PathActor.Agent`, `InteractiveConsole` mode, and any `PlannedFileChange` *inside* `request.ProfileDirectory` only.
   - `DescribeEnforcement`: label every permission honestly.
2. Register it in `App/Composition/Bootstrapper.cs` (one line).
3. Add unit tests for the plan: flags, environment, and the dangerous options that are never generated.

## Add a runtime
Implement `IModelRuntime` (and `IDownloadProvider` if it can pull). Use only `INetworkClient`. Register it in the Bootstrapper. List it in the Services screen instead of the "Planned" line.

## Rules
- Never start processes or open sockets outside the Security assembly (enforced by architecture tests).
- Never log source, prompts, responses or credentials.
- Never claim sandboxing. Label maturity: Supported, Experimental, Limited, Planned, Unsupported.
- PowerShell scripts must be ASCII (Windows PowerShell 5.1).
- Bump `<Version>` for every build. `dist\<version>\` is never overwritten or deleted.
