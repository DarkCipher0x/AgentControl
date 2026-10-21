# Architecture

DarkCipher AgentControl is a WPF desktop app on .NET 8 (Windows 10/11 x64). It is built as layered assemblies with one
composition root. The project graph enforces the dependency rules, and `tests/.../Architecture/ArchitectureTests.cs`
checks them again.

## Layers and assemblies

| Assembly | Layer | Owns | May depend on |
|---|---|---|---|
| `DarkCipher.AgentControl.Domain` | Domain | Entities, value objects and **ports**: `IModelRuntime`, `IModelSource`, `IAgentProvider`, `IProcessExecutor`, `INetworkClient`, `ICredentialStore`, `IAuditLogger`, `IProtectedPathService`, `IStackValidator`, `IDownloadProvider`, `IHardwareDiscoveryProvider`, `IPackageVerifier`, `IConfigurationProtection`, `ISecurityPolicyService`, `IPermissionService`, `IDocumentStore`, `IAppLogger`, `IApprovalService` | nothing |
| `…Application` | Application | Use cases: recommendation engine and model catalogue, HF import planner, hardware assessment, stacks (service, validator, templates), projects, sessions, downloads, setup planner, repair diagnosis, compatibility store, settings | Domain |
| `…Security` | Security | The **only** implementations of the security-relevant ports: protected paths and canonicalisation, process execution, network, credentials, audit, configuration protection, packages, updates, policy, permissions, redaction | Domain |
| `…Infrastructure` | Persistence + Observability | App paths, typed JSON document store (atomic, recovery), structured logger, Windows hardware discovery | Domain, Security |
| `…Integrations` | Integration | Ollama (REST client, runtime adapter, detector, compatibility suite, gateway probe), Hugging Face source, Claude Code and Codex agent providers | Domain |
| `…Sdk` | SDK | Public, versioned extension contracts (manifest, permissions, `IExtension`, `ISdkCapabilityBroker`, contribution records) | nothing |
| `…Extensions` | Extension platform | Manifest validator, package integrity, extension host (lifecycle, load contexts), capability broker, contribution registry | Domain, Sdk |
| `…App` | Presentation + composition root | WPF shell, views, view models, dialogs, `Bootstrapper` (DI), self-test | everything |

Rules:
- No business logic in views.
- No provider-specific conditionals outside the provider's own adapter. Adding an agent means one new `IAgentProvider` plus one DI line. Adding a runtime means one `IModelRuntime`.
- Only the Security assembly starts processes or opens network connections. Metadata-level architecture tests check this.
- Secrets are readable only inside the Security assembly (`SecureCredentialService.Reveal` is `internal`).

## Modules (by folder)
- **Shell**: `App/Shell`.
- **Screens** (15): Dashboard, Quick Setup, Agents, Models, Stacks, Projects, Sessions, Services, Hardware, Downloads, Extensions, Developer/SDK, Security, Logs, Settings (`App/Views`, `App/ViewModels`).
- **Hardware**: `Infrastructure/Hardware`, `Application/Hardware`.
- **Model**: `Application/Recommendations`, `Integrations/HuggingFace`.
- **Runtime**: `Integrations/Ollama`.
- **Agent**: `Integrations/Agents`.
- **Stack**: `Application/Stacks`.
- **Project/Session**: `Application/Sessions`.
- **Process**: `Security/Processes`.
- **Download**: `Application/Downloads`.
- **Security**: `Security/*`.
- **Settings**: `Application/Settings`.
- **Logging**: `Infrastructure/Observability`.
- **Update**: `Security/Updates`.
- **Extension**: `Extensions`.
- **SDK**: `Sdk`.

## Key flows
- **Launch a session.**
  1. `SessionService.PreviewAsync` resolves the stack and project.
  2. `ProtectedPathService.Evaluate(UseAsWorkingDirectory, Agent)` checks the folder.
  3. The privacy policy is checked.
  4. `IAgentProvider.DetectAsync` finds the agent.
  5. `IPermissionService.Effective` picks the profile; a read-only folder forces Read-Only.
  6. `IAgentProvider.BuildLaunchPlan` builds a plan. This step is pure and unit-tested.
  7. On start: approval when required, then profile files written via `IConfigurationProtection` (only inside the agent-profile folder), then `IProcessExecutor.Start` (Job Object, audit).
- **Compatibility.** `OllamaCompatibilitySuite` runs fixed-prompt tests through `INetworkClient` against loopback. `CompatibilityStore` keeps results per model digest. `StackValidator` uses them for readiness.
- **Downloads.** `DownloadManager` runs a sequential queue over `IDownloadProvider`; Ollama pull is the provider. A download is complete only when the model is listed.
- **Extensions.** `ExtensionHost.Review` does safe extraction, manifest validation and the integrity check. `Install` asks for permission approval and commits atomically. `StartAsync` re-checks integrity (tamper detection), then applies policy (Safe Mode, third-party switch, Development Mode), then loads. Every capability goes through `SdkCapabilityBroker`.

## Architectural decisions
See [ADRS.md](ADRS.md).
