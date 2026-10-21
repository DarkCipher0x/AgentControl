using DarkCipher.AgentControl.Application.Appearance;

namespace DarkCipher.AgentControl.Tests.Appearance;

public class AppearanceSettingsTests
{
    [Fact]
    public void Defaults_preserve_the_existing_interface()
    {
        var s = new AppearanceSettings();
        Assert.Equal(ThemePresetId.DarkCipher, s.Theme);
        Assert.Equal(NavigationStyle.Expanded, s.Navigation);
        Assert.Equal(218, s.NavigationWidth);
        Assert.Equal("dashboard", s.LandingPage);
        Assert.Equal(1.0, s.TextScale);
        Assert.True(s.ShowDeveloperSections);
        Assert.Equal(["hardware", "runtime", "activity", "stacks", "agents", "loaded-models"], s.Dashboard.Cards.Where(c => c.Visible).Select(c => c.Id));
    }

    [Fact]
    public void Out_of_range_and_unknown_values_are_bounded_with_notes()
    {
        var (s, notes) = AppearanceSettingsService.Normalize(new AppearanceSettings
        {
            TextScale = 9, UiScale = -1, NavigationWidth = 5000, ConsoleFontSize = 200, AccentHex = "zzz",
            Theme = (ThemePresetId)99, LandingPage = "nowhere", PinnedPages = ["models", "models", "bogus", "a", "b"], HiddenPages = ["security", "logs"],
        });
        Assert.Equal(1.5, s.TextScale);
        Assert.Equal(1.0, s.UiScale);
        Assert.Equal(320, s.NavigationWidth);
        Assert.Equal(24, s.ConsoleFontSize);
        Assert.Equal(ThemePalettes.BrandAccent, s.AccentHex);
        Assert.Equal(ThemePresetId.DarkCipher, s.Theme);
        Assert.Equal("dashboard", s.LandingPage);
        Assert.Equal(["models"], s.PinnedPages);
        Assert.Equal(["logs"], s.HiddenPages);                 // security can never be hidden
        Assert.Contains(notes, n => n.Contains("cannot be hidden", StringComparison.Ordinal));
        Assert.True(notes.Count >= 6);
    }

    [Fact]
    public void Security_and_session_pages_can_never_be_hidden()
    {
        var (s, _) = AppearanceSettingsService.Normalize(new AppearanceSettings { HiddenPages = ["dashboard", "sessions", "security", "settings"] });
        Assert.Empty(s.HiddenPages);
    }

    [Fact]
    public void Faint_custom_accent_is_replaced_by_the_brand_teal()
    {
        var (s, notes) = AppearanceSettingsService.Normalize(new AppearanceSettings { AccentHex = "0A1010" });
        Assert.Equal(ThemePalettes.BrandAccent, s.AccentHex);
        Assert.Contains(notes, n => n.Contains("too faint", StringComparison.Ordinal));
    }

    [Fact]
    public void Newer_schema_resets_to_defaults_instead_of_guessing()
    {
        var (s, notes) = AppearanceSettingsService.Normalize(new AppearanceSettings { SchemaVersion = 99, Theme = ThemePresetId.DarkCipherTeal });
        Assert.Equal(ThemePresetId.DarkCipher, s.Theme);
        Assert.Single(notes);
    }

    [Fact]
    public void Incompatible_dashboard_layouts_are_repaired_not_crashed()
    {
        var notes = new List<string>();
        var repaired = AppearanceSettingsService.NormalizeDashboard(new DashboardLayout
        {
            Cards = [new("stacks", true, CardSize.Wide), new("removed-card", true), new("stacks", true), new("hardware", true, CardSize.Wide)],
            QuickActions = ["setup", "bogus", "setup", "catalogue", "templates", "verify", "sessions"],
        }, notes);
        Assert.Equal("stacks", repaired.Cards[0].Id);
        Assert.Equal(CardSize.Normal, repaired.Cards.First(c => c.Id == "hardware").Size);   // hardware cannot be wide
        Assert.Equal(DashboardCatalog.Cards.Count, repaired.Cards.Count);                     // missing cards appended
        Assert.Equal(1, repaired.Cards.Count(c => c.Id == "stacks"));
        Assert.False(repaired.Cards.First(c => c.Id == "agents").Visible);                    // new cards arrive hidden
        Assert.Equal(DashboardCatalog.MaxQuickActions, repaired.QuickActions.Count);
        Assert.DoesNotContain("bogus", repaired.QuickActions);
        var newer = AppearanceSettingsService.NormalizeDashboard(new DashboardLayout { LayoutVersion = 7 }, notes);
        Assert.Equal(new DashboardLayout().Cards.Count, newer.Cards.Count);
    }

