# Architectural decision records

## ADR-001 .NET 8 + WPF, layered assemblies (2026-10-18)
- **Context.** The ServerManagement product uses WPF on .NET 8. The app needs P/Invoke for Credential Manager, Job Objects, WinVerifyTrust and final-path resolution.
- **Decision.** WPF and .NET 8, with eight assemblies and a strict one-way dependency graph (see ARCHITECTURE.md).
- **Why.** It matches the existing DarkCipher stack and skills. First-class Windows interop. No Node or Electron runtime to secure.
- **Alternatives considered.** WinUI 3 (packaging friction), Avalonia (no gain on Windows-only), Electron (larger attack surface).

## ADR-002 Security services own every privileged operation (2026-10-18)
- **Decision.** Domain defines ports. Only `DarkCipher.AgentControl.Security` implements process, network, credential, audit, path and configuration operations.
- **Enforcement.** Architecture tests inspect assembly metadata and fail if any other layer references `HttpClient`, sockets or `Process`.
- **Consequence.** Adapters (Ollama, Hugging Face, agents) cannot bypass policy, by construction.

## ADR-003 Canonical paths via the OS (2026-10-18)
- **Decision.** Every path goes through the same steps:
  1. Lexically normalise (`GetFullPath`, trailing dots and spaces).
  2. Walk up to the deepest existing ancestor.
  3. Resolve it with `GetFinalPathNameByHandle`, which follows junctions and symlinks and expands 8.3 names.
  4. Re-append the non-existent tail.
- Containment is segment-aware. ADS, device paths, GLOBALROOT and reserved names are refused.
- **Alternatives considered.** String prefix checks, which are bypassable. DIY reparse parsing, which is fragile.

## ADR-004 Processes: direct invocation, Job Objects, no shells (2026-10-18)
- **Decision.** Processes start with `UseShellExecute=false` and `ArgumentList`, and must be absolute `.exe`/`.com` files. The executor refuses:
  - Batch and script files.
  - Shell interpreters, unless application code opts in. Agents and extensions never can.
  - Executables in temp, Downloads or on network shares.
- Each process gets its own Job Object. Captured processes use kill-on-close; interactive agent sessions survive the app but can be stopped as a tree.
- **Why.** cmd.exe re-parses arguments ("BatBadBut"), so list quoting cannot make batch files safe.

## ADR-005 Network: deny by default, approve per endpoint (2026-10-18)
- **Decision.** Loopback is always allowed. Every other destination is classified without DNS. Unknown names are never treated as local.
- Non-loopback destinations need explicit approval. HTTPS is required except for approved LAN addresses.
- Privacy mode governs inference. Redirects are manual and re-checked on every hop. Credentials are attached only to the original HTTPS or loopback host.
- **Alternatives considered.** DNS-based classification (leaks names, unreliable with split-horizon DNS).

## ADR-006 Secrets in Windows Credential Manager (2026-10-18)
- **Decision.** Secrets are stored as `CRED_TYPE_GENERIC` with `CRED_PERSIST_LOCAL_MACHINE` (per user, never roams). The rest of the app sees only opaque `CredentialReference`s.
- Values are revealed only inside the Security assembly, for two purposes: child-process environments and outgoing auth headers.
- Every stored secret is registered with the redactor.

## ADR-007 Claude Code local mode is an honest, labelled gateway mode (2026-10-18)
- **Decision.** Local mode uses Ollama's documented Anthropic-compatible endpoint and environment variables: `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN=ollama`, and `ANTHROPIC_API_KEY` set to empty.
- It is always labelled Experimental. The label becomes "Verified Local Gateway" only after a real text and tool-use probe passes for the chosen model. The UI always shows "NOT Claude" and the actual model.
- Non-essential traffic is disabled. The app never generates `bypassPermissions`.

## ADR-008 Codex local mode uses an isolated CODEX_HOME (2026-10-18)
- **Decision.** Local Codex sessions get `CODEX_HOME=<app data>\agent-profiles\<stack>` with a generated `config.toml`. It uses only documented keys: `model`, `oss_provider`, `model_context_window`, `sandbox_mode`, `approval_policy`. The session runs with `--oss -m <model>`.
- The user's `~/.codex` is never read or changed. `danger-full-access` and approval `never` are never generated.

## ADR-009 Extension isolation tiers (2026-10-18)
- **Decision.**
  - Declarative (data-only) extensions are the default kind and run no code.
  - Code extensions load in a collectible AssemblyLoadContext that shares only the SDK and refuses references to app internals. They require Development Mode and are labelled "NOT isolated".
  - Every capability goes through the broker.
- **Why.** .NET has no in-process sandbox, so claiming isolation would be false. Out-of-process low-integrity hosting (as ServerManagement does for add-ons) is the Planned next tier.

## ADR-010 Compatibility is measured, never assumed (2026-10-18)
- **Decision.** "Agentic compatible" means a fixed-prompt suite passed against the exact model digest on this PC: generation, instructions, code, JSON schema and tool calls are required.
- Results carry the runtime version, suite version, hardware and date.
- Speed figures come only from Ollama's eval counters in those runs.
- For reasoning models, the suite asks for brief reasoning (`think`) so answers fit the test budget. This was found during live verification (suite 1.1.0).

## ADR-011 Recommendations from memory budgets, calibrated by measurement (2026-10-18)
- **Decision.** Estimated memory = weights + KV cache (f16, worst case) for the profile's context + runtime overhead. It is compared against:
  - GPU budget: dedicated VRAM (registry `qwMemorySize`, not WMI's 4 GB cap) minus 1 GiB.
  - RAM budget: total minus 8 GiB.
- When Ollama reports the model loaded (`/api/ps`), the estimate is calibrated from that real figure.
- No speed claims are made.
