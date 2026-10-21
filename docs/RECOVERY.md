# Recovery

## The app will not start properly
1. Start in **Safe Mode** (Start-menu shortcut, or `DarkCipherAgentControl.exe --safe-mode`). Only built-in components load.
2. Check `%LOCALAPPDATA%\DarkCipher\AgentControl\logs\agentcontrol.log.jsonl`, and the Windows Application event log (source ".NET Runtime").

## Damaged settings
Each config document is written atomically, with a `.prev` copy. If a document cannot be read:
1. It is renamed to `<name>.json.corrupt-<timestamp>`.
2. The previous good copy is used, or defaults if there is none.
3. A yellow banner explains what happened.

To roll back by hand, close the app and copy `<name>.json.prev` over `<name>.json` in the `config` folder.

## Undo a configuration change the app made
External or agent-profile files changed through the app are backed up under `backups\<hash>\<timestamp>.bak` (the newest 20 are kept). `target.json` in each folder names the original file.

## Extensions
- **Disable** or **Remove** on the Extensions screen.
- If an extension misbehaves at start-up, use Safe Mode, then remove it.
- Three failed starts disable an extension automatically.

## Reset the audit log
**Verify integrity** reports the first broken line. To start a new chain, close the app and move `audit\security-audit.jsonl` aside (keep it for forensics). A new chain starts on the next run. If `audit.key` cannot be decrypted (different user or machine), it is renamed `audit.key.unreadable-<time>` and a new key is created automatically.

## Remove all data
1. Uninstall (Settings → Apps, or `msiexec /x`). This removes the program only.
2. Delete `%LOCALAPPDATA%\DarkCipher\AgentControl`.
3. In **Credential Manager** → Windows Credentials, remove entries named `DarkCipher.AgentControl:*`.
4. Models stay in Ollama. Remove them from Models, or with `ollama rm`.
