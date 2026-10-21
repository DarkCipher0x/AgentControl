# DarkCipher AgentControl: security model and threat model

Status labels used here:
- **Enforced**: the application technically prevents it.
- **Partially Enforced**: enforced for some paths, with a named gap.
- **Advisory Only**: shown or recommended, but nothing prevents it.
- **Not Implemented**

Anything that relies on trust rather than enforcement is called out.

## 1. Principles
- Least privilege. The manifest requests `asInvoker`, so the app is never elevated. Only the MSI install itself needs administrator approval (it writes to Program Files).
- Secure defaults:
  - Privacy mode **Fully Local**.
  - Third-party extensions **disabled**.
  - Remote runtimes **disabled**.
  - Development Mode **off**.
  - Agent permission profile **Ask Before Changes**.
- Central services, never bypassed. Architecture tests check that Domain, Application, Integrations and Extensions reference no `HttpClient`, `Socket` or `Process` types.
- Fail safe. A missing key, an unknown network class, an unresolvable path, or a manifest with an unknown permission is refused.
- Everything external is untrusted: model metadata, Hugging Face data, Ollama responses, imported stack profiles, extension packages, environment variables, executable paths, agent output.

## 2. Trust boundaries

```
 User ── UI (WPF, view models) ── Application services ── Domain ports
                                         │
            ┌────────────── Security assembly (only implementation of the ports below) ───────────────┐
            │ ProtectedPathService  SecureProcessExecutionService  NetworkSecurityService            │
            │ SecureCredentialService  SecurityAuditService  ConfigurationProtectionService          │
            │ PackageVerificationService  UpdateSecurityService  SecurityPolicyService  Permission… │
            └──────┬──────────────────────┬───────────────────────┬────────────────────┬─────────────┘
           child processes          loopback / approved     Windows Credential     app data dir
       (agents: Claude Code, Codex)   HTTP(S) endpoints        Manager (DPAPI)     (%LOCALAPPDATA%)
                │                          │
        project folders            Ollama (local runtime) ── downloads models itself (outside app policy)
                                           │
 Extensions ── SdkCapabilityBroker ── (same services, Extension actor, permission-checked)
```

Boundaries:

| Boundary | Crossing | Control |
|---|---|---|
| UI → Application | Commands | View models hold no business logic; failures go to the central error reporter. |
| Application → OS | Processes, network, files, secrets | Domain ports only. The Security assembly holds the only implementations. |
| App → Agents | Child process | `SecureProcessExecutionService`. Protected-path check on the working folder (Agent actor). Permission profile mapped to the agent's own flags. |
| App → Runtimes | HTTP | `NetworkSecurityService`, with loopback-only defaults. |
| App → Remote services | HTTPS | Explicit endpoint approval, privacy mode, no credential forwarding. |
| Extensions → App | SDK calls | `SdkCapabilityBroker`: per-call permission and policy checks, audit. |
| App → Credentials | Credential Manager | Values are readable only inside the Security assembly (`internal Reveal`). |
| Packages → Disk | Archive extraction | `PackageVerificationService` safe extraction. Integrity list re-verified at every start. |
| Updates → App | Manifest | ECDSA-signed manifest, downgrade protection. **Disabled: no release key yet.** |

## 3. Threat model

