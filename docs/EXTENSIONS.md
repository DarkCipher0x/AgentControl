# Extensions (for users)

Extensions add stack templates, model catalogues, commands and dashboard panels. With SDK 1.1 (0.1.6) they can also add
schema-2 templates, agent-tool catalogue entries, model sources, compatibility rules, health checks (Services page) and
settings panels. Each needs its own permission:
- Catalogue entries, model sources and rules need **CatalogueContributions**; model sources also need **Network**.
- Health checks need **UI**.
- Settings panels need **Settings**.

Contributed tools are always listed as **Unverified (extension)**. Only this app installs them, using the exact winget
id they name, after showing you the full plan. Contributed templates never choose permissions: they always start at
"Ask Before Changes". SDK 1.0 extensions keep working.

## Before you install
- Third-party extensions are **disabled by default** (Security → Policy). You can still review and install one; it loads only after you allow third-party extensions.
- **Data-only (declarative)** extensions run no code at all.
- **Code** extensions run *inside* the app and are **not isolated**. They load only when Development Mode is on (Developer / SDK), and a warning badge stays visible while it is on. Install code extensions only if you wrote or fully trust them.
- Packages are **not signed**: there is no publisher key infrastructure yet. Compare the package's SHA-256 with the one its author published.

## Gallery (bundled with the app)
Extensions → **Gallery** lists data-only extensions that ship inside the app's install folder, published by DarkCipher Systems:

| Id | Contains |
|---|---|
| `darkcipher.gallery.stacks-python` | Python stacks (Codex + Ollama, read-only review) |
| `darkcipher.gallery.stacks-web` | Web and TypeScript stacks |
| `darkcipher.gallery.stacks-rust` | Rust stacks |
| `darkcipher.gallery.stacks-small-gpu` | Stacks for 8 GB GPUs and laptops |
| `darkcipher.gallery.stacks-claude-gateway` | Claude Code local-gateway stacks (Experimental) |
| `darkcipher.gallery.models-coding` | Coding model shortlist |
| `darkcipher.gallery.models-lightweight` | Lightweight model shortlist |

*Install* packs the folder with the same packager as `dcx-ext pack` (staging copy, `files.sha256`, zip, then verified as the host will). The host reviews it through a **bundled** path. That path runs every normal check (safe extraction, manifest, integrity, versions) and also requires data only and the `darkcipher.gallery.` id prefix. Only then may the package use the reserved DarkCipher Systems identity. Gallery extensions install as **Verified** (bundled), are not separately signed, request no permissions, and still load when third-party extensions are disabled (Safe Mode still stops them). The same packages are refused through *Install from file…* and by `dcx-ext pack`: the reserved identity is anti-spoofing.

## Install
1. Extensions → **Install from file…** → choose a `.dcxext`.
2. The review screen shows:
   - Id, version and publisher.
   - Trust level and signature status.
   - Isolation.
   - Licence and source.
   - Every requested permission, with what it allows and its risk.
   - Declared network endpoints.
   - Warnings.
3. **Install and grant**, or cancel. Rejected packages show why. Examples: missing integrity list, executable files inside, a reserved publisher name, a downgrade, an incompatible SDK, or a path that escapes the package.

## Manage
- **Enable / Disable / Remove**. Built-in components cannot be disabled.
- **State column**:
  - Ready, Disabled (with the reason), Failed (with the error; 3 failed starts disable it automatically).
  - Blocked: files changed after installation.
  - Incompatible.
- **Contributed commands and panels** run with **Run** and a 15-second limit.
- **Right-click** an extension for Enable, Disable, Details, Open install folder, Copy id and Remove.

## Samples shipped with each release
| Package | Kind |
|---|---|
| `com.example.rust-stacks` | Data-only |
| `com.example.coding-catalog` | Data-only |
| `com.example.agent-detector` | Code; needs Development Mode |
| `com.example.model-panel` | Code; needs Development Mode |

Writing your own: see `docs/sdk/README.md`.