    [Fact]
    public void All_cards_hidden_brings_stack_readiness_back()
    {
        var notes = new List<string>();
        var repaired = AppearanceSettingsService.NormalizeDashboard(new DashboardLayout { Cards = DashboardCatalog.DefaultCards.Select(c => c with { Visible = false }).ToList() }, notes);
        Assert.True(repaired.Cards.First(c => c.Id == "stacks").Visible);
    }

    [Fact]
    public void Settings_persist_and_raise_changes()
    {
        var store = new MemoryStore();
        var svc = new AppearanceSettingsService(store);
        AppearanceSettings? seen = null;
        svc.Changed += (_, s) => seen = s;
        svc.Update(s => s with { Theme = ThemePresetId.DarkCipherGraphite, Density = Density.Compact });
        Assert.Equal(Density.Compact, seen!.Density);
        var again = new AppearanceSettingsService(store);
        Assert.Equal(ThemePresetId.DarkCipherGraphite, again.Current.Theme);
        Assert.Equal(Density.Compact, again.Current.Density);
    }

    [Fact]
    public void Reset_by_category_leaves_other_categories_alone()
    {
        var svc = new AppearanceSettingsService(new MemoryStore());
        svc.Update(s => s with { Theme = ThemePresetId.DarkCipherTeal, Density = Density.Compact, ConsoleFontSize = 18 });
        svc.Reset(AppearanceCategory.Theme);
        Assert.Equal(ThemePresetId.DarkCipher, svc.Current.Theme);
        Assert.Equal(Density.Compact, svc.Current.Density);
        Assert.Equal(18, svc.Current.ConsoleFontSize);
        svc.Reset(null);
        Assert.Empty(AppearanceSettingsService.Diff(new AppearanceSettings(), svc.Current));
    }

    [Fact]
    public void Export_contains_no_secrets_paths_or_credentials_and_imports_with_a_preview()
    {
        var a = new AppearanceSettingsService(new MemoryStore());
        a.Update(s => s with { Theme = ThemePresetId.DarkCipherGraphite, PinnedPages = ["models"] });
        var json = a.Export();
        foreach (var bad in new[] { "token", "password", "credential", "C:\\", "Users", "apikey" }) Assert.DoesNotContain(bad, json, StringComparison.OrdinalIgnoreCase);

        var b = new AppearanceSettingsService(new MemoryStore());
        var preview = b.PreviewImport(json);
        Assert.True(preview.Valid);
        Assert.Contains(preview.Changes, c => c.StartsWith("Theme:", StringComparison.Ordinal));
        Assert.Equal(ThemePresetId.DarkCipher, b.Current.Theme);       // preview changes nothing
        b.ApplyImport(preview);
        Assert.Equal(ThemePresetId.DarkCipherGraphite, b.Current.Theme);
    }

    [Fact]
    public void Malformed_or_hostile_imports_are_refused_or_neutralised()
    {
        var svc = new AppearanceSettingsService(new MemoryStore());
        Assert.False(svc.PreviewImport("{ not json").Valid);
        Assert.False(svc.PreviewImport(new string(' ', 70_000) + "{}").Valid);
        var hostile = svc.PreviewImport("""{ "landingPage": "C:\\Windows\\System32", "pinnedPages": ["hf_abcdefghijklmnopqrstuv"], "accentHex": "sk-test" }""");
        Assert.True(hostile.Valid);
        Assert.Equal("dashboard", hostile.Proposed!.LandingPage);
        Assert.Empty(hostile.Proposed.PinnedPages);
        Assert.Equal(ThemePalettes.BrandAccent, hostile.Proposed.AccentHex);
    }
}
