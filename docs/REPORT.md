# DarkCipher AgentControl: technical report (v0.1.2, 2026-10-18)

> **0.1.6 (2026-10-20):** icon, shared design system, customisation, catalogue and installs, unified queue, templates, configuration transactions, readiness and verification are reported in **REPORT-0.1.6.md** (36 points). This file keeps the 0.1.2 baseline.

**What changed in 0.1.2:**
- Packaging: a per-user MSI, plus a self-contained zip, SDK nupkg, dcx-ext zip, sample packages, docs bundle, SBOM and `SHA256SUMS.txt`. The build verifies every checksum and checks the MSI by administrative extract plus a self-test of the extracted exe.
- Live headless Claude Code launch through the local gateway: IV.
- Quick Setup dry run in the self-test: IV.
- Fixed: the data-folder rule blocked Quick Setup sample projects.

Rows below are updated where 0.1.2 changed their status.

## Labels
- **Features**:
  - **IV**: Implemented and Verified (automated tests and/or live run on this PC).
  - **INV**: Implemented but Not Fully Verified.
  - **EXP**: Experimental.
  - **PL**: Planned.
  - **UNS**: Unsupported.
- **Security controls**:
  - **ENF**: Enforced.
  - **PART**: Partially Enforced.
  - **ADV**: Advisory Only.
  - **NI**: Not Implemented.

## 1. Identity, location, stack
- **Name**: DarkCipher AgentControl (DarkCipher Systems).
- **Location**: `%USERPROFILE%\Projects\DarkCipherAgentControl` (moved from the original development folder `%USERPROFILE%\local-projects` after 0.1.3). Build and test scratch data lives in `%USERPROFILE%\.dcac-dev`.
- **Stack**:
  - .NET 8 (SDK 8.0.426, pinned in `global.json`), WPF, C# 12.
  - Microsoft.Extensions.DependencyInjection 8.0.1, System.Management 8.0.0, System.Security.Cryptography.ProtectedData 8.0.0.
  - xUnit 2.9.2 for tests.
- **Packages**: all restored into `.nuget\packages` inside the project folder.
- **Development rule**: nothing was written under `%USERPROFILE%\Projects`. ServerManagement was read only, for its design language.

## 2. Architecture and modules
- **Layers**: eight assemblies: Domain, Application, Security, Infrastructure, Integrations, Sdk, Extensions and App (composition root).
- **Dependencies**: one-way, with no cycles. This is verified by architecture tests, including metadata inspection proving that only Security uses `HttpClient`, sockets or `Process` (IV).
- **More detail**: see ARCHITECTURE.md and ADRS.md (11 ADRs).

## 3. Security architecture

| Control | Status | Notes |
|---|---|---|
| SecurityPolicyService (privacy mode, third-party switch, remote runtimes, Safe Mode, endpoint approvals) | ENF / IV | Weakening changes are audited High. Command-line Safe Mode is locked for the session. |
| PermissionService (built-in + custom profiles, effective profile) | ENF / IV | Read-only folders force the Read-Only profile. |
| ProtectedPathService (canonical paths, 6 rule types, built-ins, user rules) | ENF / IV | Tested with real junctions and 8.3 names. |
| SecureCredentialService (Windows Credential Manager) | ENF / IV | Values readable only inside the Security assembly. |
| SecureProcessExecutionService | ENF / IV | Job-assignment race: **PART** (documented). |
| NetworkSecurityService | ENF / IV | App traffic only. Agent and Ollama traffic is outside its reach (ADV). |
| SecurityAuditService (HMAC chain, DPAPI key) | PART / IV | Tail truncation is not detectable. |
| ConfigurationProtectionService | ENF / IV | |
| PackageVerificationService (safe extraction, SHA-256, Authenticode status) | ENF / IV | |
| UpdateSecurityService (ECDSA-signed manifest, downgrade protection) | ENF (fail closed) / IV | Updater off: no release key. |
| SdkCapabilityBroker | ENF for declarative / IV; ADV for code extensions | In-process code cannot be sandboxed. |
| Redaction (non-backtracking patterns + registered secrets) | ENF / IV | |

- **Threat model and trust boundaries**: see SECURITY.md. Every listed threat has a mitigation and a status.
- **Relies on trust rather than enforcement**:
  - What an agent does *inside* its session (its own permission system).
  - Code extensions in Development Mode.
  - Ollama's own downloads and binding.
  - Integrity of Windows Credential Manager and DPAPI.

## 4. Protected paths, permissions, credentials, process and network security, audit
- **Protected paths** (ENF/IV):
  - Built-ins: app data (agents and extensions blocked), Windows, Program Files, credential folders.
  - Default removable rule: `%USERPROFILE%\Projects` Read-Only. Agents can be started there only with explicit approval and are forced to Read-Only.
