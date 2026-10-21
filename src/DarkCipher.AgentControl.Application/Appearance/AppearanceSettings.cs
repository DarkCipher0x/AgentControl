namespace DarkCipher.AgentControl.Application.Appearance;

public enum ThemePresetId { DarkCipher, DarkCipherHighContrast, DarkCipherGraphite, DarkCipherTeal, DarkCipherLight, FollowWindows }
public enum Density { Comfortable, Compact }
public enum CornerStyle { Sharp, Standard, Soft }
public enum MotionLevel { FollowWindows, Standard, Reduced }
public enum StatusIntensity { Subtle, Standard, Strong }
public enum NavigationStyle { Expanded, Compact, IconOnly }
public enum ConsolePreset { DarkCipher, Classic, HighContrast }
public enum DashboardMode { Detailed, Compact }
public enum CardSize { Normal, Wide }

public sealed record DashboardCardSetting(string Id, bool Visible, CardSize Size = CardSize.Normal);

public sealed record DashboardLayout
{
    public const int CurrentLayoutVersion = 1;
    public int LayoutVersion { get; init; } = CurrentLayoutVersion;
    public DashboardMode Mode { get; init; } = DashboardMode.Detailed;
    public IReadOnlyList<DashboardCardSetting> Cards { get; init; } = DashboardCatalog.DefaultCards;
    public IReadOnlyList<string> QuickActions { get; init; } = ["setup"];
    public bool ShowHardwareDetails { get; init; } = true;
    /// <summary>Health filter for the stack list: show every stack, or only the ones that need attention.</summary>
    public bool OnlyStacksNeedingAttention { get; init; }
}

/// <summary>Appearance, layout and dashboard preferences. Contains no secrets and no paths, so it can be exported as is.</summary>
public sealed record AppearanceSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ThemePresetId Theme { get; init; } = ThemePresetId.DarkCipher;
    public string AccentHex { get; init; } = ThemePalettes.BrandAccent;
    public bool FollowWindowsHighContrast { get; init; } = true;
    public StatusIntensity StatusIntensity { get; init; } = StatusIntensity.Standard;
    public Density Density { get; init; } = Density.Comfortable;
    public CornerStyle Corners { get; init; } = CornerStyle.Standard;
    public MotionLevel Motion { get; init; } = MotionLevel.FollowWindows;
    /// <summary>Text size multiplier for the type ramp (ServerManagement's "UI font size"): 0.85-1.5.</summary>
    public double TextScale { get; init; } = 1.0;
    /// <summary>Whole-interface zoom: 0.8-1.5.</summary>
    public double UiScale { get; init; } = 1.0;
    public NavigationStyle Navigation { get; init; } = NavigationStyle.Expanded;
    public int NavigationWidth { get; init; } = 218;
    public string LandingPage { get; init; } = "dashboard";
    public IReadOnlyList<string> PinnedPages { get; init; } = [];
    public IReadOnlyList<string> HiddenPages { get; init; } = [];
    public bool ShowDeveloperSections { get; init; } = true;
    public bool ShowExperimentalSections { get; init; }
    public bool ShowAdvancedInformation { get; init; } = true;
    public int ConsoleFontSize { get; init; } = 12;
    public ConsolePreset Console { get; init; } = ConsolePreset.DarkCipher;
    public DashboardLayout Dashboard { get; init; } = new();
}

/// <summary>What the dashboard can show, the destinations navigation knows about, and which of them may never be hidden.</summary>
public static class DashboardCatalog
{
    public sealed record CardInfo(string Id, string Title, string Description, bool CanBeWide);

    public static readonly IReadOnlyList<CardInfo> Cards =
    [
        new("hardware", "This PC", "Hardware rating and what it can run.", false),
        new("runtime", "Runtime", "Ollama status.", false),
        new("activity", "Activity", "Running sessions and blocked operations.", false),
        new("stacks", "Stack readiness", "Every stack with the reason behind its status.", true),
        new("agents", "Agents", "Installed agent tools.", true),
        new("loaded-models", "Loaded models", "Models in memory and GPU share.", true),
        new("downloads", "Downloads and installs", "Active and recent queue items.", true),
        new("verified", "Verified configurations", "Combinations that passed end-to-end verification on this PC.", true),
    ];

    public static IReadOnlyList<DashboardCardSetting> DefaultCards { get; } =
    [
        new("hardware", true), new("runtime", true), new("activity", true), new("stacks", true, CardSize.Wide),
        new("agents", true), new("loaded-models", true), new("downloads", false, CardSize.Wide), new("verified", false, CardSize.Wide),
    ];

    public sealed record QuickActionInfo(string Id, string Title);

    public static readonly IReadOnlyList<QuickActionInfo> QuickActions =
    [
        new("setup", "Get my local coding AI ready"), new("catalogue", "Open the catalogue"), new("templates", "Build a stack from a template"),
        new("verify", "Verify all stacks"), new("sessions", "Start a session"), new("downloads", "Open downloads"),
    ];

    public const int MaxQuickActions = 4;

    /// <summary>Navigation destinations (key → title). Order is the default sidebar order.</summary>
    public static readonly IReadOnlyList<(string Key, string Title)> Pages =
    [
        ("dashboard", "Dashboard"), ("setup", "Quick Setup"), ("catalogue", "Catalogue"), ("agents", "Agents"), ("models", "Models"),
        ("templates", "Templates"), ("stacks", "Stacks"), ("projects", "Projects"), ("sessions", "Sessions"), ("services", "Services"),
        ("hardware", "Hardware"), ("downloads", "Downloads"), ("extensions", "Extensions"), ("developer", "Developer / SDK"),
        ("security", "Security"), ("logs", "Logs"), ("settings", "Settings"),
    ];

    /// <summary>Destinations that hold security controls or active-session controls; they can never be hidden.</summary>
    public static readonly IReadOnlySet<string> AlwaysVisible = new HashSet<string>(["dashboard", "sessions", "security", "settings"]);
}
