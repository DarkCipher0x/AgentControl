# DarkCipher AgentControl 0.1.6: final technical report (2026-10-20)

This report answers the 36 points of the 0.1.6 specification (`docs/specs/2026-10-19-icon-theme-catalogue-templates.md`).

**Feature labels:** Implemented and Verified · Implemented but Not Fully Verified · Experimental · Planned · Unsupported.
**Security-control labels:** Enforced · Partially Enforced · Advisory Only · Not Implemented.
**Integration labels:** Supported and Verified · Compatible but Not Yet Verified · Ready with Limitations · Experimental · Unsupported.

"Verified" below means proven by an automated test, by the headless self-test, or by a live run that is listed.

## 1. Project path
`%USERPROFILE%\Projects\DarkCipherAgentControl` (the existing project; the product name is "DarkCipher AgentControl").
Before changes, a source snapshot was written to `.backups\pre-0.1.6-source.zip` inside the project. Git is not
initialised in this folder.

## 2. Reference projects inspected (read-only)
- WhisperDesk (`Projects\WhisperDesk`): `Services\ThemeManager.cs`, `Themes\Controls.xaml`.
- ServerManagement (`Projects\DarkCipher-ServerManagement`): `src\Dcx.Ui\Services\ThemeManager.cs`, `Typography.cs`, `NavLayout.cs`, and `src\DcxManager\Themes\Controls.xaml`.

Both were only read. Nothing in either was built, restored, formatted or written.

## 3. Files changed (summary)
- **New, Domain:**
  - `Tools/AgentToolTypes.cs`, `Templates/TemplateTypes.cs`, `Verification/VerificationTypes.cs`, `Configuration/ConfigurationTypes.cs`.
  - `Compatibility/CompatibilityPorts.cs`, `Common/ErrorReport.cs`, `Common/Hashing.cs`, `Processes/ProcessPreview.cs`.
  - Extended: `Downloads/DownloadTypes.cs`, `Stacks/StackProfile.cs`, `Agents/IAgentProvider.cs`, `Security/PolicyTypes.cs`.
- **New, Application:**
  - `Appearance/*`, `Tools/*` (catalogue, installation, queue provider, verifier, updates, repair, custom registry), `Models/*` (verification, install planner).
  - `Templates/*` (built-ins, validation, migration, catalogue, compatibility, configuration builder, engine), `Configuration/*` (TOML/JSON documents, transactions, ownership ledger).
  - `Verification/*` (matrix, repair, end-to-end verification).
  - Rewritten: `Downloads/DownloadManager.cs`, `Stacks/StackValidator.cs`. Extended: `Sessions/SessionService.cs` (configuration transactions), `Stacks/StackService.cs`.
- **New, Integrations:** `Tools/WingetClient.cs`, `WingetToolProvider.cs`, `BuiltInToolProviders.cs`, `ExtensionWingetToolProvider.cs`. End-to-end probes added to the Claude Code and Codex providers; template options added to both.
- **Security:** `ConfigurationProtectionService` (`RemoveCreated`, `Read`); command-line rendering moved to Domain.
- **Extensions and SDK:** SDK 1.1 contracts and permission, broker methods, contribution registry, host services.
- **App:**
  - `Appearance/*` (icon, theme, typography, layout, coordinator), `Assets/AppIcon/*`, `Themes/Theme.xaml` (rewritten), `Templates.xaml`.
  - Shell: MainWindow, Navigator, Converters, CardPanel, Dialogs, SelfTest.
  - New pages: Catalogue and Templates.
  - Rewritten views: Dashboard, Downloads, Settings. Extended: Stacks, Models, Services. Bootstrapper and App start-up.
  - All views converted to token references.
