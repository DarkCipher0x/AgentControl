# User guide

DarkCipher AgentControl helps you run AI coding agents (Claude Code, Codex) with local models (Ollama) safely. It shows
what will happen before anything happens.

## Install
- **MSI**: run `DarkCipherAgentControl-<version>-win-x64.msi`.
  - It installs for all users into `C:\Program Files\DarkCipher Systems\AgentControl`, next to the other DarkCipher Systems apps, and adds Start-menu shortcuts (normal and Safe Mode). Windows asks for administrator approval once during install; the app itself never runs elevated.
  - The installer is **unsigned**, so Windows SmartScreen may say "Windows protected your PC". Check the SHA-256 against `SHA256SUMS.txt` first, then choose *More info → Run anyway*.
- **Zip** (portable): extract `DarkCipherAgentControl-<version>-win-x64.zip` anywhere you own and run `DarkCipherAgentControl.exe`. It is self-contained, so no .NET install is needed.
- **Requirements**: Windows 10/11 x64 with winget (App Installer, included in current Windows). Quick Setup and the Catalogue install Ollama, Codex and Claude Code for you through winget, per-user, each after you approve its install plan. Without winget, install them from their vendors' official instructions.

## First run: Quick Setup ("Get My Local Coding AI Ready")
1. **Welcome**: nothing changes yet.
2. **Hardware scan**: a rating (for example "Excellent for local AI") and the reasons. Virtual adapters such as Parsec are ignored. Detected NPUs are listed as *detected only*.
3. **Software detection**: whether Ollama is running, and whether Claude Code or Codex is installed. Anything missing is installed in step 7; you do not need to install it yourself.
4. **Recommended setup**:
   - Choose a plan: Balanced, Lightweight, High Quality, CPU-Only, Hybrid or Advanced.
   - Choose an agent:
     - *Codex + Ollama* is fully local and documented by OpenAI.
     - *Claude Code + local gateway* is **Experimental**: Anthropic does not support it, and the model is not Claude.
5. **Model**: recommendations with memory estimates and limitations. Already-installed models are marked. No speed figures are shown until you test on this PC.
6. **Plan**: every step, download size and file location, shown *before* anything happens. Missing components appear as *Install Ollama*, *Install Codex CLI* or *Install Claude Code* (winget, per-user, no administrator rights), and a stopped Ollama as *Start Ollama*. Approve to continue.
7. **Install**:
   - Detects everything again, so it acts on the current state of the PC.
   - Installs missing components through the same path as the Catalogue. Each install opens its own approval with the exact winget package, version, source and changes, runs in the Downloads queue, and counts as done only after its post-install test passes.
   - Starts Ollama with its own app if it is installed but not running (as if you opened it from the Start menu). It keeps running after this app closes.
   - Downloads the model through Ollama, which verifies it.
   - Creates your stack with the *Ask Before Changes* permissions.
   - Creates a disposable sample project.
   - If something cannot be done automatically (you decline an install, winget is missing, or an install fails its test), it stops with the reason. **Retry** detects again and continues from where it stopped.
8. **Verify**: runs the compatibility tests (text, instructions, code, JSON, tool calls) with fixed prompts on your PC.
9. **Ready to code**: starts your first session in the sample project, in its own terminal window.

You can press **Cancel**, **Back** or **Rescan** at any point. If a step fails you get **Retry**, **Change model**, **View logs** and **Cancel**.

## Everyday use
**Install from where you are.** Every tool, runtime, gateway, model and bundled extension can be installed from its own page, not only from the Catalogue: Agents lists every agent tool, Services every runtime and gateway, Models has a Download button on every model, and Extensions has a Gallery. All of them use the same plan → approval → Downloads queue → post-install test path.

**Right-click anything.** Every list (tools, models, downloads, stacks, templates, projects, sessions, extensions) has a right-click menu with what you can do to that item: install or update, open its homepage or folder, copy its name, id, path or install command, and remove it (always last, in red). Short menus show everything. Crowded menus keep the main actions at the top and group the rest into submenus such as *Open*, *Copy* and *Manage*. The keyboard Menu key (or Shift+F10) opens the menu for the selected item.

- **Dashboard**: stack readiness with the reason for each status, runtime health, agents, loaded models, recent blocked operations.
- **Projects**: add the folders you allow agents to work in. Folders in protected locations show what is allowed.
- **Sessions**:
  1. Pick a stack and a project.
  2. Read the preview: backend, folder, permissions and the exact command, with secrets hidden.
  3. Press *Start session*.
  - Stop and Restart work only for sessions started here. *Copy command* never includes secrets.
- **Models**:
  - Installed models, with *Test compatibility*, *Test Claude Code gateway*, *Details*, *Unload* and *Remove* (refused while loaded).
  - Recommendations for this PC, each with a **Download** button.
  - A curated Ollama library with a **Download** button on every row, and *Download by name* for any Ollama tag (for example `qwen2.5-coder:7b`).
  - The button follows the download live: *Download* → *Queued* → percentage → *Testing* → *Installed*. A paused, interrupted or failed download shows *Resume* or *Retry*, which continue from the layers Ollama already has. Unfinished downloads are also listed at the top of the page.
  - Hugging Face search: you approve huggingface.co the first time, it is metadata only, and nothing from a repository is ever run.
  - The compatibility matrix.
