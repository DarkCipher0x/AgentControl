# Design system (0.1.6)

DarkCipher AgentControl uses the DarkCipher Systems design system shared with **WhisperDesk** and **ServerManagement**.
Both reference projects were inspected read-only on 2026-10-20 (`Services/ThemeManager.cs` in both, ServerManagement's
`Dcx.Ui/Services/Typography.cs`, `NavLayout.cs` and `Themes/Controls.xaml`, and WhisperDesk's `Themes/Controls.xaml`).
Nothing was copied from either project's code; the values below were read and re-implemented here.

## Where it lives

| Piece | File |
|---|---|
| Palettes, accent rules, contrast arithmetic (pure, unit-tested) | `src/DarkCipher.AgentControl.Application/Appearance/ThemePalettes.cs` |
| Appearance settings, validation, migration, import/export | `src/DarkCipher.AgentControl.Application/Appearance/AppearanceSettingsService.cs` |
| Applies colours to the app, dark title bars, Windows high contrast | `src/DarkCipher.AgentControl.App/Appearance/ThemeService.cs` |
| Fonts, type ramp, text size, zoom, popup re-binding | `src/DarkCipher.AgentControl.App/Appearance/TypographyService.cs` |
| Density, corner radius, navigation width, reduced motion | `src/DarkCipher.AgentControl.App/Appearance/LayoutPreferenceService.cs` |
| Token defaults and every control template | `src/DarkCipher.AgentControl.App/Themes/Theme.xaml` |
| Application icon (runtime) | `src/DarkCipher.AgentControl.App/Appearance/ApplicationIconService.cs` |

Views use tokens only (`{DynamicResource …}`): a test fails if a view contains a raw colour, a literal font family, a
literal font size, or binds a colour/size token statically.

## Typography (identical to ServerManagement)

| Role | Value |
|---|---|
| Interface font (`UiFont`, `ChromeFont`) | `Segoe UI Variable Text, Segoe UI` |
| Code, console, IDs (`MonoFont`, `EditorFont`) | `Cascadia Mono, Cascadia Code, Consolas, Courier New` |
| Icons | `Segoe Fluent Icons, Segoe MDL2 Assets` |
| Licensing | All ship with Windows. Nothing is bundled or redistributed. |

Type ramp (pixels at 100 %, ServerManagement `Typography.Ramp`): FsMicro 10.5, FsCaption 11, FsSmall 11.5, FsMeta 12,
FsBody 12.5, FsControl 13, FsBase 13.5, FsInput 14, FsTitle 15, FsHeading 16, FsH1 17, FsCode 18, FsValue 20, FsIcon 26.
The text-size setting multiplies the ramp and rounds to 0.5 px, as in ServerManagement.

Named text styles: `Display`, `PageTitle`, `SectionTitle`, `CardTitle`, `Body`, `BodySecondary`, `Caption`, `Label`,
`Code`, `Console` (plus the 0.1.x names `H1`, `H2`, `MutedText`, `Mono`, kept as aliases). Buttons, navigation, table
headers and table bodies get their typography from their control styles.

## Colours

The **DarkCipher** preset uses the reference palette exactly. These values are identical in WhisperDesk and ServerManagement:

| Key | Dark | Light ("DarkCipher Light") |
|---|---|---|
| Bg | `#050909` | `#F1F6F6` |
| Surface | `#0B1314` | `#E3EDED` |
| Input | `#020505` | `#FAFDFD` |
| Fg | `#D2F2EF` | `#08191A` |
| Muted | `#5A8986` | `#4E6E6C` |
| Line | `#16302F` | `#BCD2D1` |
| Hover / Track | `#0F2122` | `#D5E5E5` / `#CFDFDF` |
| ScrollThumb | `#234746` | `#A3BEBD` |
| HeaderBg / HeaderFg | `#000000` / `#D2F2EF` | `#061010` / `#E2F4F2` |
| Good / Warn / Bad | `#36D27A` / `#E8C547` / `#FF5F6D` | `#137A3F` / `#8A6A00` / `#B3202E` |
| Brand accent | `#36D2CB` | `#36D2CB`, darkened for light backgrounds |

Derived exactly as in the references: AccentHover (lighten 25 % dark / darken 20 % light), Selection (Bg mixed 18 % /
20 % towards the accent), OnAccent (black or white by luminance), and a neon accent darkened by 42 % on light themes.

**Deliberate additions in AgentControl** (the references do not have these keys): `Surface2` (elevated surfaces),
`FgStrong` (headings), `LineStrong`, `Info` and `Experimental` (taken from ServerManagement's environment-label colours
EnvLab `#5B9BFF` and EnvDev `#B58CFF`), `Critical` (EnvProd `#FF4D5E`), `Unknown`, `AltRow`, `DangerLine`, `HeaderMuted`,
`FgDisabled`, console colours and chart colours. **Deliberate difference:** corners are 2 px everywhere, as in the
references; the 0.1.5 build used 3–4 px.

**Semantic aliases** are published next to the shared keys, so code and extensions can use spec names:
BackgroundPrimary, BackgroundSecondary, SurfacePrimary, SurfaceElevated, SurfaceInteractive, SurfaceInput, BorderSubtle,
BorderStrong, TextPrimary, TextStrong, TextSecondary, TextDisabled, AccentPrimary, AccentSecondary, AccentHoverState,
SelectedState, FocusRing, StatusSuccess, StatusInformation, StatusWarning, StatusError, StatusCritical, StatusOffline,
StatusUnknown and StatusExperimental.

## Presets

| Preset | Notes |
|---|---|
| DarkCipher (default) | The reference palette. |
| DarkCipher High Contrast | Pure black background, white text, strong borders, brighter status colours. Used automatically when Windows high contrast is on (can be turned off). |
| DarkCipher Graphite | Neutral grey surfaces with the DarkCipher accent. |
| DarkCipher Teal | Deeper, teal-tinted surfaces. |
| DarkCipher Light | The references' light palette. It is offered because every surface uses tokens; the self-test renders the shell and eight screens in it. |
| Follow Windows | DarkCipher or DarkCipher Light, following the Windows app mode. |

Contrast is enforced by tests on every preset:
- Body text and card text are at least 7:1.
- Secondary text is at least 4.5:1.
- Status colours are at least 3:1.
- The accent is at least 3:1 against the background (WCAG 2.2 non-text contrast). A custom accent below that is refused.
- Status is always shown as text as well, never by colour alone.

## Customisation (Settings → Appearance, Navigation, Dashboard, Console)

Every option is a bounded choice and is validated when loaded:
- Theme preset, accent swatch or custom accent, status-colour intensity (Subtle, Standard or Strong), follow Windows high contrast.
- Density (Comfortable or Compact), text size (85–150 %), interface zoom (80–150 %), corners (0, 2 or 4–6 px), animation (Follow Windows, Standard or Reduced).
- Sidebar (Expanded, Compact, or icon-only with tooltips and accessible names), sidebar width, landing page, up to 6 pinned pages, hidden pages, developer, experimental and advanced sections. Dashboard, Sessions, Security and Settings can never be hidden.
- Dashboard: Detailed (3 columns) or Compact (4), card visibility, order and wide/normal size (also edited in place with Customise), up to 4 quick actions, hardware details, and a filter for stacks that need attention.
- Console font size (9–24 px) and colours (DarkCipher, Classic or High contrast).
- Reset per category or everything. Export and import as JSON, with a preview before anything changes. Exports contain no secrets, paths or project data.

Stored settings from a newer version are reset with a notice. Unknown or out-of-range values are corrected and listed in
the recovery notice; they never crash the app.

## Icon

See `src/DarkCipher.AgentControl.App/Assets/AppIcon/`. It is generated by `tools/build-icons.ps1` from
`tools/DarkCipher.AgentControl.IconBuilder/Program.cs` (the editable master is the geometry there plus `app-master.svg`).

The concept is a dark hexagon (the controlled boundary) holding an open cipher ring in the shape of a "C". Three
orchestration nodes sit on the ring, wired to a light agent core, and a spoke leaves through the ring's opening to a
square output node (controlled action).

Detail drops with size:
- 16–24 px: hexagon, ring and core.
- 32–48 px: adds solid nodes.
- 64 px and up: adds the wiring and the output node.

There is no text, no provider logo, and nothing reused from WhisperDesk or ServerManagement. The colours are the brand teal `#36D2CB`, Surface `#0B1314` and Fg `#D2F2EF`.

`app.ico` has 9 frames (16, 20, 24, 32, 40, 48, 64, 128 and 256). The frames are 32-bit BMP with an AND mask, plus a PNG frame at 256. There are also PNGs from 16 to 512 px.

The icon is wired into:
- the exe (`ApplicationIcon`);
- every window: the title bar, the taskbar and Alt+Tab (`ApplicationIconService`, set by a class handler on every window);
- the header brand mark (vector XAML using the same geometry);
- Settings → About (256 px);
- the MSI: the Add/Remove Programs entry (`ARPPRODUCTICON`) and both Start-menu shortcuts.