- **Permission profiles** (IV):
  - Profiles: Read-Only, Ask Before Changes (default), Project Workspace Only, Standard, Advanced, Custom.
  - Each permission is shown with its enforcement level: Application Enforced, Agent Configured, User Approval Required, Advisory Only or Unsupported.
  - Never generated: `bypassPermissions`, `danger-full-access`, approval `never` (tests).
- **Credentials** (IV): Credential Manager only; redacted everywhere; extensions get references only.
- **Processes** (IV):
  - Direct invocation; shells, scripts, temp and network executables refused.
  - Code-injection environment variables refused or stripped.
  - Job Objects, timeouts, cancellation, bounded output, duplicate prevention, audit.
- **Network** (IV):
  - Deny by default; DNS-free classification (Loopback, Local Network, Private Remote, Public Internet, Unknown).
  - HTTPS rules, privacy modes, manual safe redirects, no credential forwarding, size caps, bounded GET retries.
- **Audit** (IV): Timestamp, Action, Component, Result, Resource, Classification, Correlation ID, Verify in the UI.

## 5. Features

| Feature | Status |
|---|---|
| WPF shell, 15 screens, DarkCipher design (near-black, graphite panels, teal accent), off-screen render self-test | IV (rendered and visually reviewed; not driven by a human yet) |
| Hardware discovery: OS, CPU and instruction sets, RAM, GPUs (registry qwMemorySize; Parsec and virtual adapters ignored), driver date, disks, NPUs (detected only) | IV (correct on this PC: 9070 XT 15.9 GiB, Parsec ignored, no false NPUs) |
| Hardware rating with "Why this recommendation?" | IV |
| Recommendation engine (memory budgets, KV cache, context, tools, licence, 11 profiles, measured calibration via /api/ps, no speed claims) | IV (unit-tested). Curated catalogue sizes are approximate and labelled. |
| Ollama: detection (official, AMD AI Bundle, PATH), REST status/tags/ps/show/pull/delete/copy/unload, exposure warning | IV (live against 0.40.6, plus unit tests) |
| Ollama compatibility suite 1.1.0 (connectivity, load, generate, instructions, code, JSON schema, tool calls, multi-turn, context, cancellation; tok/s from Ollama counters) | IV (live: gpt-oss-64k and qwen3:14b Agentic Compatible; gemma3:4b Text Generation Only) |
| Agent compatibility matrix | INV (built from stored results; UI shown, populated by tests) |
| Model browser: installed, recommended, Ollama library (curated) with filters, Hugging Face, actions (Test, Gateway test, Details, Unload, Copy, Remove with in-use check, Download) | INV (screens render; actions use tested services; not clicked by a human) |
| Hugging Face (official API: search, details, GGUF files/quant, licence class, gated/private; endpoint approval on first use) | INV (request shape and parsing unit-tested; no live HF call made this session) |
| HF import via Ollama `hf.co/{repo}:{quant}` with hardware-based quant choice, disk check, warnings | INV (planner unit-tested; no live import run) |
| Download manager (queue, dedupe, disk check, progress, speed, ETA, cancel, retry/resume, verified completion) | INV (provider unit-tested; no full live download run, to avoid multi-GB downloads) |
| Claude Code native mode | INV (detected 2.1.296; launch plan tested; interactive launch not exercised, to avoid opening windows on your desktop) |
| Claude Code + verified/experimental local gateway (Ollama Anthropic API, honest labels, "NOT Claude") | EXP. Gateway probe IV (text and tool_use pass with gpt-oss-64k). Real launch IV headless: the app's plan through the secure executor in a disposable project answered via gpt-oss-64k; start/stop of the process tree verified. A windowed launch from the UI is INV. |
| Packaging (per-user MSI, self-contained zip, SDK nupkg, tool zip, samples, docs zip, SBOM, SHA256SUMS) | IV (built and checksum-verified by `build.ps1`; MSI ICE-validated, admin-extracted and self-tested). A real `msiexec /i` install is INV, so as not to install on the development PC. **Unsigned.** |
| Codex + Ollama (isolated CODEX_HOME, documented keys, `--oss`) | INV (Codex is not installed on this PC; plan and config unit-tested against current docs) |
| Codex native | INV |
| Stack profiles: 6 built-ins, save/duplicate/delete/export/import (untrusted handling), readiness validation, Repair (diagnose only) | IV (services) / INV (UI) |
| Projects and sessions: add folder, protected-path evaluation, approval flow, start/stop/restart, copy safe command, history | IV (services, with fakes) / INV (real interactive launch) |
| Quick Setup wizard (Welcome → Hardware → Software → Recommended → Model → Plan preview → Install → Verify → Ready, with rescan, back, cancel, retry, change model, logs, technical details) | INV (renders; steps use tested services; not walked through by a human) |
| Logs (filter, search, copy, export, correlation IDs, redaction) | IV (logger) / INV (UI) |
| Settings (endpoint, preferred agent, HF token, data folder, onboarding reset; no fake options) | INV |
| Privacy modes Fully Local / Local Preferred / Hybrid / Cloud Allowed | IV |
| Benchmarking / auto-tuning beyond the compatibility suite | PL |
| LM Studio, llama.cpp, vLLM, LocalAI, generic OpenAI/Anthropic-compatible runtimes | PL (shown as Planned in Services) |
| NPU inference runtimes | PL (detection only) |
| Automatic cloud fallback | UNS by design (never automatic) |
| Ollama / agent installation by the app | UNS by policy (official links; the user installs; no silent installs) |

