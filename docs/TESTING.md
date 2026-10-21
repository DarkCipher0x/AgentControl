# Testing

Scratch data lives in the developer scratch folder, `DCAC_DEV_SCRATCH` (default `%USERPROFILE%\.dcac-dev`), under
`tests\<random>`, and is deleted afterwards. It is deliberately outside the repository: the repository can sit in
`%USERPROFILE%\Projects`, which the app protects by default, so scratch data there would be refused by the code under test.
It is also kept out of AppData, because packaged (MSIX) hosts redirect AppData writes and that breaks the junction tests.
Nothing is written to a real project; tests that involve `%USERPROFILE%\Projects` only *evaluate* paths there.

| Suite | Where | What it proves |
|---|---|---|
| Path security | `Security/PathSecurityTests.cs` | Canonicalisation (`..`, `/`, `\\?\`, trailing dots, case, real 8.3 names, real junctions via mklink), ADS, device and reserved names, segment-aware containment, every protected-path rule, audit of violations |
| Process security | `Security/ProcessExecutionTests.cs` | No PATH lookup, batch/script/shell refusal, temp-dir refusal, environment injection refusal, NUL refusal, protected working folders, metacharacters passed as data, timeout and cancellation kill the tree, duplicate refusal, Stop touches only owned processes, bounded output, preview redaction, quoting round-trip through `CommandLineToArgvW` |
| Network security | `Security/NetworkSecurityTests.cs` | Classification without DNS, deny-by-default, HTTPS rules, Fully Local inference block, redirect downgrade and host checks, raw auth header refusal, size caps, scheme refusal, remote-runtime opt-in (fake handler; no real network) |
| Redaction and audit | `Security/RedactionAndAuditTests.cs` | Token shapes, PEM, registered secrets, linear time on hostile input; audit chain survives restart and detects edits and removals; DPAPI-protected key |
| Packages, config, updates | `Security/PackageConfigUpdateTests.cs` | Zip-slip, absolute paths, ADS, device names, symlink entries, zip bombs (actual bytes and ratio), entry limits, cleanup; preview/apply/backup/rollback, stale-preview refusal, protected targets; signed update manifests, downgrade, tamper, foreign key and host refusal |
| Credentials and persistence | `Security/CredentialAndPersistenceTests.cs` | Real Credential Manager round trip (test-only prefix, cleaned up), redaction of stored secrets, id injection; malformed-config quarantine and recovery; data root refused inside Projects; policy persistence and High audit on weakening; Safe Mode lock |
| Providers | `Integrations/ProviderTests.cs` | Claude Code env and flags (never bypass), Codex isolated CODEX_HOME and config keys (never full access), TOML escaping, Ollama parsing (real captured JSON), pull progress and errors, delete-while-loaded refusal, exposure warning, model-name rules, Hugging Face id and quant parsing, search request shape |
| Application | `Application/*.cs` | Hardware rating, virtual GPU filtering, registry memory parsing, NPU classification, recommendation fit and calibration, HF import planning, stack validation and untrusted import, session launch rules (read-only folders forced Read-Only and approval required, cloud blocked in Fully Local, profile writes confined) |
| Extensions and SDK | `Extensions/*.cs`, `Architecture/*` | Manifest rules, install/review, tamper → Blocked, downgrade refusal, zip-slip packages, third-party switch and Safe Mode, code extensions need Development Mode, broker permission denials, settings limits; **real code-extension loading of the compiled samples**; SDK surface contract |
| Architecture | `Architecture/ArchitectureTests.cs` | Dependency direction, no project cycles, no UI references below the App, no HttpClient/Socket/Process types outside Security (metadata inspection) |
| Live (opt-in) | `Integrations/LiveOllamaTests.cs` | Real compatibility suite and Anthropic-gateway probe against local Ollama. Set `DCAC_LIVE_OLLAMA=1` and `DCAC_LIVE_MODEL=<model>`. |

Commands:
```powershell
$env:TEMP = "$env:USERPROFILE\.dcac-dev\tmp"; $env:TMP = $env:TEMP; New-Item -ItemType Directory -Force $env:TEMP | Out-Null
dotnet test .\tests\DarkCipher.AgentControl.Tests
.\tools\run-selftest.ps1          # headless app self-test + off-screen PNG renders in %USERPROFILE%\.dcac-dev\selftest\report\shots
```

| Live agent (opt-in) | `Integrations/LiveAgentTests.cs` | Real Claude Code binary, launched headless through the secure executor from the app's own plan, in a disposable project under the developer scratch folder. One test checks it answers through the Ollama gateway; the other checks start, duplicate refusal and stopping the tree. Set `DCAC_LIVE_AGENT=1`. Claude Code records the folder in its own `~/.claude.json`. |

## Results for 0.1.2 (2026-10-18)
- `dotnet test`: 268 passed, 0 failed. That is 264 offline tests plus 4 opt-in live tests, which return immediately unless enabled.
- Live tests, run separately with the variables set:

| Live test | Result |
|---|---|
| `Live_compatibility_suite` | Passed (see the model table below) |
| `Live_anthropic_gateway_probe` | Passed: text and tool_use with gpt-oss-64k |
| `Live_claude_code_answers_through_the_local_gateway_headless` | Passed: Claude Code 2.1.296 answered "READY" via gpt-oss-64k on 127.0.0.1, exit 0, 35 s including model load |
| `Live_claude_code_session_can_be_started_and_stopped_as_a_tree` | Passed |

- Self-test: 31 ok, 0 failed, including the Quick Setup dry run. No dialogs opened. `build.ps1` also runs the self-test against the exe extracted from the MSI.

## Earlier results (0.1.1)
- Live suite (Ollama 0.40.6, RX 9070 XT):

| Model | Verdict | Notes |
|---|---|---|
| gpt-oss-64k | **Agentic Compatible** | 51.8 tok/s measured in that run |
| qwen3:14b | **Agentic Compatible** | 78.5 tok/s |
| gemma3:4b | **Text Generation Available / Agentic Compatibility Failed** | Ollama: "does not support tools" |

- Anthropic gateway probe with gpt-oss-64k: text and tool_use both passed.
- The "TTFT" figure includes model load time when Ollama had to swap models.
