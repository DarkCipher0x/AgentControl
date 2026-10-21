# Changelog

All builds are unsigned (no code-signing certificate exists).

## 0.1.10 — 2026-10-21
The first public release: the source is on GitHub (MIT), and every release now comes from one automated, privacy-gated pipeline.
### Added
- **Public source:** `github.com/DarkCipher0x/AgentControl`, under the MIT licence (`LICENSE`).
- **Release pipeline** (`release.ps1`, `tools\release\`, documented in `docs/RELEASE.md`):
  - verify → sanitise → mirror → GitHub Release → website check → Discord check;
  - a dry run (`-DryRun`), resuming from a step (`-From`) and a per-version record (`dist\<ver>\release-state.json`);
  - the kill switch `DCX_PUBLISH_OFF=1`, shared by every DarkCipher app.
- **The public source zip** (`DarkCipherAgentControl-<ver>-source.zip`) is written by every build. It is built from an explicit allowlist: a file in neither the allowlist nor the private list stops the build. Every file then passes the image approval list, a privacy scan and gitleaks 8.30.1 (pinned and hash-checked), and the archive uses `/` paths.
### Changed
- The README is rewritten for the public repository.
- The release procedure is rewritten as `docs/RELEASE.md`.
### Fixed
- A maintainer's name in `docs/REPORT.md` was replaced by "the owner".

## 0.1.9 — 2026-10-20
More to install, clearer ways to install it, and right-click menus everywhere.
### Added
- **17 more catalogue entries (winget, per-user, reviewed and pinned):**
  - Model runtimes: LM Studio, llama.cpp, Jan, llamafile.
  - Agent CLIs: GitHub Copilot CLI, OpenCode, Crush, Kilo CLI, Amp CLI.
  - Supporting tools: Git, Node.js LTS, Python 3.13, uv, ripgrep, fd, jq, GitHub CLI.
  - These are labelled **Catalogue only**: install, update, detect and remove, but no stack or session uses them yet.
- **Gateways you can install.** The API servers built into runtimes (Ollama's OpenAI-compatible API, LM Studio's local server, llama-server, Jan's local API) are listed with their port and a running/not-running state. Installing one installs its runtime.
- **Extension Gallery.** Seven bundled, data-only extensions from DarkCipher Systems: Python, web, Rust, 8 GB GPU and Claude Code gateway stacks, plus two model shortlists. They install through the normal review, and nothing is downloaded.
- **Install from every page.**
  - Agents lists every agent tool, Services every runtime and gateway, each with Install or Update.
  - Models has a Download button on every library row and recommendation that follows the queue live (Download → Queued → % → Testing → Installed, or Resume/Retry). It also has *Download by name* and a list of unfinished downloads.
  - Catalogue gains *Available* and *Supporting tools* tabs and a search box.
- **Right-click menus** on every list: tools, models, downloads, stacks, templates, projects, sessions and extensions. Short menus are flat. Crowded ones keep the main actions on top and group the rest into submenus (*Open*, *Copy*, *Manage*). Destructive actions are always last, in red. The menus use themed templates (dark submenus, accent highlight).
- *Start Ollama* on the Services page.
### Changed
- Catalogue detection reads one shared `winget list` snapshot instead of one winget call per tool.
- Winget versions may start with `v` or `b` (Copilot `v1.0.100`, llama.cpp `b11723`); they used to show as "latest".
- `dcx-ext pack` and the app share one packager (`ExtensionPackager`).
### Fixed
- The Ollama Anthropic gateway no longer shows "not running" while Ollama runs. Its health is Unknown until the gateway test passes; "not running" now means the port is closed.
### Tests
- 424 automated tests, including catalogue data integrity, snapshot detection, gateway install delegation, packaging and bundled review of every gallery extension, and refusal of the reserved identity outside the gallery.
- Self-test 51/51, with new checks for catalogue breadth, the gallery and menu grouping. It also renders a real tool's right-click menu.
- Live (opt-in): all 17 new packages resolve per-user in the winget source and plan correctly. Nothing was installed.

## 0.1.8 — 2026-10-20
Quick Setup is now automated end to end (reported: it stopped at "Install Codex from OpenAI's official instructions").
### Changed
- **Quick Setup installs missing components itself.** Ollama, Codex CLI and Claude Code are installed through the same path as the Catalogue (winget exact id, `--scope user`, no administrator rights). Each install opens its own approval with the full plan, runs in the Downloads queue, and counts as done only after its post-install test. Manual steps remain only as a fallback when winget is not available.
- **Quick Setup starts a stopped Ollama** with its own tray app. It keeps running after AgentControl closes. *Fix → Start runtime* on Stacks and Templates does the same, instead of telling you to start it yourself.
- Hybrid plans no longer ask for Ollama.
### Fixed
- **Retry and Rescan on the Install step repeated a stale plan**, so the step failed again even after the component was installed, and the same line appeared twice. The Install step now detects again and re-plans every time, and Retry clears the old output.
### Security
- New **Background** process mode for helpers that outlive the app: `CreateProcessW` with handle inheritance off (a helper holds none of AgentControl's handles; found live, an inherited stdout pipe kept a test run open), no window, and a job without kill-on-close (Stop still ends its tree). Refused for agents and extensions. See `docs/SECURITY.md` §8.
### Tests
- 406 automated tests: Quick Setup planner, Background process mode (survives the app; refused for agents) and an opt-in live test that started a stopped Ollama on the build PC.

## 0.1.7 — 2026-10-20
Fixes found by the first live install and end-to-end verification (Codex and Claude Code installed through the catalogue).
### Fixed
- **Agents installed by winget were not detected when winget created no `Links` alias** (seen with `OpenAI.Codex` 0.161.0, whose exe has a non-standard name). Detection now also looks in the winget package folder (`%LOCALAPPDATA%\Microsoft\WinGet\Packages\<Id>_*`). It reads the entry point from the package's own `*-package.json` (a bare `.exe` name only, validated) and falls back to known names.
- **Codex stacks failed on models without thinking support** ("does not support thinking"). The template's reasoning level became a thinking request for every call. The deterministic configuration builder now drops the reasoning option, with a warning, when the model does not report the thinking capability. The model's capabilities are part of the fingerprinted inputs. Template compatibility shows a Reasoning warning.
### Verified
- **First Supported and Verified configuration:** Codex CLI 0.161.0 + Ollama 0.40.6 + llama3.2:latest, Code Review (read-only) template, on the build PC. See `docs/SUPPORTED-CONFIGURATIONS.md`.
- Claude Code 2.1.302 + the Ollama gateway + llama3.2 was **Failed Validation**: the model is too small for Claude Code's agent prompt. A larger model has not been tested yet.
### Tests
- 400 automated tests, including a new test for dropping the reasoning option. The live install-and-verify test now records the real app version.

## 0.1.6 — 2026-10-20
### Added
- **Application icon.** An original DarkCipher mark: a hexagon, an open cipher ring, orchestration nodes and an agent core.
  - Generated as an SVG master, PNGs from 16 to 512 px, and a 9-frame ICO.
  - Detail is reduced at small sizes.
  - Wired into the exe, every window (title bar and taskbar), the header mark, Settings → About and the MSI (Add/Remove Programs and both Start-menu shortcuts).
- **Shared DarkCipher design system.** The exact WhisperDesk/ServerManagement palette and typography: Segoe UI Variable Text, Cascadia Mono, the 14-step type ramp and 2 px corners.
  - Semantic tokens.
  - Every control re-templated (including radio buttons, switches, progress and focus states).
  - Views use tokens only, enforced by tests.
- **Customisation**:
  - Presets: DarkCipher, High Contrast, Graphite, Teal, Light and Follow Windows.
  - Accent colours, with a contrast check.
  - Status intensity, density, text size, zoom, corners and reduced motion.
  - Sidebar: expanded, compact or icon-only; pinned and hidden pages; landing page; developer, experimental and advanced sections. Pages with security or session controls cannot be hidden.
  - Dashboard: customise cards in place, Detailed/Compact layout, quick actions and an attention filter.
  - Console font and colours, recent destinations, and reset, export and import with a preview.
- **Catalogue**: Claude Code, Codex and Ollama, installed, updated, repaired and uninstalled only through winget (exact pinned ids, the `winget` source, per-user).
  - Each operation has a full plan and explicit approval.
  - The plan is fingerprinted and re-checked right before it runs.
  - Diagnose-first repair.
  - Update checks.
  - The Ollama gateway is listed.
  - Custom agent tools: Unverified until validated.
- **Unified download and installation queue.**
  - Stages: Queued, Resolving, Downloading, Verifying, Installing, Testing, Ready, Ready with limitations, Failed, Cancelled, Rollback required and Interrupted.
  - A post-install Testing stage decides Ready.
  - Pause and resume only where resumable.
  - Cancel, retry, repair, details/log and remove.
  - History survives restarts.
- **Model pipeline.** A 10-step plan before download. The Testing stage confirms the runtime lists the model, then runs the compatibility suite. Results are Ready, Ready with limitations ("Text Generation Available / Agentic Compatibility Failed") or Failed.
- **Template engine.**
  - 13 built-in, versioned schema-2 templates covering every category.
  - Validation: unknown fields refused, options allow-listed, secret scan.
  - Migration from 0.1.x stack templates.
  - Compatibility filtering with 20 checks, each with a corrective action.
  - Deterministic, fingerprinted configuration.
  - Preview, dry run, create stack and launch through the normal session service.
  - Duplicate, edit, reset, export and import.
- **Configuration transactions** for agent profile files:
  - Merge only the generated keys, keeping comments and unrelated settings.
  - Ownership tracking and conflict decisions.
  - Backup, atomic write, re-read and value check.
  - Health and integration checks, with rollback on failure.
  - "Roll back last change".
- **Reliability.**
  - New readiness states: runtime missing, authentication required, permission conflict, security policy blocked.
  - Every check carries evidence and a fix.
  - Diagnose-first stack repair plans.
  - Full verification with a safe end-to-end probe (disposable project, Read-Only, random token).
  - Strict classification ("Supported and Verified" only when everything is proven).
  - A per-PC supported-configuration matrix.
- **Useful errors**: what failed, the component, why, what completed, what was rolled back, whether user data was affected, the next step and a reference id. "Something went wrong" is gone.
- **SDK 1.1** (additive): templates, agent-tool catalogue entries, model sources, compatibility rules, health checks and settings panels.
  - New permission: CatalogueContributions.
  - Extension health checks appear on the Services page.
- New docs: DESIGN-SYSTEM.md and SUPPORTED-CONFIGURATIONS.md. Threat review added to SECURITY.md.
### Fixed
- A late progress report could overwrite a queue item's final state. Found by the new tests.
- Icon-only navigation bindings no longer depend on the window. Status reasons on the dashboard now match the status shown.
### Verification
- 398 automated tests, up from 268 (10 of them are opt-in live tests that run only when enabled). Self-test checks: 47.
- Live on the build PC: winget through the secure executor, source lookups and plans (nothing installed); the model pipeline end to end with `llama3.2` (Ready); the Ollama gateway probe.
- No stack is labelled Supported and Verified for 0.1.6 yet: neither agent is installed on the build PC.

## 0.1.5 — 2026-10-19
- **Installer fixed.** 0.1.4 and earlier declared a per-user MSI but pointed at `C:\Program Files\DarkCipher AgentControl`, so installs failed with "insufficient privileges". The MSI is now per-machine and installs to `C:\Program Files\DarkCipher Systems\AgentControl`, the same layout as the other DarkCipher Systems apps. Windows asks for administrator approval once; the app still runs unelevated. 0.1.4 is superseded.

## 0.1.4 — 2026-10-19 (superseded by 0.1.5: installer could not install)
- Added `tools\setup.ps1`: checks prerequisites and downloads all build dependencies (pinned .NET 8 SDK via `-InstallMissing`, WiX tool, locked NuGet restore). No application code changes.
- The in-app updater remains OFF (fails closed): no release signing key exists.

## 0.1.3 — 2026-10-18
- Same code as 0.1.2. The documentation no longer contains the development PC's user path (DarkCipher privacy gate).
- **0.1.2 is superseded; do not distribute it.** Its docs zip contained that path. It is kept in `dist\0.1.2` because release folders are never deleted.
- `build.ps1` now fails if the docs or release notes contain the build machine's user name.

## 0.1.2 — 2026-10-18 (superseded by 0.1.3)
### Added
- Release package. `dist\0.1.2\` contains:
  - Per-user MSI (WiX 5, no admin rights).
  - Self-contained portable zip.
  - SDK NuGet package.
  - `dcx-ext` tool zip.
  - The four sample extensions as `.dcxext`.
  - Documentation bundle.
  - CycloneDX SBOM and third-party licence list.
  - `SHA256SUMS.txt`, verified by the build.
- Opt-in live agent tests: the real Claude Code binary, launched headless through the secure executor from the app's own launch plan, in a disposable project. One test checks it answers through the Ollama gateway; the other checks the process tree can be started and stopped.
- A Quick Setup dry run in the self-test: real detection, the setup plan, adding the disposable sample project, a loopback-only session preview, and refusal of a cloud stack. The self-test now has 31 checks.
- Claude Code local mode passes the stack's real context window (`CLAUDE_CODE_MAX_CONTEXT_TOKENS`). Without it, Claude Code assumes 200K for unknown models.
- New documentation: user guide, administrator guide, privacy, troubleshooting, recovery, developer guide, extensions (user) and CHANGELOG.

### Fixed
- **The Quick Setup sample project could not be used by an agent.** The rule protecting the app data folder covered `test-projects` too. The rule now closes the data root itself plus config, audit, logs, backups, extensions and staging. `test-projects` and `agent-profiles` (Codex `CODEX_HOME`) stay usable. Found by the live agent test; regression test added.

## 0.1.1 — 2026-10-18
### Fixed
- NPU detection matched "INPUT" devices (USB input and audio devices were listed as NPUs).
- A non-backtracking regex size limit crashed start-up (private-key and model-name patterns).
- The extension host enumerated its registry while it was being modified (install racing start-up). Loads are now one per installed version.
- Compatibility suite 1.1.0: reasoning models get brief thinking and an explicit JSON prompt, so answers fit the test budget.
- Permission picker equality, the Package-install enforcement note, and the App project missing from the solution.

## 0.1.0 — 2026-10-18
- First build:
  - Eight layered assemblies and a 15-screen WPF UI.
  - Central security services.
  - Hardware discovery and recommendations.
  - Ollama runtime and compatibility suite.
  - Hugging Face source.
  - Claude Code and Codex providers.
  - Stacks, sessions, downloads and Quick Setup.
  - Extension platform and SDK 1.0, with `dcx-ext` and samples.