- **Installer and build:** `installer\DarkCipherAgentControl.wxs` (icon), `build.ps1` (icon define).
- **Tools:** `tools\DarkCipher.AgentControl.IconBuilder`, `tools\build-icons.ps1`.
- **Tests:** 124 new tests across icon, theme, appearance, tools, queue, models, templates, configuration, readiness, verification and SDK, plus 3 opt-in live test groups.
- **Docs:** DESIGN-SYSTEM, SUPPORTED-CONFIGURATIONS, SECURITY (threat review), USER-GUIDE, DEVELOPER-GUIDE, EXTENSIONS, SDK README, CHANGELOG, release notes, this report.

## 4. Icon concept
A dark hexagon (the controlled boundary) holding an open cipher ring forming a "C". Three orchestration nodes on the ring
are wired to a light agent core, and a spoke leaves through the ring's opening to a square output node: secure,
orchestrated agent action.

It is original: no text, no provider logo, nothing from WhisperDesk or ServerManagement. The colours are the DarkCipher
brand teal `#36D2CB`, Surface `#0B1314` and Fg `#D2F2EF`.

Three detail levels keep it readable at every size:
- 16–24 px: hexagon, ring and core.
- 32–48 px: adds solid nodes.
- 64 px and up: adds the wiring and the output node.

It was inspected on dark, light and taskbar-grey backgrounds at native and magnified sizes.

