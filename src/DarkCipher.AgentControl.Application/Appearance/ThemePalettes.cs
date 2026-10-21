using System.Globalization;

namespace DarkCipher.AgentControl.Application.Appearance;

/// <summary>
/// The DarkCipher Systems colour system, shared with WhisperDesk and ServerManagement. The "DarkCipher" and
/// "DarkCipher Light" base values are copied from their ThemeManager palettes (Bg, Surface, Input, Fg, Muted, Line, Hover,
/// Track, ScrollThumb, HeaderBg, HeaderFg, Good, Warn, Bad; brand accent 36D2CB) and the accent-derived colours use the
/// same formulas. AgentControl adds a few keys the references do not have (Surface2, FgStrong, LineStrong, Info,
/// Experimental, Critical, Unknown); see docs/DESIGN-SYSTEM.md. Pure data and arithmetic: no WPF, fully testable.
/// </summary>
public static class ThemePalettes
{
    public const string BrandAccent = "36D2CB";

    /// <summary>Base keys every preset defines (hex RRGGBB without '#').</summary>
    public static readonly string[] BaseKeys =
    [
        "Bg", "Surface", "Surface2", "Input", "Fg", "FgStrong", "Muted", "Line", "LineStrong", "Hover", "Track", "ScrollThumb",
        "HeaderBg", "HeaderFg", "Good", "Warn", "Bad", "Info", "Experimental", "Critical", "Unknown",
    ];

    public sealed record Preset(ThemePresetId Id, string DisplayName, bool IsDark, string Description, IReadOnlyDictionary<string, string> Colors);

    private static Dictionary<string, string> P(params (string Key, string Hex)[] v) => v.ToDictionary(x => x.Key, x => x.Hex);

    /// <summary>darkcipher0x "terminal" palette, dark — identical base values to WhisperDesk/ServerManagement.</summary>
    public static readonly Preset DarkCipher = new(ThemePresetId.DarkCipher, "DarkCipher", true,
        "The DarkCipher Systems theme shared with WhisperDesk and ServerManagement (default).",
        P(("Bg", "050909"), ("Surface", "0B1314"), ("Surface2", "0E1A1B"), ("Input", "020505"), ("Fg", "D2F2EF"), ("FgStrong", "EAFBF9"),
          ("Muted", "5A8986"), ("Line", "16302F"), ("LineStrong", "234746"), ("Hover", "0F2122"), ("Track", "0F2122"), ("ScrollThumb", "234746"),
          ("HeaderBg", "000000"), ("HeaderFg", "D2F2EF"), ("Good", "36D27A"), ("Warn", "E8C547"), ("Bad", "FF5F6D"),
          ("Info", "5B9BFF"), ("Experimental", "B58CFF"), ("Critical", "FF4D5E"), ("Unknown", "6F8584")));

    public static readonly Preset HighContrast = new(ThemePresetId.DarkCipherHighContrast, "DarkCipher High Contrast", true,
        "Pure black, white text, strong borders and brighter status colours.",
        P(("Bg", "000000"), ("Surface", "000000"), ("Surface2", "0A0F0F"), ("Input", "000000"), ("Fg", "FFFFFF"), ("FgStrong", "FFFFFF"),
          ("Muted", "C7DAD8"), ("Line", "6F9C99"), ("LineStrong", "B9E6E2"), ("Hover", "102A2A"), ("Track", "102A2A"), ("ScrollThumb", "8FBFBB"),
          ("HeaderBg", "000000"), ("HeaderFg", "FFFFFF"), ("Good", "4CFF9A"), ("Warn", "FFD84D"), ("Bad", "FF7A86"),
          ("Info", "8CB8FF"), ("Experimental", "D2B8FF"), ("Critical", "FF6B78"), ("Unknown", "B5C4C3")));

    public static readonly Preset Graphite = new(ThemePresetId.DarkCipherGraphite, "DarkCipher Graphite", true,
        "Neutral graphite surfaces with the DarkCipher accent.",
        P(("Bg", "0A0B0C"), ("Surface", "121416"), ("Surface2", "171A1D"), ("Input", "070809"), ("Fg", "DCE3E6"), ("FgStrong", "F2F5F6"),
          ("Muted", "7D8B91"), ("Line", "23292D"), ("LineStrong", "343C41"), ("Hover", "1A1F22"), ("Track", "1A1F22"), ("ScrollThumb", "343C41"),
          ("HeaderBg", "000000"), ("HeaderFg", "DCE3E6"), ("Good", "36D27A"), ("Warn", "E8C547"), ("Bad", "FF5F6D"),
          ("Info", "5B9BFF"), ("Experimental", "B58CFF"), ("Critical", "FF4D5E"), ("Unknown", "7D8B91")));