- **Stacks**: built-in templates are read-only; *Duplicate* to customise.
  - *Test readiness* checks the agent and its version, the mode, privacy, permissions, network policy, the runtime, the model, tool use, context and the gateway.
  - *Diagnose* lists only real problems, each with its evidence, a minimal fix, the privileges needed, rollback and how the fix is checked. *Do this* opens the fix.
  - *Run full verification* runs the model tests, generates and checks the configuration, then runs a safe end-to-end test: the agent answers a fixed prompt in a new sample folder with Read-Only permissions. The result is labelled strictly; "Supported and Verified" only when everything passed.
  - *Roll back last change* restores the stack's agent profile file.
  - *Export* and *Import*: imports are treated as untrusted (permissions reset, remote endpoints refused).
- **Catalogue**: tabs for *Available* (everything not installed yet), *Agent tools*, *Model runtimes*, *Gateways*, *Supporting tools*, *Installed* and *Updates*, with a search box.
  - Agent tools: Claude Code and Codex (full stack and session support), plus GitHub Copilot CLI, OpenCode, Crush, Kilo CLI and Amp CLI.
  - Model runtimes: Ollama (runs stacks), plus LM Studio, llama.cpp, Jan and llamafile.
  - Gateways: the API servers built into those runtimes (Ollama's Anthropic- and OpenAI-compatible APIs, LM Studio's local server, llama-server, Jan's local API). Installing a gateway installs its runtime; *not running* means its local port is closed.
  - Supporting tools agents use: Git, Node.js LTS, Python 3.13, uv, ripgrep, fd, jq and the GitHub CLI.
  - Items marked **Catalogue only** can be installed, updated, detected and removed, but no stack or session uses them yet.
  - Each tool shows its publisher, pinned winget source, licence, installed, available and verified versions, protocols and trust.
  - *Install*, *Update* and *Uninstall* always show the full plan (downloads, privileges, files, PATH and environment changes, services, licence, rollback) and wait for your approval. Nothing installs silently, and installs never need administrator rights.
  - *Diagnose / repair* only reinstalls a tool that is actually broken.
  - *Check for updates* asks winget.
  - *Custom tools* (Advanced) lets you register your own agent CLI. It stays Unverified until its validation passes.
- **Templates**: pick an agent (and optionally a mode, model, project, permissions, context and reasoning level). Only templates that fit are listed, each with every check (agent, versions, runtime, protocol, model, tool calling, structured output, context, hardware, disk, permissions, folder, services, network, privacy) and a fix button.
  - *Launch* is enabled only when no check failed; the reason is shown when it is not.
  - *Dry run* shows everything and changes nothing.
  - *Preview configuration* shows the generated settings and their fingerprint. The same choices always give the same fingerprint.
  - *Create stack*, *Duplicate*, *Export*, *Import*, *Reset to default* and *Delete* (your own templates only).
- **Downloads**: one queue for model pulls, Hugging Face imports and tool installs. It shows the type, source, version or tag, stage, installation state, progress, bytes, speed, ETA, verification result, location and errors.
  - Stages: Queued → Resolving → Downloading → Verifying → Installing → Testing → Ready / Ready with limitations / Failed / Cancelled / Rollback required.
  - An item is Ready only after its test passes, never just because it downloaded.
  - *Pause* and *Resume* work only for downloads that can resume (Ollama keeps finished layers).
  - *Cancel*, *Retry*, *Repair*, *Details / log* and *Remove entry* (which uninstalls nothing).
  - History survives restarts; anything interrupted is marked Interrupted and never resumed silently.
- **Settings → Appearance, Navigation, Dashboard, Console**: theme presets (DarkCipher, High Contrast, Graphite, Teal, Light, Follow Windows), accent colour (must stay readable), density, text size, zoom, corners, animation, sidebar style (expanded, compact or icons only), pinned and hidden pages, landing page, dashboard cards and quick actions, and console font and colours. Reset per category, export and import with a preview. The *Customise* button on the Dashboard reorders, resizes and hides cards in place. See DESIGN-SYSTEM.md.
- **Agents**: each agent's modes, and for every permission, *who* enforces it (this app, the agent, your approval, or nobody). Below that, every agent tool in the catalogue with *Install* or *Update*.
- **Services**: Ollama (with *Start Ollama*), loaded models, and every model runtime and gateway with *Install* or *Update*.
- **Extensions**: a **Gallery** of bundled, data-only extensions (stack templates for Python, web, Rust, 8 GB GPUs and Claude Code's local gateway, plus model shortlists). Install opens the normal review. Nothing is downloaded: they ship with the app.
- **Logs**: search, filter, copy, export. Secrets are removed before anything is written.

## Permission profiles

| Profile | In short |
|---|---|
| Read-Only | Read and answer only |
| Ask Before Changes (default) | Reads freely and asks before each edit or command |
| Project Workspace Only | Edits inside the project without asking; commands ask |
| Standard Development | Edits without asking; commands, git and installs ask |
| Advanced Development | Most commands without asking; never bypasses the agent's own safety checks |

The agent enforces most of these itself. The Agents screen labels each one honestly.

## Privacy modes
Set the privacy mode in Security → Policy:
- **Fully Local** (default): prompts and code never leave this PC or your approved local network.
- **Local Preferred**: cloud inference blocked.
- **Hybrid**: cloud stacks allowed when you pick one.
- **Cloud Allowed**.

The header badge always shows the current mode.

## Safe Mode
Start with the *Safe Mode* shortcut (or `--safe-mode`) to load built-in components only.