## 6. Model formats
- **GGUF**: through Ollama (IV via installed models). Hugging Face GGUF import is INV.
- **Split GGUF, safetensors and other formats**: UNS for import. The UI says why.
- **Text models without tool support**: never treated as agent compatible (IV, gemma3).

## 7. Extensions and SDK
- **Platform** (IV):
  - Discovery and validation; review screen (permissions with meaning and risk, endpoints, trust, isolation).
  - Integrity list, safe extraction, executable ban.
  - Anti-spoofing (reserved ids and publishers), downgrade and duplicate protection, update permission gain flagged.
  - Enable, disable, remove; Safe Mode; "Disable third-party extensions" (default on).
  - Tamper check at every start; crash-loop auto-disable after 3 failures.
  - Failures never block start-up.
- **Isolation**: declarative ENF. Code extensions ADV: in-process, collectible ALC, only the SDK shared, references to internals refused, Development Mode only.
- **SDK 1.0.0**:
  - Semver; additive minor versions; major versions refused; negotiation checks both the manifest and the assembly reference.
  - Contracts: `IExtension`, `ISdkCapabilityBroker` (stack templates, commands, panels, model info, hardware summary, approved processes, model downloads, credential references, scoped settings, logging, health).
  - The contract test pins the surface (IV).
- **Docs**: `docs/sdk/README.md` (overview, architecture, security, manifest, permissions, lifecycle, versioning/deprecation, API, tutorials, testing, packaging, signing status, debugging, publishing) and `schemas/extension-manifest.schema.json`.
- **Samples**:
  - Declarative: stack template, model catalogue (IV, validated and packed).
  - Code: agent detector, dashboard panel (IV, loaded through the real host in tests).
- **Tooling**: `dcx-ext new | validate | analyse | check-compat | pack` (IV), with the mock host and contract tests in the test suite.
- **Planned**: out-of-process isolation, agent-provider and runtime registration via the SDK, publisher signing, catalogue.

## 8. Supply chain, SBOM, updates
- Lock files with locked restore in `build.ps1` (ENF).
- Single nuget.org source with packageSourceMapping (ENF).
- NuGet audit; vulnerability scan clean (IV).
- CycloneDX SBOM and licence report in `dist\<version>\` (IV).
- Release signing NI (no certificate).
- Updater ENF fail closed and disabled.

## 9. Tests
- 268 automated tests pass: 264 offline (security, architecture, SDK contract, providers, application, extensions) and 4 opt-in live tests.
- 31-point headless self-test, including the Quick Setup dry run.
- Opt-in live Ollama tests.
- Details in TESTING.md.

## 10. Build and run
- `.\build.ps1` (full pipeline), or `dotnet build` then run `src\DarkCipher.AgentControl.App\bin\<cfg>\net8.0-windows\win-x64\DarkCipherAgentControl.exe`.
- `--safe-mode`; `--selftest <dir>` (with `DCAC_DATA_DIR`).

## 11. Limitations
- **General**: the UI has not yet been driven interactively by a person. Interactive agent launch and Quick Setup install were not executed end to end, because they open terminal windows or start multi-GB downloads.
- **Hardware and runtimes**:
  - Curated catalogue sizes are approximate.
  - KV estimates are worst-case f16.
  - Only Ollama is implemented.
  - Codex is not installed here, so it is unverified live.
  - NPUs are detected only.
- **Security**: see the SECURITY.md "Known gaps" list (code extensions not isolated, job race, audit tail truncation, agent-internal behaviour, Ollama's own traffic, no signing).
- **Extensions**: no publisher signatures, no catalogue, no out-of-process host. Model-catalogue contributions are validated but not yet merged into the Models screen.

## 12. Next steps (priority order)
1. A watched interactive pass by the owner:
   1. Quick Setup with the Codex stack, after installing Codex.
   2. Claude Code + gateway session in the sample project.
   3. Confirm the console window, Stop, and Restart behave.
2. Out-of-process extension host (low integrity + Job Object), then enable agent-provider and runtime contributions via the SDK.
3. Suspended-start process creation (`CreateProcess` with `CREATE_SUSPENDED`, then assign to the job, then resume) to close the job race.
4. Audit anchor (periodic signed checkpoint) to detect tail truncation.
5. Merge extension model catalogues into Models. Add a benchmark screen (prompt eval, generation, TTFT, VRAM) built on the suite.
6. LM Studio / llama.cpp adapters behind `IModelRuntime`.
7. Code-signing certificate, then a release key for the updater.