    public static readonly Preset Teal = new(ThemePresetId.DarkCipherTeal, "DarkCipher Teal", true,
        "Deeper teal-tinted surfaces; the most saturated DarkCipher look.",
        P(("Bg", "031111"), ("Surface", "071D1E"), ("Surface2", "0A2627"), ("Input", "010909"), ("Fg", "CFF7F3"), ("FgStrong", "EAFFFC"),
          ("Muted", "5FA39E"), ("Line", "134240"), ("LineStrong", "1F5654"), ("Hover", "0B2C2D"), ("Track", "0B2C2D"), ("ScrollThumb", "1F5654"),
          ("HeaderBg", "000808"), ("HeaderFg", "CFF7F3"), ("Good", "36D27A"), ("Warn", "E8C547"), ("Bad", "FF5F6D"),
          ("Info", "5B9BFF"), ("Experimental", "B58CFF"), ("Critical", "FF4D5E"), ("Unknown", "6F9894")));

    /// <summary>"Ink on paper" — the light values of WhisperDesk/ServerManagement.</summary>
    public static readonly Preset Light = new(ThemePresetId.DarkCipherLight, "DarkCipher Light", false,
        "The light DarkCipher palette used by WhisperDesk and ServerManagement.",
        P(("Bg", "F1F6F6"), ("Surface", "E3EDED"), ("Surface2", "EBF3F3"), ("Input", "FAFDFD"), ("Fg", "08191A"), ("FgStrong", "000F10"),
          ("Muted", "4E6E6C"), ("Line", "BCD2D1"), ("LineStrong", "93B2B0"), ("Hover", "D5E5E5"), ("Track", "CFDFDF"), ("ScrollThumb", "A3BEBD"),
          ("HeaderBg", "061010"), ("HeaderFg", "E2F4F2"), ("Good", "137A3F"), ("Warn", "8A6A00"), ("Bad", "B3202E"),
          ("Info", "1F4FB0"), ("Experimental", "6A3BC2"), ("Critical", "B3202E"), ("Unknown", "4E6463")));

    public static IReadOnlyList<Preset> All { get; } = [DarkCipher, HighContrast, Graphite, Teal, Light];

    /// <summary>Accent choices offered in Settings (all pass the contrast rule on every dark preset).</summary>
    public static IReadOnlyList<(string Name, string Hex)> AccentSwatches { get; } =
    [
        ("DarkCipher teal", BrandAccent), ("Cyan", "3FB6FF"), ("Green", "36D27A"), ("Violet", "B58CFF"), ("Amber", "E8C547"), ("Rose", "FF6B9A"),
    ];

    public static string DisplayName(ThemePresetId id) => id == ThemePresetId.FollowWindows ? "Follow Windows (DarkCipher or DarkCipher Light)" : Resolve(id, true).DisplayName;

    /// <summary>The concrete palette for a preset. FollowWindows uses DarkCipher or DarkCipher Light.</summary>
    public static Preset Resolve(ThemePresetId id, bool windowsPrefersDark) => id switch
    {
        ThemePresetId.DarkCipherHighContrast => HighContrast,
        ThemePresetId.DarkCipherGraphite => Graphite,
        ThemePresetId.DarkCipherTeal => Teal,
        ThemePresetId.DarkCipherLight => Light,
        ThemePresetId.FollowWindows => windowsPrefersDark ? DarkCipher : Light,
        _ => DarkCipher,
    };

    /// <summary>
    /// Every colour token for a preset + accent + status intensity, as #RRGGBB (or #AARRGGBB for overlays). Contains the
    /// reference keys, the derived accent keys and the semantic aliases (BackgroundPrimary, TextPrimary, StatusError…).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(Preset preset, string? accentHex, StatusIntensity intensity, ConsolePreset console)
    {
        var c = preset.Colors.ToDictionary(k => k.Key, k => Rgb.Parse(k.Value));
        var dark = preset.IsDark;
        var bg = c["Bg"];
        var brand = Rgb.TryParse(accentHex, out var a) ? a : Rgb.Parse(BrandAccent);
        // Same accent rules as WhisperDesk/ServerManagement: neon accents are darkened on light backgrounds.
        var accent = dark ? brand : brand.Darken(brand.Luminance > 0.45 ? 0.42 : 0.1);
        var output = new Dictionary<string, Rgb>(c)
        {
            ["Accent"] = accent,
            ["AccentHover"] = dark ? accent.Lighten(0.25) : accent.Darken(0.2),
            ["AccentDim"] = Rgb.Mix(bg, accent, dark ? 0.35 : 0.30),
            ["Brand"] = brand,
            ["OnAccent"] = accent.Luminance > 0.5 ? Rgb.Black : Rgb.White,
            ["Selection"] = Rgb.Mix(bg, accent, dark ? 0.18 : 0.20),
            ["HeaderMuted"] = Rgb.Mix(c["HeaderBg"], c["HeaderFg"], 0.62),
            ["FgDisabled"] = Rgb.Mix(c["Surface"], c["Muted"], 0.62),
            ["AltRow"] = Rgb.Mix(c["Surface"], c["Bg"], 0.45),
            ["DangerLine"] = Rgb.Mix(c["Surface"], c["Bad"], 0.40),
        };

        foreach (var key in new[] { "Good", "Warn", "Bad", "Info", "Critical" })
        {
            output[key] = intensity switch
            {
                StatusIntensity.Subtle => Rgb.Mix(c[key], c["Muted"], 0.30),
                StatusIntensity.Strong => dark ? c[key].Lighten(0.18) : c[key].Darken(0.18),
                _ => c[key],
            };
        }

        var (consoleBg, consoleFg) = console switch
        {
            ConsolePreset.Classic => (Rgb.Parse("0C0C0C"), Rgb.Parse("CCCCCC")),
            ConsolePreset.HighContrast => (Rgb.Black, Rgb.White),
            _ => dark ? (c["Input"], c["Fg"]) : (Rgb.Parse("061010"), Rgb.Parse("D2F2EF")),
        };
        output["ConsoleBg"] = consoleBg;
        output["ConsoleFg"] = consoleFg;
        output["ConsoleMuted"] = Rgb.Mix(consoleBg, consoleFg, 0.6);
        output["Chart1"] = accent;
        output["Chart2"] = output["Info"];
        output["Chart3"] = output["Experimental"];
        output["Chart4"] = output["Warn"];

        var hex = output.ToDictionary(k => k.Key, k => "#" + k.Value.Hex);
        hex["Overlay"] = dark ? "#B3000000" : "#80061010";
        hex["Shadow"] = dark ? "#66000000" : "#33061010";
        foreach (var (semantic, source) in SemanticAliases) hex[semantic] = hex[source];
        return hex;
    }

