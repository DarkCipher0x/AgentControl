# Administrator guide

## Deployment
- **Per-machine MSI** (`Scope=perMachine`), same layout as the other DarkCipher Systems apps:
  - Installs to `C:\Program Files\DarkCipher Systems\AgentControl`. Windows asks for administrator approval once; the app itself always runs unelevated.
  - Start-menu folder "DarkCipher Systems".
  - One HKLM value (`Software\DarkCipher Systems\AgentControl\StartMenu`) as the shortcut key path.
  - No services, drivers, firewall rules or scheduled tasks.
- **Silent install**: `msiexec /i DarkCipherAgentControl-<ver>-win-x64.msi /qn`. **Uninstall**: `msiexec /x <same msi> /qn` (or Settings → Apps).
- **Upgrades**: the MSI uses a fixed UpgradeCode with MajorUpgrade, so newer versions replace older ones and downgrades are refused.
- **Portable zip**: self-contained .NET 8 runtime; no prerequisites.
- **Signing**: binaries and the MSI are **not code-signed**. Verify `SHA256SUMS.txt` before deploying.

## What the app touches

| Location | Contents |
|---|---|
| `%LOCALAPPDATA%\DarkCipher\AgentControl\config` | Settings, stacks, projects, policy, protected-path user rules, extension registry (JSON; `.prev` copies) |
| `…\audit\security-audit.jsonl` + `audit.key` | Tamper-evident audit log; HMAC key protected with DPAPI (current user) |
| `…\logs` | Structured log, rotated 5 × 5 MB |
| `…\backups` | Backups of configuration files the app changed |
| `…\extensions`, `…\staging` | Installed extensions; temporary review extractions |
| `…\agent-profiles\<stack>` | Isolated agent configuration (Codex `CODEX_HOME`) |
| `…\test-projects` | Disposable sample projects from Quick Setup |
| Windows Credential Manager, targets `DarkCipher.AgentControl:*` | Secrets (for example a Hugging Face token) |

`DCAC_DATA_DIR` overrides the data root. It is intended for testing. The app refuses a data root inside `%USERPROFILE%\Projects` or on a network share.

## Security defaults (Security screen)
- Privacy **Fully Local**.
- **Third-party extensions disabled**.
- **Remote runtimes disabled**.
- HTTPS required for non-local endpoints.
- **Development Mode off**.
- Default agent permission profile **Ask Before Changes**.
- Built-in protected folders:
  - The app's sensitive data folders (closed to agents and extensions).
  - Windows and Program Files (read-only).
  - Credential folders: `.ssh`, `.gnupg`, `.aws`, `.azure`, `.kube`, `.docker`, Windows Credentials/Protect (closed).
- Default user rule: `%USERPROFILE%\Projects` read-only (removable).

## Network behaviour
- The app opens **no listening ports**.
- **Outbound traffic**:
  - Loopback (Ollama at `127.0.0.1:11434`) is always allowed.
  - Anything else needs per-endpoint approval: `huggingface.co` the first time you search, or anything you add in Security → Network.
- **Not routed through the app's policy**: Ollama downloads models itself. Agents (Claude Code, Codex) make their own connections; native modes talk to their vendors.
- The Services screen warns if Ollama is listening beyond loopback.

## Auditing
- Security → Audit log shows recent entries; **Verify integrity** checks the hash chain.
- The file is JSON lines with fields Timestamp, Action, Component, Result, AffectedResource, Classification, CorrelationId, Detail.
- Deleting the newest lines cannot be detected (no external anchor yet).

## Updates
The built-in updater is **disabled**: no release-signing key exists, and unsigned updates are never installed. Deploy new versions with the MSI.
