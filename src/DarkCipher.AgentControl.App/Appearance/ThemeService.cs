using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DarkCipher.AgentControl.Application.Appearance;
using Microsoft.Win32;

namespace DarkCipher.AgentControl.App.Appearance;

/// <summary>
/// Paints the application resources with a DarkCipher preset (ThemePalettes) and darkens window title bars, like
/// WhisperDesk/ServerManagement's ThemeManager. Every brush is replaced (frozen) so views pick it up through
/// {DynamicResource}. Windows high-contrast mode switches to the High Contrast preset unless the user turned that off.
/// </summary>
public sealed class ThemeService
{
    public ThemePalettes.Preset Active { get; private set; } = ThemePalettes.DarkCipher;
    public bool IsDark => Active.IsDark;
    /// <summary>True when Windows high contrast is on and forced the High Contrast preset.</summary>
    public bool HighContrastForced { get; private set; }
    public IReadOnlyDictionary<string, string> Tokens { get; private set; } = new Dictionary<string, string>();

    public event EventHandler? Changed;

    public void Apply(AppearanceSettings s, ResourceDictionary resources)
    {
        HighContrastForced = s.FollowWindowsHighContrast && SystemParameters.HighContrast;
        Active = HighContrastForced ? ThemePalettes.HighContrast : ThemePalettes.Resolve(s.Theme, SystemPrefersDark());
        Tokens = ThemePalettes.Build(Active, s.AccentHex, s.StatusIntensity, s.Console);
        foreach (var (key, hex) in Tokens)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
            resources[key + "Color"] = color;
        }
        if (System.Windows.Application.Current is { } app)
            foreach (Window w in app.Windows) ApplyTitleBar(w);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return true; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark or light window title bar to match the theme (DWMWA_USE_IMMERSIVE_DARK_MODE).</summary>
    public void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }
}