    /// <summary>Spec-level semantic names → the shared DarkCipher keys (both are published as resources).</summary>
    public static readonly IReadOnlyList<(string Semantic, string Source)> SemanticAliases =
    [
        ("BackgroundPrimary", "Bg"), ("BackgroundSecondary", "HeaderBg"), ("SurfacePrimary", "Surface"), ("SurfaceElevated", "Surface2"),
        ("SurfaceInteractive", "Hover"), ("SurfaceInput", "Input"), ("BorderSubtle", "Line"), ("BorderStrong", "LineStrong"),
        ("TextPrimary", "Fg"), ("TextStrong", "FgStrong"), ("TextSecondary", "Muted"), ("TextDisabled", "FgDisabled"),
        ("AccentPrimary", "Accent"), ("AccentSecondary", "AccentDim"), ("AccentHoverState", "AccentHover"), ("SelectedState", "Selection"),
        ("FocusRing", "Accent"), ("StatusSuccess", "Good"), ("StatusInformation", "Info"), ("StatusWarning", "Warn"), ("StatusError", "Bad"),
        ("StatusCritical", "Critical"), ("StatusOffline", "Unknown"), ("StatusUnknown", "Unknown"), ("StatusExperimental", "Experimental"),
    ];

    /// <summary>Accent rule: the accent (after light-theme darkening) must reach 3:1 against the background (WCAG 2.2 non-text
    /// contrast) so focus rings, selection bars and primary buttons stay visible.</summary>
    public static (bool Ok, double Ratio) CheckAccent(Preset preset, string hex)
    {
        if (!Rgb.TryParse(hex, out var brand)) return (false, 0);
        var accent = preset.IsDark ? brand : brand.Darken(brand.Luminance > 0.45 ? 0.42 : 0.1);
        var ratio = Rgb.Contrast(accent, Rgb.Parse(preset.Colors["Bg"]));
        return (ratio >= 3.0, ratio);
    }
}

/// <summary>An sRGB colour with the mixing and WCAG contrast arithmetic the theme needs.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Black => new(0, 0, 0);
    public static Rgb White => new(255, 255, 255);

    public string Hex => $"{R:X2}{G:X2}{B:X2}";

    /// <summary>The perceptual weighting the reference apps use to decide accent darkening and on-accent text.</summary>
    public double Luminance => (0.2126 * R + 0.7152 * G + 0.0722 * B) / 255.0;

    /// <summary>WCAG relative luminance (linearised sRGB).</summary>
    public double RelativeLuminance
    {
        get
        {
            static double L(byte v) { var s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * L(R) + 0.7152 * L(G) + 0.0722 * L(B);
        }
    }

    public static double Contrast(Rgb a, Rgb b)
    {
        var (x, y) = (a.RelativeLuminance, b.RelativeLuminance);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    public static Rgb Mix(Rgb a, Rgb b, double t) =>
        new((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public Rgb Darken(double t) => Mix(this, Black, t);
    public Rgb Lighten(double t) => Mix(this, White, t);

    public static Rgb Parse(string hex) => TryParse(hex, out var c) ? c : throw new FormatException($"'{hex}' is not an RRGGBB colour.");

    public static bool TryParse(string? hex, out Rgb value)
    {
        value = default;
        hex = (hex ?? "").Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        value = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }
}
