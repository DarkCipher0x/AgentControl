using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DarkCipher.AgentControl.Application.Appearance;

namespace DarkCipher.AgentControl.Tests.Appearance;

public partial class IconTests
{
    private static string IconDir => Path.Combine(TempDir.RepoRoot, "src", "DarkCipher.AgentControl.App", "Assets", "AppIcon");

    /// <summary>Reads the ICO directory: (width, height, bitsPerPixel, isPng) per frame.</summary>
    private static List<(int W, int H, int Bpp, bool Png)> Frames(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        var count = BitConverter.ToUInt16(bytes, 4);
        var frames = new List<(int, int, int, bool)>();
        for (var i = 0; i < count; i++)
        {
            var e = 6 + i * 16;
            int w = bytes[e] == 0 ? 256 : bytes[e], h = bytes[e + 1] == 0 ? 256 : bytes[e + 1];
            var bpp = BitConverter.ToUInt16(bytes, e + 6);
            var size = BitConverter.ToInt32(bytes, e + 8);
            var offset = BitConverter.ToInt32(bytes, e + 12);
            Assert.True(offset + size <= bytes.Length, $"frame {i} points past the end of the file");
            var png = bytes[offset] == 0x89 && bytes[offset + 1] == (byte)'P';
            if (!png) Assert.Equal(40, BitConverter.ToInt32(bytes, offset)); // BITMAPINFOHEADER
            frames.Add((w, h, bpp, png));
        }
        return frames;
    }

