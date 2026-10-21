using System.Text.Json;
using System.Text.Json.Serialization;
using DarkCipher.AgentControl.Domain.Common;

namespace DarkCipher.AgentControl.Application.Appearance;

public enum AppearanceCategory { Theme, Layout, Navigation, Dashboard, Console }

public sealed record AppearanceImportPreview(bool Valid, AppearanceSettings? Proposed, IReadOnlyList<string> Changes, IReadOnlyList<string> Notes, string Message);

/// <summary>
/// Loads, validates, migrates, saves, resets, exports and imports <see cref="AppearanceSettings"/>. Every value is bounded:
/// out-of-range numbers are clamped, unknown enum values, pages and cards are dropped, missing dashboard cards are added
/// back, and a document from a newer schema is reset to defaults with a notice. Nothing here touches WPF.
/// </summary>
public sealed class AppearanceSettingsService
{
    public const string StoreName = "appearance";
    private const int MaxImportBytes = 64 * 1024;
    private readonly IDocumentStore _store;
    private AppearanceSettings _current;

    public static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 16,
    };

    public AppearanceSettingsService(IDocumentStore store)
    {
        _store = store;
        var (normalized, notes) = Normalize(store.Load(StoreName, () => new AppearanceSettings()));
        _current = normalized;
        LoadNotes = notes;
        if (notes.Count > 0) _store.Save(StoreName, _current);
    }

    public AppearanceSettings Current => _current;

    /// <summary>What was corrected while loading (shown once in Settings).</summary>
    public IReadOnlyList<string> LoadNotes { get; }

    public event EventHandler<AppearanceSettings>? Changed;

    public OperationResult Update(Func<AppearanceSettings, AppearanceSettings> change)
    {
        var (next, notes) = Normalize(change(_current));
        if (next == _current) return OperationResult.Ok("No change.");
        _current = next;
        _store.Save(StoreName, next);
        Changed?.Invoke(this, next);
        return OperationResult.Ok(notes.Count == 0 ? "Saved." : "Saved. " + string.Join(" ", notes));
    }

    public OperationResult Reset(AppearanceCategory? category)
    {
        var d = new AppearanceSettings();
        return Update(s => category switch
        {
            AppearanceCategory.Theme => s with { Theme = d.Theme, AccentHex = d.AccentHex, FollowWindowsHighContrast = d.FollowWindowsHighContrast, StatusIntensity = d.StatusIntensity },
            AppearanceCategory.Layout => s with { Density = d.Density, Corners = d.Corners, Motion = d.Motion, TextScale = d.TextScale, UiScale = d.UiScale },
            AppearanceCategory.Navigation => s with
            {
                Navigation = d.Navigation, NavigationWidth = d.NavigationWidth, LandingPage = d.LandingPage, PinnedPages = d.PinnedPages,
                HiddenPages = d.HiddenPages, ShowDeveloperSections = d.ShowDeveloperSections, ShowExperimentalSections = d.ShowExperimentalSections,
                ShowAdvancedInformation = d.ShowAdvancedInformation,
            },
            AppearanceCategory.Dashboard => s with { Dashboard = d.Dashboard },
            AppearanceCategory.Console => s with { ConsoleFontSize = d.ConsoleFontSize, Console = d.Console },
            _ => d,
        });
    }

    public string Export() => JsonSerializer.Serialize(_current, ExportOptions);

    /// <summary>Parses and validates an exported file and lists what would change. Nothing is applied.</summary>
    public AppearanceImportPreview PreviewImport(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(false, null, [], [], "The file is empty.");
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxImportBytes) return new(false, null, [], [], "The file is too large to be an appearance export.");
        AppearanceSettings? parsed;
        try { parsed = JsonSerializer.Deserialize<AppearanceSettings>(json, ExportOptions); }
        catch (JsonException ex) { return new(false, null, [], [], "Not a valid appearance export: " + ex.Message.Split('\n')[0]); }
        if (parsed is null) return new(false, null, [], [], "Not a valid appearance export.");
        var (normalized, notes) = Normalize(parsed);
        return new(true, normalized, Diff(_current, normalized), notes, "Valid. Review the changes, then apply.");
    }

    public OperationResult ApplyImport(AppearanceImportPreview preview) =>
        preview is { Valid: true, Proposed: { } p } ? Update(_ => p) : OperationResult.Fail("Nothing to apply.");

    /// <summary>Bounds every value. Returns the corrected settings and a note per correction.</summary>
    public static (AppearanceSettings Settings, IReadOnlyList<string> Notes) Normalize(AppearanceSettings? s)
    {
        var notes = new List<string>();
        if (s is null) return (new AppearanceSettings(), ["Appearance settings were missing; defaults used."]);
        if (s.SchemaVersion > AppearanceSettings.CurrentSchemaVersion)
            return (new AppearanceSettings(), [$"Appearance settings came from a newer version (schema {s.SchemaVersion}); defaults used."]);
        // Schema migrations go here (none yet: schema 1 is the first). Older or missing versions are read field by field.

        T Enum<T>(T value, T fallback, string what) where T : struct, System.Enum
        {
            if (System.Enum.IsDefined(value)) return value;
            notes.Add($"Unknown {what}; reset to {fallback}.");
            return fallback;
        }
        double Clamp(double v, double min, double max, double def, string what)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) { notes.Add($"{what} was invalid; reset."); return def; }
            var c = Math.Round(Math.Clamp(v, min, max), 2);
            if (Math.Abs(c - v) > 0.001) notes.Add($"{what} limited to {c:0.##}.");
            return c;
        }

        var d = new AppearanceSettings();
        var theme = Enum(s.Theme, d.Theme, "theme");
        var accent = Rgb.TryParse(s.AccentHex, out var rgb) ? rgb.Hex : null;
        if (accent is null) notes.Add("The accent colour was not a valid RRGGBB value; the DarkCipher teal is used.");
        else
        {
            var check = ThemePalettes.CheckAccent(ThemePalettes.Resolve(theme, true), accent);
            if (!check.Ok) { notes.Add($"The accent colour #{accent} is too faint on this theme ({check.Ratio:0.0}:1, needs 3:1); the DarkCipher teal is used."); accent = null; }
        }

        var pages = DashboardCatalog.Pages.Select(p => p.Key).ToHashSet();
        IReadOnlyList<string> Pages(IEnumerable<string>? list, bool forHidden)
        {
            var result = new List<string>();
            foreach (var p in list ?? [])
            {
                if (!pages.Contains(p)) { notes.Add($"Unknown page '{Short(p)}' ignored."); continue; }
                if (forHidden && DashboardCatalog.AlwaysVisible.Contains(p)) { notes.Add($"'{p}' holds security or session controls and cannot be hidden."); continue; }
                if (!result.Contains(p)) result.Add(p);
            }
            return result;
        }

        var hidden = Pages(s.HiddenPages, forHidden: true);
        var landing = pages.Contains(s.LandingPage ?? "") && !hidden.Contains(s.LandingPage!) ? s.LandingPage! : "dashboard";
        if (landing != s.LandingPage) notes.Add("The landing page was unavailable; Dashboard is used.");

        var result = s with
        {
            SchemaVersion = AppearanceSettings.CurrentSchemaVersion,
            Theme = theme,
            AccentHex = accent ?? ThemePalettes.BrandAccent,
            StatusIntensity = Enum(s.StatusIntensity, d.StatusIntensity, "status intensity"),
            Density = Enum(s.Density, d.Density, "density"),
            Corners = Enum(s.Corners, d.Corners, "corner style"),
            Motion = Enum(s.Motion, d.Motion, "animation level"),
            TextScale = Clamp(s.TextScale, 0.85, 1.5, 1.0, "Text size"),
            UiScale = Clamp(s.UiScale, 0.8, 1.5, 1.0, "Interface zoom"),
            Navigation = Enum(s.Navigation, d.Navigation, "navigation style"),
            NavigationWidth = Math.Clamp(s.NavigationWidth <= 0 ? d.NavigationWidth : s.NavigationWidth, 180, 320),
            LandingPage = landing,
            PinnedPages = Pages(s.PinnedPages, forHidden: false).Where(p => !hidden.Contains(p)).Take(6).ToList(),
            HiddenPages = hidden,
            ConsoleFontSize = Math.Clamp(s.ConsoleFontSize <= 0 ? d.ConsoleFontSize : s.ConsoleFontSize, 9, 24),
            Console = Enum(s.Console, d.Console, "console colours"),
            Dashboard = NormalizeDashboard(s.Dashboard, notes),
        };
        return (result, notes);
    }

    /// <summary>Repairs a stored dashboard layout: unknown cards are dropped, duplicates removed, cards added in a newer
    /// version are appended (hidden), wide is kept only where a card supports it, and quick actions are bounded.</summary>
    public static DashboardLayout NormalizeDashboard(DashboardLayout? layout, List<string> notes)
    {
        var d = new DashboardLayout();
        if (layout is null) return d;
        if (layout.LayoutVersion > DashboardLayout.CurrentLayoutVersion)
        {
            notes.Add("The dashboard layout came from a newer version; the default layout is used.");
            return d;
        }
        var known = DashboardCatalog.Cards.ToDictionary(c => c.Id);
        var cards = new List<DashboardCardSetting>();
        foreach (var c in layout.Cards ?? [])
        {
            if (c is null || !known.TryGetValue(c.Id ?? "", out var info)) { notes.Add($"Unknown dashboard card '{Short(c?.Id)}' removed."); continue; }
            if (cards.Any(x => x.Id == c.Id)) { notes.Add($"Duplicate dashboard card '{c.Id}' removed."); continue; }
            var size = System.Enum.IsDefined(c.Size) && (c.Size == CardSize.Normal || info.CanBeWide) ? c.Size : CardSize.Normal;
            cards.Add(c with { Size = size });
        }
        foreach (var def in DashboardCatalog.DefaultCards.Where(x => cards.All(c => c.Id != x.Id)))
            cards.Add(def with { Visible = layout.Cards is null || layout.Cards.Count == 0 ? def.Visible : false });
        if (cards.All(c => !c.Visible))
        {
            notes.Add("Every dashboard card was hidden; Stack readiness is shown again.");
            cards = cards.Select(c => c.Id == "stacks" ? c with { Visible = true } : c).ToList();
        }
        var actions = DashboardCatalog.QuickActions.Select(a => a.Id).ToHashSet();
        var quick = (layout.QuickActions ?? []).Where(actions.Contains).Distinct().Take(DashboardCatalog.MaxQuickActions).ToList();
        return layout with
        {
            LayoutVersion = DashboardLayout.CurrentLayoutVersion,
            Mode = System.Enum.IsDefined(layout.Mode) ? layout.Mode : d.Mode,
            Cards = cards,
            QuickActions = quick,
        };
    }

    public static IReadOnlyList<string> Diff(AppearanceSettings a, AppearanceSettings b)
    {
        var changes = new List<string>();
        void C<T>(string name, T x, T y) { if (!EqualityComparer<T>.Default.Equals(x, y)) changes.Add($"{name}: {x} → {y}"); }
        C("Theme", a.Theme, b.Theme); C("Accent", "#" + a.AccentHex, "#" + b.AccentHex); C("Follow Windows high contrast", a.FollowWindowsHighContrast, b.FollowWindowsHighContrast);
        C("Status colours", a.StatusIntensity, b.StatusIntensity); C("Density", a.Density, b.Density); C("Corners", a.Corners, b.Corners);
        C("Animation", a.Motion, b.Motion); C("Text size", a.TextScale, b.TextScale); C("Interface zoom", a.UiScale, b.UiScale);
        C("Navigation", a.Navigation, b.Navigation); C("Navigation width", a.NavigationWidth, b.NavigationWidth); C("Landing page", a.LandingPage, b.LandingPage);
        C("Pinned pages", string.Join(", ", a.PinnedPages), string.Join(", ", b.PinnedPages));
        C("Hidden pages", string.Join(", ", a.HiddenPages), string.Join(", ", b.HiddenPages));
        C("Developer sections", a.ShowDeveloperSections, b.ShowDeveloperSections); C("Experimental sections", a.ShowExperimentalSections, b.ShowExperimentalSections);
        C("Advanced information", a.ShowAdvancedInformation, b.ShowAdvancedInformation);
        C("Console font size", a.ConsoleFontSize, b.ConsoleFontSize); C("Console colours", a.Console, b.Console);
        C("Dashboard mode", a.Dashboard.Mode, b.Dashboard.Mode);
        C("Dashboard cards", string.Join(", ", a.Dashboard.Cards.Where(c => c.Visible).Select(c => c.Id)), string.Join(", ", b.Dashboard.Cards.Where(c => c.Visible).Select(c => c.Id)));
        C("Quick actions", string.Join(", ", a.Dashboard.QuickActions), string.Join(", ", b.Dashboard.QuickActions));
        return changes;
    }

    private static string Short(string? s) => s is null ? "(none)" : s.Length <= 32 ? s : s[..32] + "…";
}
