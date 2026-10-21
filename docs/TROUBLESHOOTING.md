# Troubleshooting

| Symptom | Cause | What to do |
|---|---|---|
| "Ollama is installed but not running" | The Ollama app (or AMD AI Bundle) is not started | Quick Setup starts it for you, and so does *Fix* on a Stacks or Templates check (it opens Ollama's own app, which keeps running after this app closes). Or start it from the Start menu and press Refresh. The app never stops or reconfigures Ollama. |
| "Ollama is not installed" | No `ollama.exe` in the official, AMD AI Bundle or PATH locations | Install from https://ollama.com, then Rescan. |
| Warning: Ollama is listening on all interfaces | `OLLAMA_HOST` is set to `0.0.0.0` | Unset `OLLAMA_HOST` (or set it to `127.0.0.1`) and restart Ollama, unless you intend LAN access. |
| Stack shows "Agent missing" | Claude Code or Codex not found | Install the native build. npm `.cmd` shims are deliberately not launched (batch files cannot be quoted safely). Then Agents → Rescan. |
| Stack shows "Configuration error: … Fully Local mode" | A cloud (native) stack in Fully Local mode | Switch the privacy mode in Security, or use a local stack. |
| "Text Generation Available / Agentic Compatibility Failed" | The model cannot make tool calls (for example gemma3) or fails JSON | Choose a model with tool support (Models → filter "Tool calling"). |
| Session refused: "Path refused" or "is protected" | The folder is protected or is a credential/system folder | Pick another folder, or change the rule in Security → Protected folders (audited). |
| Session asks for approval every time | The folder is read-only protected (for example the Projects tree), a drive root, your profile root, removable or network | Expected behaviour. Read-only folders also force the Read-Only profile. |
| "This session is already running" | Duplicate launch of the same stack and project | Use Sessions → Stop, or close the running session window. |
| Hugging Face search asks for approval | huggingface.co is not approved yet | Approve it (metadata only). Revoke in Security → Network. |
| "Not enough free disk space" | Free space on the Ollama model drive is under size + 10% | Free space or change `OLLAMA_MODELS`. |
| Extension shows Disabled | Third-party extensions off, Safe Mode, code extension without Development Mode, or 3 failed starts | See the reason column on the Extensions screen. |
| Extension shows Blocked | Files changed after installation (tamper check) | Remove it and reinstall from a trusted package. |
| Yellow banner about recovered settings | A config file was unreadable; it was quarantined and the previous copy or defaults used | See RECOVERY.md. The damaged file is kept as `*.corrupt-<time>`. |
| Claude Code (local) prints "isn't described by this version's model catalog" | Claude Code does not know local model names | Informational. The app passes the real context window (`CLAUDE_CODE_MAX_CONTEXT_TOKENS`). |
| SmartScreen warns about the installer | The MSI is unsigned | Verify SHA-256 against `SHA256SUMS.txt`, then More info → Run anyway. |

**Logs**: Logs screen (search by correlation id), or `%LOCALAPPDATA%\DarkCipher\AgentControl\logs\agentcontrol.log.jsonl`.
