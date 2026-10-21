using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using DarkCipher.AgentControl.App.Appearance;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Appearance;
using DensityKind = DarkCipher.AgentControl.Application.Appearance.Density;
using DashboardModeKind = DarkCipher.AgentControl.Application.Appearance.DashboardMode;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed class Option<T>(T value, string label)
{
    public T Value { get; } = value;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

public sealed class ToggleItem(string key, string label, bool isChecked, bool enabled, Action<string, bool> changed) : ObservableObject
{
    private bool _checked = isChecked;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public bool Enabled { get; } = enabled;
    public bool IsChecked { get => _checked; set { if (Set(ref _checked, value)) changed(Key, value); } }
}

public sealed record Swatch(string Name, string Hex, Brush Brush, ICommand Apply);

/// <summary>Settings → Appearance, Navigation, Dashboard, Console, Import/Export and About. Every option is a bounded,
/// validated choice; changes apply live and are saved immediately.</summary>
public sealed class AppearanceSettingsViewModel : ObservableObject
{
    private readonly AppearanceSettingsService _svc;
    private readonly ThemeService _theme;
    private string _customAccent = "";
    private string _message = "";
    private bool _syncing;

    public AppearanceSettingsViewModel(AppearanceSettingsService svc, ThemeService theme)
    {
        _svc = svc;
        _theme = theme;
        Themes = [.. Enum.GetValues<ThemePresetId>().Select(t => new Option<ThemePresetId>(t, ThemePalettes.DisplayName(t)))];
        Swatches = [.. ThemePalettes.AccentSwatches.Select(s => new Swatch(s.Name, s.Hex, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + s.Hex)), new RelayCommand(() => SetAccent(s.Hex))))];
        ApplyCustomAccentCommand = new RelayCommand(() => SetAccent(CustomAccent));
        ResetCommand = new RelayCommand(p =>
        {
            AppearanceCategory? category = p is string s && Enum.TryParse<AppearanceCategory>(s, out var c) ? c : null;
            if (category is null && !Dialogs.Confirm("Reset everything", "Reset theme, layout, navigation, dashboard and console settings to their defaults?", null, "Reset")) return;
            Message = _svc.Reset(category).Message;
        });
        ExportCommand = new RelayCommand(Export);
        ImportCommand = new RelayCommand(Import);
        svc.Changed += (_, _) => Ui.OnUi(Sync);
        Sync();
    }

    public string Message { get => _message; private set => Set(ref _message, value); }
    private AppearanceSettings S => _svc.Current;
    private void Update(Func<AppearanceSettings, AppearanceSettings> f) { if (_syncing) return; var r = _svc.Update(f); if (r.Message != "No change.") Message = r.Message; }

    // ---- Theme ----
    public IReadOnlyList<Option<ThemePresetId>> Themes { get; }
    public Option<ThemePresetId>? Theme { get => Themes.FirstOrDefault(t => t.Value == S.Theme); set { if (value is not null) Update(s => s with { Theme = value.Value }); } }
    public IReadOnlyList<Swatch> Swatches { get; }
    public string CurrentAccent => "#" + S.AccentHex;
    public string CustomAccent { get => _customAccent; set => Set(ref _customAccent, value); }
    public ICommand ApplyCustomAccentCommand { get; }
    public bool FollowWindowsHighContrast { get => S.FollowWindowsHighContrast; set => Update(s => s with { FollowWindowsHighContrast = value }); }
    public string ThemeNote => _theme.HighContrastForced ? "Windows high contrast is on, so the DarkCipher High Contrast preset is in use." : $"In use: {_theme.Active.DisplayName}.";
    public IReadOnlyList<Option<StatusIntensity>> Intensities { get; } = [new(StatusIntensity.Subtle, "Subtle"), new(StatusIntensity.Standard, "Standard"), new(StatusIntensity.Strong, "Strong")];
    public Option<StatusIntensity>? Intensity { get => Intensities.FirstOrDefault(o => o.Value == S.StatusIntensity); set { if (value is not null) Update(s => s with { StatusIntensity = value.Value }); } }

    private void SetAccent(string hex)
    {
        if (!Rgb.TryParse(hex, out var rgb)) { Message = "Enter a colour as RRGGBB, e.g. 36D2CB."; return; }
        var (ok, ratio) = ThemePalettes.CheckAccent(ThemePalettes.Resolve(S.Theme, ThemeService.SystemPrefersDark()), rgb.Hex);
        if (!ok) { Message = $"#{rgb.Hex} is too faint on this theme ({ratio:0.0}:1; at least 3:1 is needed so focus and selection stay visible)."; return; }
        Update(s => s with { AccentHex = rgb.Hex });
    }

    // ---- Layout ----
    public IReadOnlyList<Option<DensityKind>> Densities { get; } = [new(DensityKind.Comfortable, "Comfortable"), new(DensityKind.Compact, "Compact")];
    public Option<DensityKind>? Density { get => Densities.FirstOrDefault(o => o.Value == S.Density); set { if (value is not null) Update(s => s with { Density = value.Value }); } }
    public IReadOnlyList<Option<CornerStyle>> CornerStyles { get; } = [new(CornerStyle.Sharp, "Sharp (0 px)"), new(CornerStyle.Standard, "Standard (2 px, as WhisperDesk/ServerManagement)"), new(CornerStyle.Soft, "Soft (4–6 px)")];
    public Option<CornerStyle>? Corners { get => CornerStyles.FirstOrDefault(o => o.Value == S.Corners); set { if (value is not null) Update(s => s with { Corners = value.Value }); } }
    public IReadOnlyList<Option<MotionLevel>> MotionLevels { get; } = [new(MotionLevel.FollowWindows, "Follow Windows (Animation effects)"), new(MotionLevel.Standard, "Standard"), new(MotionLevel.Reduced, "Reduced motion")];
    public Option<MotionLevel>? Motion { get => MotionLevels.FirstOrDefault(o => o.Value == S.Motion); set { if (value is not null) Update(s => s with { Motion = value.Value }); } }
    public IReadOnlyList<Option<double>> TextScales { get; } = [new(0.85, "85 %"), new(0.9, "90 %"), new(1.0, "100 %"), new(1.1, "110 %"), new(1.25, "125 %"), new(1.5, "150 %")];
    public Option<double>? TextScale { get => TextScales.FirstOrDefault(o => Math.Abs(o.Value - S.TextScale) < 0.001); set { if (value is not null) Update(s => s with { TextScale = value.Value }); } }
    public IReadOnlyList<Option<double>> UiScales { get; } = [new(0.8, "80 %"), new(0.9, "90 %"), new(1.0, "100 %"), new(1.1, "110 %"), new(1.25, "125 %"), new(1.5, "150 %")];
    public Option<double>? UiScale { get => UiScales.FirstOrDefault(o => Math.Abs(o.Value - S.UiScale) < 0.001); set { if (value is not null) Update(s => s with { UiScale = value.Value }); } }

    // ---- Navigation ----
    public IReadOnlyList<Option<NavigationStyle>> NavStyles { get; } = [new(NavigationStyle.Expanded, "Expanded"), new(NavigationStyle.Compact, "Compact"), new(NavigationStyle.IconOnly, "Icons only (names in tooltips)")];
    public Option<NavigationStyle>? NavStyle { get => NavStyles.FirstOrDefault(o => o.Value == S.Navigation); set { if (value is not null) Update(s => s with { Navigation = value.Value }); } }
    public IReadOnlyList<Option<int>> NavWidths { get; } = [new(180, "Narrow (180)"), new(218, "Standard (218)"), new(260, "Wide (260)"), new(320, "Extra wide (320)")];
    public Option<int>? NavWidth { get => NavWidths.FirstOrDefault(o => o.Value == S.NavigationWidth) ?? NavWidths[1]; set { if (value is not null) Update(s => s with { NavigationWidth = value.Value }); } }
    public IReadOnlyList<Option<string>> Pages { get; } = [.. DashboardCatalog.Pages.Select(p => new Option<string>(p.Key, p.Title))];
    public Option<string>? LandingPage { get => Pages.FirstOrDefault(p => p.Value == S.LandingPage); set { if (value is not null) Update(s => s with { LandingPage = value.Value }); } }
    public ObservableCollection<ToggleItem> PinnedPages { get; } = [];
    public ObservableCollection<ToggleItem> VisiblePages { get; } = [];
    public bool ShowDeveloperSections { get => S.ShowDeveloperSections; set => Update(s => s with { ShowDeveloperSections = value }); }
    public bool ShowExperimentalSections { get => S.ShowExperimentalSections; set => Update(s => s with { ShowExperimentalSections = value }); }
    public bool ShowAdvancedInformation { get => S.ShowAdvancedInformation; set => Update(s => s with { ShowAdvancedInformation = value }); }

    // ---- Dashboard ----
    public IReadOnlyList<Option<DashboardModeKind>> DashboardModes { get; } = [new(DashboardModeKind.Detailed, "Detailed (3 columns)"), new(DashboardModeKind.Compact, "Compact (4 columns, fewer details)")];
    public Option<DashboardModeKind>? DashboardMode { get => DashboardModes.FirstOrDefault(o => o.Value == S.Dashboard.Mode); set { if (value is not null) Update(s => s with { Dashboard = s.Dashboard with { Mode = value.Value } }); } }
    public bool ShowHardwareDetails { get => S.Dashboard.ShowHardwareDetails; set => Update(s => s with { Dashboard = s.Dashboard with { ShowHardwareDetails = value } }); }
    public ObservableCollection<ToggleItem> Cards { get; } = [];
    public ObservableCollection<ToggleItem> QuickActions { get; } = [];

    // ---- Console ----
    public IReadOnlyList<Option<int>> ConsoleSizes { get; } = [.. new[] { 9, 10, 11, 12, 13, 14, 16, 18, 20, 24 }.Select(i => new Option<int>(i, i + " px"))];
    public Option<int>? ConsoleSize { get => ConsoleSizes.FirstOrDefault(o => o.Value == S.ConsoleFontSize); set { if (value is not null) Update(s => s with { ConsoleFontSize = value.Value }); } }
    public IReadOnlyList<Option<ConsolePreset>> ConsolePresets { get; } = [new(ConsolePreset.DarkCipher, "DarkCipher"), new(ConsolePreset.Classic, "Classic terminal"), new(ConsolePreset.HighContrast, "High contrast")];
    public Option<ConsolePreset>? Console { get => ConsolePresets.FirstOrDefault(o => o.Value == S.Console); set { if (value is not null) Update(s => s with { Console = value.Value }); } }
    public string ConsoleSample => "PS> ollama list\nNAME                ID              SIZE      MODIFIED\ngpt-oss:20b         17052f91a42e    13 GB     2 days ago";

    // ---- About ----
    public ImageSource AppIcon => ApplicationIconService.LargeImage;
    public string Version => "DarkCipher AgentControl " + Composition.Bootstrapper.AppVersion.ToString(3);
    public string IconFrames
    {
        get
        {
            try { return "Icon frames: " + string.Join(", ", ApplicationIconService.FrameSizes().Select(s => $"{s}×{s}")); }
            catch (Exception ex) when (ex is IOException or NotSupportedException) { return "Icon frames could not be read: " + ex.Message; }
        }
    }
    public string Typography => $"Interface: {TypographyService.DefaultUi}. Code and console: {TypographyService.DefaultMono}. Both ship with Windows; no font is bundled.";

    public ICommand ResetCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }

    private void Sync()
    {
        _syncing = true;
        try
        {
            foreach (var p in new[] { nameof(Theme), nameof(CurrentAccent), nameof(FollowWindowsHighContrast), nameof(ThemeNote), nameof(Intensity), nameof(Density), nameof(Corners),
                         nameof(Motion), nameof(TextScale), nameof(UiScale), nameof(NavStyle), nameof(NavWidth), nameof(LandingPage), nameof(ShowDeveloperSections),
                         nameof(ShowExperimentalSections), nameof(ShowAdvancedInformation), nameof(DashboardMode), nameof(ShowHardwareDetails), nameof(ConsoleSize), nameof(Console) })
                Raise(p);
            Rebuild(PinnedPages, DashboardCatalog.Pages.Select(p => (p.Key, p.Title, S.PinnedPages.Contains(p.Key), !S.HiddenPages.Contains(p.Key))),
                (k, on) => Update(s => s with { PinnedPages = on ? [.. s.PinnedPages, k] : s.PinnedPages.Where(x => x != k).ToList() }));
            Rebuild(VisiblePages, DashboardCatalog.Pages.Select(p => (p.Key, p.Title + (DashboardCatalog.AlwaysVisible.Contains(p.Key) ? " (always shown)" : ""),
                    !S.HiddenPages.Contains(p.Key), !DashboardCatalog.AlwaysVisible.Contains(p.Key))),
                (k, on) => Update(s => s with { HiddenPages = on ? s.HiddenPages.Where(x => x != k).ToList() : [.. s.HiddenPages, k] }));
            Rebuild(Cards, S.Dashboard.Cards.Select(c => (c.Id, DashboardCatalog.Cards.First(x => x.Id == c.Id).Title, c.Visible, true)),
                (k, on) => Update(s => s with { Dashboard = s.Dashboard with { Cards = s.Dashboard.Cards.Select(c => c.Id == k ? c with { Visible = on } : c).ToList() } }));
            Rebuild(QuickActions, DashboardCatalog.QuickActions.Select(a => (a.Id, a.Title, S.Dashboard.QuickActions.Contains(a.Id), true)),
                (k, on) => Update(s => s with { Dashboard = s.Dashboard with { QuickActions = on ? [.. s.Dashboard.QuickActions, k] : s.Dashboard.QuickActions.Where(x => x != k).ToList() } }));
        }
        finally { _syncing = false; }
    }

    private static void Rebuild(ObservableCollection<ToggleItem> list, IEnumerable<(string Key, string Label, bool On, bool Enabled)> items, Action<string, bool> changed)
    {
        list.Clear();
        foreach (var (k, l, on, en) in items) list.Add(new ToggleItem(k, l, on, en, changed));
    }

    private void Export()
    {
        var json = _svc.Export();
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "agentcontrol-appearance.json", Filter = "Appearance settings|*.json" };
        if (Dialogs.Headless || dlg.ShowDialog() != true) { Ui.Copy(json); Message = "Copied to the clipboard. Appearance exports contain no credentials, tokens, paths or project data."; return; }
        File.WriteAllText(dlg.FileName, json);
        Message = "Exported. Appearance exports contain no credentials, tokens, paths or project data.";
    }

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Appearance settings|*.json" };
        if (Dialogs.Headless || dlg.ShowDialog() != true) return;
        if (new FileInfo(dlg.FileName).Length > 64 * 1024) { Message = "That file is too large to be an appearance export."; return; }
        var preview = _svc.PreviewImport(File.ReadAllText(dlg.FileName));
        if (!preview.Valid) { Dialogs.Error("Import refused", preview.Message); return; }
        var details = preview.Changes.Concat(preview.Notes.Select(n => "Corrected: " + n)).ToList();
        if (details.Count == 0) { Message = "The file matches your current settings."; return; }
        if (!Dialogs.Confirm("Import appearance settings", "Apply these changes?", details, "Apply")) return;
        Message = _svc.ApplyImport(preview).Message;
    }
}