## 5. Icon assets
`src\DarkCipher.AgentControl.App\Assets\AppIcon\`:
- `app-master.svg` (editable master, generated from the builder's geometry).
- `app-16/20/24/32/40/48/64/128/256/512.png` (transparent RGBA).
- `app.ico` with 9 frames (16, 20, 24, 32, 40, 48, 64, 128, 256): 32-bit BMP with an AND mask, plus a PNG frame at 256.

Regenerate with `tools\build-icons.ps1`. **Implemented and Verified** (tests: files, sizes, IHDR, ICO frames, transparent corners, opaque centre).

## 6. Icon integration
- exe `ApplicationIcon`, verified by ExtractIconEx on the built exe.
- Every window's title bar, taskbar and Alt+Tab, through a class handler (self-test: the icon decodes from resources).
- Header brand mark (vector, same geometry).
- Settings → About (256 px).
- MSI `ARPPRODUCTICON` (Add/Remove Programs) and both Start-menu shortcuts.

**Implemented and Verified**, except that display in an *installed* Start menu and Add/Remove Programs needs a real
`msiexec /i`, which was not run (**Implemented but Not Fully Verified**).

## 7. Typography found in WhisperDesk
Fonts are fixed in XAML: `Cascadia Mono, Cascadia Code, Consolas, Courier New` for monospace, with the system UI font for
the rest. Radius 2 px throughout. Mono is used for labels and accents, SemiBold for emphasis.

## 8. Typography found in ServerManagement
Centralised in `Typography.cs`:
- `UiFont` and `ChromeFont`: `Segoe UI Variable Text, Segoe UI`.
- `MonoFont` and `EditorFont`: `Cascadia Mono, Cascadia Code, Consolas, Courier New`.
- A 14-step ramp from FsMicro 10.5 to FsIcon 26; FsBase is 13.5.
- A text-size multiplier (0.85–1.5, rounded to 0.5 px) and a UI zoom transform.
- Popups re-bound on open. Density paddings are set in `NavLayout`.

## 9. Final typography
ServerManagement's system, unchanged. Same families, same ramp, same rounding, same popup re-binding. Named styles:
Display, PageTitle, SectionTitle, CardTitle, Body, BodySecondary, Caption, Label, Code and Console, plus control
typography. All the fonts ship with Windows, so there are no licence issues. **Implemented and Verified** (tests compare
the ramp and fonts; the self-test checks the live resources).

## 10. Theme colours found
Both references define the same palette in `ThemeManager.cs`:

| Key | Dark | Light |
|---|---|---|
| Bg | 050909 | F1F6F6 |
| Surface | 0B1314 | E3EDED |
| Input | 020505 | FAFDFD |
| Fg | D2F2EF | 08191A |
| Muted | 5A8986 | 4E6E6C |
| Line | 16302F | BCD2D1 |
| Hover | 0F2122 | D5E5E5 |
| Track | 0F2122 | CFDFDF |
| ScrollThumb | 234746 | A3BEBD |
| HeaderBg | 000000 | 061010 |
| HeaderFg | D2F2EF | E2F4F2 |

- Brand accent: 36D2CB. Accent-derived colours are computed.
- ServerManagement adds Good 36D27A, Warn E8C547, Bad FF5F6D and environment colours (Prod FF4D5E, Test F2B33D, Lab 5B9BFF, Dev B58CFF).
- Differences between the two apps: WhisperDesk has no status or environment keys.

The table in DESIGN-SYSTEM.md has the complete detail.

## 11. Theme-token architecture
- Pure palette and arithmetic in `ThemePalettes` (Application layer).
- `ThemeService` writes frozen brushes and colours for every key, the shared names plus semantic aliases (BackgroundPrimary … FocusRing).
- `TypographyService` and `LayoutPreferenceService` write the font, size, padding and radius tokens.
- `Theme.xaml` holds the defaults and every control template.
- Views reference tokens with `DynamicResource` only, enforced by tests.
- Additions that the references do not have are documented in DESIGN-SYSTEM.md.

**Implemented and Verified.**

## 12. UI areas updated
- Every view and the shell are converted to tokens.
- Every control template is re-themed: buttons, inputs, password box, check box, radio button (new template), switch (new), combo box, data grid (rows, headers, cells), list box, tab, progress (animated and still), tooltip, context menu, scroll bar, focus states and disabled states.
- New pages: Catalogue and Templates.
- Rewritten pages: Dashboard (cards), Downloads (unified manager) and Settings (7 tabs).
- Extended pages: Stacks (diagnose, verify, rollback) and Models (pipeline plans).
- Services has extension health checks. Errors use structured dialogs.

The self-test renders the shell and 8 screens in Light and in High Contrast with compact density and icon-only navigation; the renders were inspected visually.

## 13. Customisation options
Every option below is bounded, validated, live-applied and persisted:
- Theme preset, accent (swatches or custom, with a contrast check), status intensity, follow Windows high contrast.
- Density, text size, zoom, corners, animation (reduced motion).
- Sidebar style and width, landing page, pinned and hidden pages, developer, experimental and advanced sections.
- Dashboard layout, cards and quick actions.
- Console size and colours.
- Reset by category or everything; export and import with a preview.

**Implemented and Verified** (unit tests plus a live self-test switch).

## 14. Dashboard customisation
- Show, hide, reorder and resize cards in place (Customise).
- Detailed or Compact layout, quick actions, hardware details, and a filter for stacks that need attention.
- Two new cards: downloads/installs and verified configurations.
- Layout versioning and repair: unknown or duplicate cards are dropped, new cards are added hidden, a newer layout is reset, and at least one card stays visible.

**Implemented and Verified** (tests); the in-place edit bar was **Implemented but Not Fully Verified** by an interactive click-through.

## 15. Navigation customisation
- Expanded, compact or icon-only, with tooltips and accessible names.
- Pinned group, hidden pages, recent destinations in the status bar, landing page, developer and experimental sections.
- Dashboard, Sessions, Security and Settings can never be hidden; header security badges are always shown.

**Implemented and Verified** (tests plus self-test).

## 16. Agent-tool catalogue
Tabs: agent tools, runtimes, gateways, models (link), templates (link), extensions (link), installed, updates,
downloads and custom tools.

Each tool shows publisher, description, pinned source, install method, licence, installed, available and verified
versions, a version verdict, protocols, authentication, limitations, security class and trust.

**Implemented and Verified** (tests; live detection and source lookups).

## 17. Supported agent providers
| Provider | Status |
|---|---|
| Claude Code (`Anthropic.ClaudeCode`) | Built-in provider. The app verified headless launch with 2.1.296 in 0.1.2; winget now offers 2.1.302. |
| Codex CLI (`OpenAI.Codex`) | Built-in provider. Never verified live (not installed on the build PC). |
| Ollama runtime (`Ollama.Ollama`) | Built-in provider. Verified live: 0.40.6. |
| Ollama Anthropic-compatible gateway | Built into Ollama; not installable on its own. The live probe passed with llama3.2. |
| Custom tools | Registration and validation work. Sessions with custom tools are **Planned**. |
| Extension catalogue entries (SDK 1.1) | Always Unverified; installed only through the host's winget path. |

## 18. Agent installation
Plan → approval → queue → re-plan fingerprint check → winget (exact id, `winget` source, per-user, silent, no shell) →
Testing stage (detection and version range) → Ready, Ready with limitations, or Rollback required. Update, diagnose-first
repair and uninstall work the same way. Rollback is offered as the uninstall plan.

**Implemented and Verified by automated tests** (scripted executor) **and by live plans and lookups**. **Not live-verified:**
an actual install was not run (it would change the PC; your approval is needed) → **Implemented but Not Fully Verified**.

## 19. Model catalogue
The existing curated Ollama catalogue, recommendations and Hugging Face search (Models page), plus the new 10-step
installation plan: source, metadata, licence, tag or file, runtime, agent and template requirements, hardware, disk,
plan, approval. **Implemented and Verified** (tests).

## 20. Ollama pulls
Pull through the unified queue: Ollama verifies layer digests; the Testing stage confirms the runtime lists the model
and then (by default) runs the compatibility suite. Pause and resume keep finished layers. Cancel, retry and remove
work. **Implemented and Verified**: live, `llama3.2:latest` went Resolving → digest check → Testing → **Ready**.

## 21. Hugging Face
Search and metadata (existing), the import plan through Ollama's `hf.co/<repo>:<quant>` pull, and the 10-step plan.
Gated, private, split and non-GGUF repositories are refused. **Implemented but Not Fully Verified**: no live import was
run in 0.1.6; the pipeline is the same one verified for Ollama pulls.

## 22. Download manager
One queue for every item type, with every column and action the specification lists; a Testing stage decides Ready;
persistence and Interrupted state. A race was fixed (a late progress report overwrote the final state). **Implemented
and Verified.**

## 23. Template engine
Catalogue (built-in, extension, user and imported), validation, migration, compatibility filtering, preview, dry run,
create, launch, duplicate, edit (through duplicate/export/import), reset, export, import and deletion.
**Implemented and Verified** (tests); a live launch from a template was not run (no agent installed) →
**Implemented but Not Fully Verified** for launch.

## 24. Built-in templates (13, v1.0.0)
General Coding (local), Agentic Coding (local), Code Review (read-only), Bug Investigation, Large Repository Analysis,
Refactoring, Documentation, Test Generation, Planning and Architecture, Fast Local Assistance, Private Offline Coding,
Hybrid: Claude native with local fallback suggested, and Claude Code + local gateway (Experimental).

## 25. Configuration transactions
Discover, parse, validate, back up, merge (only the generated keys), diff, validate syntax, write atomically, re-read,
confirm values, health check and integration test, then commit or roll back. Ownership ledger, conflict decisions,
"Roll back last change". Session launches use it for agent profile files. **Implemented and Verified** (tests plus a
self-test rollback).

## 26. Stack readiness
States: Ready, Ready with limitations, Setup incomplete, Agent missing, Runtime missing, Model missing, Authentication
required, Service unavailable, Configuration invalid, Permission conflict, Compatibility failed, Security policy
blocked. Each check carries evidence and a fix. Diagnose, Repair (do this), View details, Run full verification and Roll
back last change. **Implemented and Verified** (tests; screens rendered).

## 27. End-to-end verification results
- The verification service works end to end in tests: disposable project, Read-Only profile, random token, strict classifier, matrix entry.
- Live on the build PC: no agent is installed, so **no stack was verified end to end in 0.1.6**.
- Live components: the model pipeline (Ready) and the gateway probe (Agentic Compatible).

## 28. Supported configuration matrix
See `docs/SUPPORTED-CONFIGURATIONS.md`. 0.1.6 has **no Supported and Verified entry yet**. The 0.1.2 historical record
shows Claude Code 2.1.296 through the Ollama gateway with gpt-oss-64k as **Experimental**.

## 29. Security controls added or changed
| Control | Status |
|---|---|
| winget-only installs with pinned exact ids from the reviewed source | Enforced |
| Plans and approvals for every install, update, repair and uninstall | Enforced |
| Plan fingerprint re-check before execution | Enforced |
| No elevation (`--scope user`) | Enforced |
| PATH and environment changes | Advisory Only (disclosed in the plan; the app itself changes none) |
| Testing stage before Ready | Enforced |
| Templates are data (unknown fields refused, allow-listed options, secret scan, imported permissions reset) | Enforced |
| Configuration transactions (merge, conflicts, backup, atomic, re-read, rollback) | Enforced |
| Export secret exclusion | Enforced |
| SDK 1.1 permission checks and audit | Enforced (extensions are still in-process: Partially Enforced isolation) |
| Custom-agent validation and trust | Enforced |
| Strict "Supported and Verified" classification | Enforced |
| Useful errors with reference ids | Enforced |

The full threat review is in SECURITY.md §8.

## 30. Tests performed
- **Automated:** 398, up from 268, all passing (10 are opt-in live tests that skip unless enabled); the offline suite was run three times in a row.
- **Headless self-test:** 47 checks, all passing.
- **Live, opt-in:**
  - winget: version, three source lookups and three install plans (nothing installed).
  - Ollama with llama3.2: the compatibility suite, the gateway probe, and the full model pipeline through the queue.

## 31. Build result
See `dist\0.1.6\BUILD-INFO.txt`. The release pipeline runs a locked restore, a Release build, the tests, a vulnerability
scan, the SBOM, the privacy gate, the self-test on the Release build and on the MSI payload, ICE validation and
SHA256SUMS.

## 32. Installer / package result
MSI 0.1.6, per-machine to `C:\Program Files\DarkCipher Systems\AgentControl`, with the new icon for Add/Remove Programs
and the shortcuts. A real install and uninstall is still **not run** on this PC (needs your approval).

## 33. Known limitations
- No agent is installed on the build PC, so there is no end-to-end verification for 0.1.6 and no Supported and Verified entry.
- Tool installs were not executed live.
- Sessions with custom or extension tools are Planned.
- Codex's end-to-end probe (`codex exec`) has never run against a real Codex.
- Extension model sources are browse-only (host-side acquisition for non-Hugging-Face sources is Planned).
- The appearance settings for template *editing* are through duplicate/export/import; there is no in-app template field editor yet (Planned).
- Status brushes in already-loaded rows update on the next refresh, not instantly, after a theme change.
- The updater stays off (no release signing key).

## 34. Experimental features
- Claude Code through the local gateway (template and stack).
- Extension contributions are in-process.

## 35. Unsupported combinations
- Remote gateways for Claude Code.
- Gated, private, split or non-GGUF Hugging Face repositories.
- Installs from anything but pinned winget packages.
- Automatic local/cloud fallback.
- Sessions with custom or extension tools.

## 36. Recommended next steps
1. With your approval: install Codex (`OpenAI.Codex`) and Claude Code (`Anthropic.ClaudeCode`) from the Catalogue on the gaming PC, then run **Stacks → Run full verification** for "Code Review (read-only)" and "Claude Code + local gateway" to get the first 0.1.6 matrix entries.
2. Run the 0.1.6 MSI for real (install, start, check the icon in the Start menu and Add/Remove Programs, uninstall).
3. Add an in-app template field editor; allow sessions for validated custom tools.
4. Out-of-process extension host; then let extension model sources supply verified GGUF downloads.
