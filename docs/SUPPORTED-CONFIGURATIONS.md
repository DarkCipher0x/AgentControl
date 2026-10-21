# Supported configuration matrix

Matrix version **2** · app version **0.1.7** · last updated **2026-10-20**.

The app keeps its own, per-PC matrix (Stacks → Run full verification). Each entry records the exact combination, what
was tested and when, and Dashboard → "Verified configurations" shows it. This document is the project-level record of
what was verified while building the app. **No combination is claimed beyond the exact versions listed.** One success
never implies that other versions, models or PCs work.

## Labels

| Label | Meaning |
|---|---|
| **Supported and Verified** | Every requirement was proven AND the agent answered a fixed prompt in a disposable project (safe end-to-end test). |
| Ready with Limitations | Proven, but with a recorded limitation (for example, the model does not fit fully in GPU memory). |
| Experimental | Works end to end, but the path itself is experimental (for example, Claude Code through a local gateway, which Anthropic does not support). |
| Compatible on Paper | The components fit by metadata; model capabilities were not proven on this PC. |
| Not Yet Tested | Requirements met, but the end-to-end test was not run. |
| Failed Validation | A requirement or the end-to-end test failed. |
| Unsupported | Refused by design. |

## Verified combinations

| Result | OS | App | Agent | Runtime / gateway | Model | Template | Context | Tool calls | Structured output | Install | Launch | E2E | Verified | Limitations |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Experimental | Windows 11 Pro x64 (build 26300) | 0.1.2 | Claude Code 2.1.296 (native installer) | Ollama, Anthropic-compatible API (loopback) | gpt-oss-64k (gpt-oss:20b with 64K context) | Claude Code + Local Gateway stack (pre-template) | 65,536 | passed (gateway test) | n/a | pre-installed | started and stopped as a job tree | answered `READY` headless | 2026-10-18 | Not supported by Anthropic; the model is not Claude. This predates the 0.1.6 verification flow, so it is a historical record, not a 0.1.6 matrix entry. |

**0.1.7 on the build PC (2026-10-20, AMD Ryzen 9 5950X, Radeon RX 6600 8 GB, Ollama 0.40.6).** Codex and Claude Code
were installed by the app's own catalogue path (plan → approval → queue → winget `--scope user` → Testing stage). Then
each stack was generated from a template and passed through the full verification (readiness → compatibility suite →
deterministic configuration → validation → headless end-to-end test in a disposable project, Read-Only profile).

| Result | OS | App | Agent | Runtime / gateway | Model | Template | Context | Tool calls | Structured output | Install | Launch | E2E | Verified | Limitations |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Supported and Verified** | Windows 11 Pro x64 (build 26300) | 0.1.7 | Codex CLI 0.161.0 (winget `OpenAI.Codex`, per-user) | Ollama 0.40.6 (loopback, Codex OSS provider) | llama3.2:latest (digest `a80c4f17acd5`) | Code Review (read-only) 1.0.0 | 32,768 | passed | passed | installed by the catalogue | `codex exec` headless | answered the random token in 6.7 s | 2026-10-20 | The template's reasoning level is left out: llama3.2 does not support thinking. Codex enforces its own sandbox. |
| Failed Validation | Windows 11 Pro x64 (build 26300) | 0.1.7 | Claude Code 2.1.302 (winget `Anthropic.ClaudeCode`, per-user) | Ollama 0.40.6, Anthropic-compatible API (gateway test passed) | llama3.2:latest | Claude Code + local gateway (Experimental) 1.0.0 | 65,536 | passed | passed | installed by the catalogue | `claude -p` headless, exit 0 | **failed**: the model did not answer with the token. It narrated sub-agent plans instead. | 2026-10-20 | A 3B model is too small to drive Claude Code's agent prompt. Expected to need a larger tool-capable model, for example qwen2.5-coder:7b or gpt-oss:20b, which is not yet tested. |

Bugs that this live run found and that 0.1.7 fixes:
- **Codex was not detected after a winget install.** winget put no alias in `WinGet\Links`, and the package's exe has a
  non-standard name. Detection now also reads the package folder's entry point under
  `%LOCALAPPDATA%\Microsoft\WinGet\Packages\<Id>_*`.
- **Codex rejected every request on llama3.2** with "does not support thinking". The template's reasoning level became a
  thinking request. The configuration builder now leaves the reasoning option out, with a warning, when the model does
  not report the thinking capability. Template compatibility shows the same warning.

Earlier (0.1.6, before any agent was installed) the following was also proven live:

| Component | Result | Evidence |
|---|---|---|
| Model pipeline (unified queue → Ollama pull → digest check → Testing stage → suite) | **Ready** | `llama3.2:latest` (3.2B Q4_K_M, digest `a80c4f17acd5`). Suite 1.1.0 passed: generation, instructions, code, JSON schema, tool calling, context and cancellation. Multi-turn (not required) failed. About 159 tok/s, first token in 53 ms. |
| Ollama Anthropic-compatible gateway | Agentic Compatible | Live probe with `llama3.2:latest`: text and tool use worked through `/v1/messages`. |
| winget path (no install performed) | Works | winget v1.29.380 runs through the secure executor. The source resolves `Anthropic.ClaudeCode` 2.1.302, `OpenAI.Codex` 0.161.0 and `Ollama.Ollama` 0.40.6. Install plans are exact-id and per-user. Ollama's update plan is correctly blocked (already newest). |

## Compatible but not yet verified

| Combination | Why it is not verified |
|---|---|
| Codex CLI 0.161.0 + Ollama 0.40.6 + any model other than llama3.2, or any Codex template other than Code Review | Only the exact row above was run. Other models and templates have not been tested. |
| Claude Code 2.1.302 + Ollama gateway + a 7B-or-larger tool-capable model, template "Claude Code + local gateway" | Only llama3.2 is installed on the build PC, and it failed (above). No larger model has been pulled for testing. |
| Claude Code native, template "Hybrid: Claude native" | Cloud. The end-to-end test sends a prompt to Anthropic and needs your approval and the Hybrid or Cloud privacy mode. |
| Hugging Face GGUF imports (single-file, ungated) | The import path is Ollama's `hf.co/…` pull, which is the same pipeline as above. No live import was run in 0.1.6. |

## Catalogue only (0.1.9)
These can be installed, updated, detected and removed through the catalogue. No stack or session provider uses them, so none of them is part of any verified configuration:
- Runtimes: LM Studio, llama.cpp, Jan, llamafile (and their built-in gateways).
- Agent CLIs: GitHub Copilot CLI, OpenCode, Crush, Kilo CLI, Amp CLI.
- Supporting tools: Git, Node.js LTS, Python 3.13, uv, ripgrep, fd, jq, GitHub CLI.

Verified live on 2026-10-20 without installing anything: every package resolves in the winget source with a per-user installer, and detection and install/update planning work against the real winget. Git 2.55.0.5 and ripgrep 15.2.0 were detected as installed on the build PC. No new package was actually installed by this app during testing.

## Unsupported (refused by design)

- Claude Code through a gateway that is not on this PC (only loopback gateways are allowed).
- Gated, private, split or non-GGUF Hugging Face repositories; repositories that need custom code.
- Starting sessions with custom (user-registered) or extension-contributed agent tools. Registration and validation work; launching them is Planned.
- Installing anything that is not a pinned winget package from the reviewed `winget` source.
- Automatic fallback between local and cloud models (templates can only *suggest* a fallback).
