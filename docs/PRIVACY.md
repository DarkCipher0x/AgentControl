# Privacy

- **No telemetry.** The app sends no usage data, crash reports or analytics anywhere. It has no account and no cloud backend.
- **No listening ports.** The app exposes no network services.
- **Where inference runs** is always shown: the header badge, session previews ("Backend: …") and stack descriptions.
  - In **Fully Local** mode (default), the network service refuses to send inference traffic anywhere except this PC or approved local-network endpoints.
  - The session launcher refuses cloud stacks.
- **Data the app sends, and only when you act**:
  - **Ollama on 127.0.0.1**: management calls and fixed test prompts (compatibility tests). Never your source code.
  - **huggingface.co**, after you approve it: search text and repository ids. A token is sent only if you stored one.
- **Data agents send** (outside this app's control):
  - Claude Code and Codex **native** modes send your prompts and code to Anthropic or OpenAI. You approve each native session, and it is audited.
  - **Local** modes talk to Ollama on loopback. Claude Code local mode sets `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1`.
- **Ollama** downloads models from its registry or Hugging Face itself when you start a download.
- **Logs and audit**: secrets are redacted (known token formats and every credential you store). Source code, prompts and model responses are not logged. The compatibility tests' fixed prompts and short answers appear in test results only.
- **Credentials**: Windows Credential Manager, your user only, never roaming, never exported. Stack exports contain no secrets by design.
- **Remove everything**: see RECOVERY.md → "Remove all data".