| Threat | Mitigation | Status |
|---|---|---|
| Command / argument injection | Direct exe invocation (`UseShellExecute=false`) with `ArgumentList`. Shell interpreters (cmd, PowerShell, script hosts, rundll32, …) refused; agents and extensions can never opt in. `.bat`/`.cmd`/`.ps1` refused (cmd re-parses arguments). Model names and stack fields validated. | **Enforced** |
| Path traversal / alternate forms | Every path canonicalised: `..`, `/`, case, trailing dots, 8.3 names, `\\?\` / `\\?\UNC\`, symlinks and junctions (OS final path). ADS, device paths and reserved names rejected. Containment is segment-aware. | **Enforced** (tested, including real junctions) |
| Arbitrary execution / exe substitution | Absolute path required, no PATH lookup at launch. Executables refused from temp, Downloads, network shares and protected NoProcessExecution folders. Code-injection environment variables stripped/refused (`NODE_OPTIONS`, `DOTNET_STARTUP_HOOKS`, profilers, …). PATH/ComSpec cannot be overridden. | **Enforced** |
| Child processes escaping control | Each process gets its own Job Object; Stop terminates the whole tree; captured processes die with the app. Only processes this app started can be stopped; nothing is killed by name. | **Partially Enforced**: a child spawned in the instant before job assignment could escape (no suspended-start). |
| Malicious model repositories | Hugging Face is metadata only. Import is Ollama's documented `hf.co/` GGUF pull. No repo scripts, Python, remote code, native libraries or installers are ever run. Gated, private and split repos refused. Result marked "Compatibility Unverified" until tested. | **Enforced** (by design: there is no code path that runs repo content) |
| Malicious extensions | Pre-install review (permissions, risks, isolation). Integrity list required. Executables and scripts forbidden in packages. Reserved ids and publishers refused (spoofing). Downgrades refused. Updates cannot silently gain permissions. Broker checks every call. Tamper check at every start. Crash loops auto-disable. Third-party extensions off by default; Safe Mode switch. | Declarative extensions **Enforced**. Code extensions **Advisory Only**: they run in-process, and .NET cannot sandbox in-process code. They load only in Development Mode, with a persistent warning. |
| Zip-slip / zip bombs | Safe extraction into a new empty folder; symlink entries rejected. Limits on entry count, actual decompressed bytes and compression ratio. Cleanup on any failure. | **Enforced** |
| Supply chain (packages) | Lock files with locked restore in the build. Single official feed with `packageSourceMapping` (dependency confusion). NuGet audit with warnings as errors. Vulnerability scan in `build.ps1`. SBOM (CycloneDX) and licence report. | **Enforced** in the build. Release signing **Not Implemented** (no certificate). |
| Prompt injection / tool-call abuse | Agents run under the user's chosen permission profile, mapped to the agent's own controls. The app never runs commands an agent generates, never auto-approves, and never passes `bypassPermissions` or `danger-full-access`. | **Advisory Only** for what an agent does inside its session (enforced by the agent). **Enforced** for launch folder, credentials and elevation. |
| Credential disclosure | Windows Credential Manager (per user, local persistence). Redactor masks known token shapes and every stored secret, in logs, audit, previews, copied commands and exports. Extensions receive opaque references only. Stack profiles cannot hold secrets (type-level). | **Enforced** for the app's own outputs. Agent processes receive only what the user chooses. |
| Config tampering | App documents: atomic writes, `.prev` copy, quarantine of malformed files, recovery notices. External configs: preview/diff, staleness check, backup, atomic replace, rollback, audit. Agent configs are written only inside the app's agent-profile folder (Codex `CODEX_HOME`); the user's `~/.codex` is never touched. | **Enforced** |
| Insecure updates | Signed manifest (ECDSA P-256), HTTPS on an allow-listed host, strictly newer version, SHA-256. With no key configured, everything is refused. | **Enforced (fail closed)**. The updater is off because no release key exists. |
| Privilege escalation | Never elevated. Agents run as the user. No services installed. | **Enforced** |
| Local API exposure | The app exposes no network listener. Ollama exposure check warns when port 11434 listens beyond loopback. | **Enforced** (app). Ollama binding is **Advisory** (warned, not changed). |
| Unexpected cloud fallback | No automatic fallback exists. Fully Local blocks non-local inference at the network service and the session launcher. Native (cloud) sessions need approval and are audited. | **Enforced** |
| Data exfiltration via network | Non-loopback endpoints need explicit approval. HTTPS required except approved LAN. Redirects re-checked (no HTTPS→HTTP, no unapproved hosts). Credentials never sent cross-host. No ambient Windows credentials. Response size caps. | **Enforced** for app traffic. Traffic made by agents and by Ollama itself is outside app control (documented). |
| Unsafe temp files | Atomic writes use temp files beside the target (same volume, `CreateNew`). The app never runs anything from temp. | **Enforced** |
| Storage exhaustion / DoS | Free-disk check before downloads. Bounded process output, log rotation, bounded audit/history lists, response size caps. ReDoS-safe redactor (non-backtracking) and bounded regexes. Timeouts everywhere. | **Enforced** |
| Malicious imported profiles | Size cap and schema version check. Permission profile reset to the safe default. Remote endpoints refused. Invalid model names refused. Audited. | **Enforced** |
| Audit tampering | HMAC-SHA256 hash chain, keyed with a DPAPI-protected key. Edits, deletions and reordering are detected by Verify. | **Partially Enforced**: truncating the tail (deleting the newest lines) is not detectable without an external anchor. A user with DPAPI access to the key could rewrite the chain. |

## 4. Protected paths
- **Built-in (not removable)**:
  - App data: the data root itself and its config, audit, logs, backups, extensions and staging folders are BlockAllAccess for agents and extensions (the app itself may write). `test-projects` (Quick Setup sample projects) and `agent-profiles` (isolated agent config such as Codex CODEX_HOME) are deliberately usable by agents.
  - Windows and Program Files: ReadOnly + NoAddOnAccess.
  - Credential stores (`.ssh`, `.gnupg`, `.aws`, `.azure`, `.kube`, `.docker`, Windows Credentials/Protect folders): BlockAllAccess.
- **Default user rule** (removable): `%USERPROFILE%\Projects`, ReadOnly + AskBeforeDeletion + NoAddOnAccess. An agent can still be started there deliberately: it needs explicit approval every time, and the session is **forced to the Read-Only profile**.
- **Rules**: Block All Access, Read Only, Ask Before Writes, Ask Before Deletion, No Process Execution, No Add-On Access. All matching rules combine and the most restrictive wins.
- **Working folders** that are a drive root, the whole user profile, a network share or removable media require approval.
- **Enforcement scope**: the app enforces these rules for its own operations and at agent launch. Inside a running agent session, file access is governed by the agent's own permission system. The app does not sandbox agent processes (**Advisory** beyond launch).

## 5. Agent permission profiles
- **Profiles**: Read-Only, Ask Before Changes (default), Project Workspace Only, Standard Development, Advanced Development, and custom profiles.
- **Coverage**: every agent permission (file read, create, write and delete; directories; process execution; terminal; network; package installs; git; outside workspace; credentials; local services; external APIs; add-ons; privileged operations).
- **Enforcement labels**: each permission is shown with who enforces it: Application Enforced, Agent Configured, User Approval Required, Advisory Only or Unsupported. See the Agents screen.
- **Never generated**: `bypassPermissions`, `--dangerously-skip-permissions`, `danger-full-access` and approval `never`. These are covered by tests.

## 6. Security audit log
- **Location**: `%LOCALAPPDATA%\DarkCipher\AgentControl\audit\security-audit.jsonl`.
- **Fields**: Timestamp, Action, Component, Result, Affected Resource, Security Classification, Correlation ID, Detail (redacted), Prev, Mac.
- **Recorded events**:
  - Privileged operations, process starts, stops and blocks.
  - Protected-path violations and approvals.
  - Credential create and remove (no values).
  - Policy changes; weakening changes are classified High.
  - Endpoint approvals and network blocks.
  - Extension install, update, enable, disable, remove, tamper and capability denials.
  - Config changes and rollbacks.
  - Stack imports.
  - Cloud sessions.
  - Downloads.
  - Update signature checks.

## 7. Known gaps (honest list)
1. Code extensions are not isolated (in-process). Out-of-process, low-integrity isolation is Planned.
2. A Job Object assignment race allows a tiny window for an early grandchild to escape.
3. The audit chain cannot detect tail truncation without an external anchor.
4. Once launched, an agent's actions inside its session are governed by the agent, not this app.
5. Ollama performs model downloads itself, so its network traffic does not pass through this app's network policy.
6. No code-signing certificate, so builds are unsigned and the updater stays disabled.
7. Development-time rule: this project was built without writing anything under `%USERPROFILE%\Projects`. The Projects default rule enforces the same idea at run time.
8. (0.1.6) Tool installs are only as trustworthy as winget's community repository: winget checks the installer's SHA-256 against the reviewed manifest, but this app does not verify publisher signatures of those installers itself.
9. (0.1.6) Extension health checks and model sources run in-process (code extensions are not isolated); they are time-limited and exceptions are contained, but a hostile extension could still misbehave inside the process.

## 8. Threat review for 0.1.6 (installs, downloads, templates, configuration)

| Threat | Control | Status |
|---|---|---|
| Installing from an unverified mirror or arbitrary URL | Only winget, only `--exact` ids pinned in built-in providers, only the `winget` source; ids validated against a strict pattern. Extension catalogue entries can only name a winget id and are labelled Unverified. | Enforced |
| Silent installs | Every install, update, repair and uninstall needs a plan listing tool, publisher, source, version, method, downloads, privileges, files, configuration, services, environment/PATH changes, licence, rollback and warnings, then explicit approval. Headless hosts deny. | Enforced |
| Plan changes between approval and execution (TOCTOU, downgrade or upgrade swap) | Plans are fingerprinted (package, version, command, privileges, environment, files). The queue re-plans before running and refuses on any difference. | Enforced |
| Shell injection through installer commands | winget runs through the secure executor: full path, argument list, no shell. The app-execution alias passes the executable rules; interpreters, Temp and Downloads stay refused. | Enforced |
| Elevation | Installs use `--scope user`; nothing requests administrator rights. The app never runs elevated. | Enforced (Ollama's own installer behaviour is described in its plan) |
| Global environment and PATH changes | Plans state the exact user-level PATH entries winget or the installer add. The app itself never changes PATH, profiles or terminal settings. | Advisory (third-party installers make the change; the app shows it first) |
| Partial-download confusion / "installed because downloaded" | A queue item is Ready only after its verifier proves it: tool detection and version range, or model visibility plus the compatibility suite. Interrupted items are never resumed silently. | Enforced |
| Archive traversal, zip bombs, symlinks (packages, extensions) | Unchanged `PackageVerificationService` safe extraction. | Enforced |
| Model integrity | Ollama verifies layer SHA-256 digests. Hugging Face imports go through Ollama's `hf.co` pull (no repository code runs). | Enforced by Ollama (outside this app's network policy, see gap 5) |
| Templates gaining privileges | Templates are data: unknown fields are refused, options come from an allow-list, there are no executables, arguments, environment variables, paths or secrets, and the text is secret-scanned. Imported and extension templates are reset to "Ask Before Changes". Launch goes through the session service (protected paths, privacy policy, approvals). | Enforced |
| Configuration overwrite of user settings | Configuration transactions merge only the generated keys, keep unrelated settings and comments, and report user-changed values as conflicts. Every write has a backup, an atomic write, a re-read and a value check. Health or integration failures roll back. Unsupported TOML/JSON is refused, never rewritten. | Enforced |
| Credentials in exports | Appearance, template and stack exports contain no secrets by construction and are scanned; credential-like content refuses import. | Enforced |
| Extension contributions (SDK 1.1) | Templates are data only. Catalogue entries, model sources and compatibility rules need CatalogueContributions (model sources also need Network). Health checks need UI; settings panels need Settings. Every denial is audited. Safe Mode refuses all of them. | Enforced (in-process, see gap 9) |
| Unverified custom agents | Registration validates a full `.exe` path and refuses secrets, PATH and code-injection variables. Trust is Unverified until a validation run through the secure executor. Templates refuse unverified tools. | Enforced |
| "Supported" claims without proof | Strict classifier: Supported and Verified only when every requirement AND a safe end-to-end probe (disposable project, Read-Only profile, random token) pass. Cloud probes need approval and a cloud-permitting privacy mode. | Enforced |

| Starting software the app does not own (0.1.8) | Only Ollama's own tray app (`ollama app.exe` next to the detected `ollama.exe`), and only from Quick Setup after plan approval or from a *Fix* the user clicks. A running Ollama is never restarted. It is launched as a **Background** helper: same executable, argument, working-folder and environment validation as every launch; no window; `CreateProcessW` with handle inheritance **off**, so the helper holds none of this app's handles; a job without kill-on-close, so it survives the app and Stop still ends its tree. Background mode is refused for agents and extensions (`ProcessBlocked`). | Enforced |

| Wider catalogue (0.1.9): 17 more winget packages | Each entry is reviewed and pinned in code: an exact package id, an https homepage and a licence. Per-user installer availability was checked against the live winget source (`Live_catalogue_entries_resolve_per_user_and_plan_without_installing`). Packages without a per-user installer (GPT4All, Docker Desktop) were left out. Detection reads one shared `winget list` snapshot (cached 60 s, invalidated by every install, update and uninstall). It never starts GUI apps to read a version and ignores WindowsApps aliases. | Enforced |
| Gateways installed "by name" | A gateway built into a runtime plans its runtime's install, and the plan says so. Update and uninstall are refused on the gateway and managed on the runtime. | Enforced |
| Bundled gallery extensions (0.1.9) | Packed from the app's install folder by the shared packager and reviewed through `ExtensionHost.ReviewBundled`: all normal checks, plus data only and the `darkcipher.gallery.` prefix. Only that path may use the reserved identity (trust Verified). `Install from file…` and `dcx-ext pack` still refuse it. Bundled extensions load even when third-party extensions are disabled; Safe Mode still blocks them. | Enforced (integrity relies on the install folder being admin-writable only, as for the app's own binaries) |
| Right-click menus | Menu items are the page's existing commands with their CanExecute rules. Destructive actions are always last, in the danger colour, and keep their confirmation dialogs. | Enforced |

New audit events: `ToolOperationApproved`, `ToolOperationDeclined`, `ToolOperation`, `ToolPlanChanged`,
`CustomAgentRegistered`, `CustomAgentValidated`, `ExtensionCatalogueEntry`, `TemplateRegistered`, `TemplateSaved`,
`TemplateImported`, `TemplateImportRejected`, `ConfigTransactionCommitted`, `ConfigTransactionRolledBack`,
`ConfigRemoved`, `StackVerified`, `DownloadQueued` and `DownloadFinished` (now with stage and verification).