    [Fact]
    public void Icon_files_exist_master_png_set_and_ico()
    {
        Assert.True(File.Exists(Path.Combine(IconDir, "app.ico")));
        Assert.True(File.Exists(Path.Combine(IconDir, "app-master.svg")));
        foreach (var s in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256, 512 })
        {
            var png = Path.Combine(IconDir, $"app-{s}.png");
            Assert.True(File.Exists(png), $"missing {s}px PNG");
            var b = File.ReadAllBytes(png);
            Assert.Equal(0x89, b[0]);
            Assert.Equal(s, (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19]);   // IHDR width
            Assert.Equal(6, b[25]);                                                  // colour type 6 = RGBA (transparent)
        }
        var svg = XDocument.Load(Path.Combine(IconDir, "app-master.svg"));
        Assert.Equal("svg", svg.Root!.Name.LocalName);
        Assert.DoesNotContain("<text", File.ReadAllText(Path.Combine(IconDir, "app-master.svg")), StringComparison.OrdinalIgnoreCase); // no readable text in the mark
    }

    [Fact]
    public void Ico_has_every_required_frame_32bit_bmp_below_256_and_png_at_256()
    {
        var frames = Frames(Path.Combine(IconDir, "app.ico"));
        var sizes = frames.Select(f => f.W).ToHashSet();
        foreach (var s in new[] { 16, 20, 24, 32, 48, 64, 128, 256 }) Assert.Contains(s, sizes);
        Assert.All(frames, f => { Assert.Equal(f.W, f.H); Assert.Equal(32, f.Bpp); Assert.Equal(f.W >= 256, f.Png); });
    }

    [Fact]
    public void Icon_corners_are_transparent_and_centre_is_opaque()
    {
        // 16 px BMP frame: the corners are outside the hexagon (no background box), the centre is the mark.
        var bytes = File.ReadAllBytes(Path.Combine(IconDir, "app.ico"));
        var frames = Frames(Path.Combine(IconDir, "app.ico"));
        var index = frames.FindIndex(f => f.W == 16);
        var offset = BitConverter.ToInt32(bytes, 6 + index * 16 + 12);
        byte Alpha(int x, int y) => bytes[offset + 40 + ((15 - y) * 16 + x) * 4 + 3];   // bottom-up BGRA rows
        Assert.Equal(0, Alpha(0, 0));
        Assert.Equal(0, Alpha(15, 0));
        Assert.Equal(0, Alpha(0, 15));
        Assert.Equal(255, Alpha(8, 8));
    }

    [Fact]
    public void Project_embeds_the_icon_and_ships_it_as_a_resource_and_in_the_installer()
    {
        var csproj = File.ReadAllText(Path.Combine(TempDir.RepoRoot, "src", "DarkCipher.AgentControl.App", "DarkCipher.AgentControl.App.csproj"));
        Assert.Contains(@"<ApplicationIcon>Assets\AppIcon\app.ico</ApplicationIcon>", csproj, StringComparison.Ordinal);
        Assert.Contains(@"<Resource Include=""Assets\AppIcon\app.ico"" />", csproj, StringComparison.Ordinal);
        Assert.Contains(@"<Resource Include=""Assets\AppIcon\app-256.png"" />", csproj, StringComparison.Ordinal);
        var wxs = File.ReadAllText(Path.Combine(TempDir.RepoRoot, "installer", "DarkCipherAgentControl.wxs"));
        Assert.Contains("<Icon Id=\"AppIcon.ico\"", wxs, StringComparison.Ordinal);
        Assert.Contains("ARPPRODUCTICON", wxs, StringComparison.Ordinal);
        Assert.Contains("Icon=\"AppIcon.ico\"", wxs, StringComparison.Ordinal);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [Fact]
    public void Built_executable_has_an_embedded_icon()
    {
        var bin = Path.Combine(TempDir.RepoRoot, "src", "DarkCipher.AgentControl.App", "bin");
        var exe = Directory.Exists(bin) ? Directory.EnumerateFiles(bin, "DarkCipherAgentControl.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        Assert.True(exe is not null, "Build the App project first (the test checks the real executable).");
        Assert.True(ExtractIconExW(exe!, -1, null, null, 0) >= 1, "no icon group in " + exe);
    }
}

public class ThemeTests
{
    [Fact]
    public void DarkCipher_preset_uses_the_reference_palette_from_WhisperDesk_and_ServerManagement()
    {
        var c = ThemePalettes.DarkCipher.Colors;
        Assert.Equal("050909", c["Bg"]);
        Assert.Equal("0B1314", c["Surface"]);
        Assert.Equal("020505", c["Input"]);
        Assert.Equal("D2F2EF", c["Fg"]);
        Assert.Equal("5A8986", c["Muted"]);
        Assert.Equal("16302F", c["Line"]);
        Assert.Equal("0F2122", c["Hover"]);
        Assert.Equal("000000", c["HeaderBg"]);
        Assert.Equal("36D27A", c["Good"]);
        Assert.Equal("E8C547", c["Warn"]);
        Assert.Equal("FF5F6D", c["Bad"]);
        Assert.Equal("36D2CB", ThemePalettes.BrandAccent);
        var light = ThemePalettes.Light.Colors;
        Assert.Equal("F1F6F6", light["Bg"]);
        Assert.Equal("08191A", light["Fg"]);
    }

    [Fact]
    public void Accent_derivations_match_the_reference_formulas()
    {
        var t = ThemePalettes.Build(ThemePalettes.DarkCipher, "36D2CB", StatusIntensity.Standard, ConsolePreset.DarkCipher);
        Assert.Equal("#36D2CB", t["Accent"]);
        Assert.Equal("#" + Rgb.Parse("36D2CB").Lighten(0.25).Hex, t["AccentHover"]);
        Assert.Equal("#000000", t["OnAccent"]);
        Assert.Equal("#" + Rgb.Mix(Rgb.Parse("050909"), Rgb.Parse("36D2CB"), 0.18).Hex, t["Selection"]);
        var l = ThemePalettes.Build(ThemePalettes.Light, "36D2CB", StatusIntensity.Standard, ConsolePreset.DarkCipher);
        Assert.Equal("#" + Rgb.Parse("36D2CB").Darken(0.42).Hex, l["Accent"]);   // neon accent darkened on light backgrounds
    }

    [Fact]
    public void Every_preset_defines_every_base_key_and_semantic_alias()
    {
        foreach (var p in ThemePalettes.All)
        {
            foreach (var k in ThemePalettes.BaseKeys) Assert.True(p.Colors.ContainsKey(k), $"{p.DisplayName} lacks {k}");
            var t = ThemePalettes.Build(p, null, StatusIntensity.Standard, ConsolePreset.DarkCipher);
            foreach (var (semantic, source) in ThemePalettes.SemanticAliases) Assert.Equal(t[source], t[semantic]);
            foreach (var k in new[] { "BackgroundPrimary", "SurfaceElevated", "BorderSubtle", "TextPrimary", "TextDisabled", "AccentPrimary", "StatusSuccess", "StatusInformation", "StatusWarning", "StatusError", "StatusUnknown", "FocusRing" })
                Assert.True(t.ContainsKey(k), k);
        }
    }

    [Fact]
    public void Text_and_status_colours_are_readable_on_every_preset()
    {
        foreach (var p in ThemePalettes.All)
        {
            var c = p.Colors.ToDictionary(k => k.Key, k => Rgb.Parse(k.Value));
            Assert.True(Rgb.Contrast(c["Fg"], c["Bg"]) >= 7, $"{p.DisplayName}: body text below 7:1");
            Assert.True(Rgb.Contrast(c["Fg"], c["Surface"]) >= 7, $"{p.DisplayName}: text on cards below 7:1");
            Assert.True(Rgb.Contrast(c["Muted"], c["Surface"]) >= 4.5, $"{p.DisplayName}: secondary text below 4.5:1");
            Assert.True(Rgb.Contrast(c["HeaderFg"], c["HeaderBg"]) >= 7, $"{p.DisplayName}: header text");
            foreach (var s in new[] { "Good", "Warn", "Bad", "Info", "Experimental" })
                Assert.True(Rgb.Contrast(c[s], c["Surface"]) >= 3, $"{p.DisplayName}: {s} status below 3:1");
            Assert.True(ThemePalettes.CheckAccent(p, ThemePalettes.BrandAccent).Ok, $"{p.DisplayName}: brand accent below 3:1");
        }
        var hc = ThemePalettes.HighContrast.Colors;
        Assert.True(Rgb.Contrast(Rgb.Parse(hc["Muted"]), Rgb.Parse(hc["Bg"])) >= 7);
    }

    [Fact]
    public void Faint_accents_are_refused_and_every_offered_swatch_passes_on_dark_presets()
    {
        Assert.False(ThemePalettes.CheckAccent(ThemePalettes.DarkCipher, "101818").Ok);
        Assert.False(ThemePalettes.CheckAccent(ThemePalettes.DarkCipher, "not-a-colour").Ok);
        foreach (var p in ThemePalettes.All.Where(p => p.IsDark))
            foreach (var (_, hex) in ThemePalettes.AccentSwatches) Assert.True(ThemePalettes.CheckAccent(p, hex).Ok, $"{hex} on {p.DisplayName}");
    }

    [Fact]
    public void Status_intensity_changes_status_colours_only()
    {
        var std = ThemePalettes.Build(ThemePalettes.DarkCipher, null, StatusIntensity.Standard, ConsolePreset.DarkCipher);
        var subtle = ThemePalettes.Build(ThemePalettes.DarkCipher, null, StatusIntensity.Subtle, ConsolePreset.DarkCipher);
        var strong = ThemePalettes.Build(ThemePalettes.DarkCipher, null, StatusIntensity.Strong, ConsolePreset.DarkCipher);
        Assert.NotEqual(std["Bad"], subtle["Bad"]);
        Assert.NotEqual(std["Bad"], strong["Bad"]);
        Assert.Equal(std["Bg"], subtle["Bg"]);
        Assert.Equal(std["Fg"], strong["Fg"]);
    }

    [Fact]
    public void Follow_windows_resolves_to_dark_or_light()
    {
        Assert.Same(ThemePalettes.DarkCipher, ThemePalettes.Resolve(ThemePresetId.FollowWindows, true));
        Assert.Same(ThemePalettes.Light, ThemePalettes.Resolve(ThemePresetId.FollowWindows, false));
        Assert.Same(ThemePalettes.Graphite, ThemePalettes.Resolve(ThemePresetId.DarkCipherGraphite, false));
    }
}

/// <summary>Static checks of the XAML: tokens exist, the ramp matches ServerManagement, and views use tokens only.</summary>
public partial class DesignTokenTests
{
    private static readonly string AppDir = Path.Combine(TempDir.RepoRoot, "src", "DarkCipher.AgentControl.App");
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static Dictionary<string, XElement> ThemeResources()
    {
        var doc = XDocument.Load(Path.Combine(AppDir, "Themes", "Theme.xaml"));
        return doc.Root!.Elements().Where(e => e.Attribute(X + "Key") is not null).ToDictionary(e => e.Attribute(X + "Key")!.Value);
    }

    [Fact]
    public void Theme_xaml_declares_every_colour_token_the_theme_service_publishes()
    {
        var res = ThemeResources();
        var tokens = ThemePalettes.Build(ThemePalettes.DarkCipher, null, StatusIntensity.Standard, ConsolePreset.DarkCipher).Keys
            .Where(k => ThemePalettes.SemanticAliases.All(a => a.Semantic != k) && !k.StartsWith("Chart", StringComparison.Ordinal) && k != "Shadow");
        foreach (var k in tokens) Assert.True(res.ContainsKey(k), $"Theme.xaml lacks a default for '{k}'");
    }

    [Fact]
    public void Type_ramp_and_fonts_match_ServerManagement_typography()
    {
        var res = ThemeResources();
        (string Key, double Px)[] reference =
        [
            ("FsMicro", 10.5), ("FsCaption", 11), ("FsSmall", 11.5), ("FsMeta", 12), ("FsBody", 12.5), ("FsControl", 13), ("FsBase", 13.5),
            ("FsInput", 14), ("FsTitle", 15), ("FsHeading", 16), ("FsH1", 17), ("FsCode", 18), ("FsValue", 20), ("FsIcon", 26),
        ];
        foreach (var (k, px) in reference) Assert.Equal(px, double.Parse(res[k].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Segoe UI Variable Text, Segoe UI", res["UiFont"].Value);
        Assert.Equal("Cascadia Mono, Cascadia Code, Consolas, Courier New", res["MonoFont"].Value);
        foreach (var style in new[] { "Display", "PageTitle", "SectionTitle", "CardTitle", "Body", "BodySecondary", "Caption", "Label", "Code", "Console" })
            Assert.True(res.ContainsKey(style), $"missing typography style {style}");
    }

    [GeneratedRegex(@"#[0-9A-Fa-f]{6,8}\b")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"\bFontSize=""[0-9]")]
    private static partial Regex LiteralFontSize();

    [GeneratedRegex(@"\bFontFamily=""(?!\{)")]
    private static partial Regex LiteralFontFamily();

    [GeneratedRegex(@"\{StaticResource (Bg|Surface|Surface2|Input|Fg|FgStrong|Muted|Line|LineStrong|Hover|Selection|Accent|AccentHover|AccentDim|OnAccent|Good|Warn|Bad|Info|Experimental|Fs\w+|UiFont|MonoFont|ChromeFont)\}")]
    private static partial Regex StaticToken();

    [Fact]
    public void Views_use_tokens_only_no_raw_colours_fonts_or_sizes()
    {
        var files = Directory.EnumerateFiles(Path.Combine(AppDir, "Views"), "*.xaml").Append(Path.Combine(AppDir, "Shell", "MainWindow.xaml"))
            .Append(Path.Combine(AppDir, "Themes", "Templates.xaml"));
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            Assert.False(HexColour().IsMatch(text), $"{Path.GetFileName(f)} contains a raw colour");
            Assert.False(LiteralFontSize().IsMatch(text), $"{Path.GetFileName(f)} contains a literal font size");
            Assert.False(LiteralFontFamily().IsMatch(text), $"{Path.GetFileName(f)} contains a literal font family");
            Assert.False(StaticToken().IsMatch(text), $"{Path.GetFileName(f)} binds a theme token statically (it would not follow preset changes)");
        }
    }
}
