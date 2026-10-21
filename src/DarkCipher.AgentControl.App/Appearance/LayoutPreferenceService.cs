using System.Windows;
using System.Windows.Controls;
using DarkCipher.AgentControl.Application.Appearance;

namespace DarkCipher.AgentControl.App.Appearance;

/// <summary>
/// Spacing, corner radius, navigation width and motion. Comfortable is the designed spacing; Compact tightens rows and
/// padding the way ServerManagement's density setting does. Reduced motion swaps the indeterminate progress animation for
/// a still bar (the only animation in the app). "Follow Windows" uses the Windows "Animation effects" setting.
/// </summary>
public sealed class LayoutPreferenceService
{
    public bool ReducedMotion { get; private set; }

    public static double NavWidthFor(AppearanceSettings s) => s.Navigation switch
    {
        NavigationStyle.IconOnly => 58,
        NavigationStyle.Compact => Math.Min(s.NavigationWidth, 184),
        _ => s.NavigationWidth,
    };

    public void Apply(AppearanceSettings s, ResourceDictionary r)
    {
        var compact = s.Density == Density.Compact;
        r["CardPadding"] = compact ? new Thickness(12, 9, 12, 9) : new Thickness(16, 14, 16, 14);
        r["CardMargin"] = compact ? new Thickness(0, 0, 8, 8) : new Thickness(0, 0, 12, 12);
        r["ButtonPadding"] = compact ? new Thickness(10, 3, 10, 3) : new Thickness(12, 5, 12, 5);
        r["InputPadding"] = compact ? new Thickness(5, 2, 5, 2) : new Thickness(6, 4, 6, 4);
        r["ListRowPadding"] = compact ? new Thickness(6, 2, 6, 2) : new Thickness(8, 5, 8, 5);
        r["NavItemPadding"] = s.Navigation == NavigationStyle.IconOnly ? new Thickness(18, 8, 8, 8)
            : compact || s.Navigation == NavigationStyle.Compact ? new Thickness(12, 4, 8, 4) : new Thickness(14, 7, 10, 7);
        r["PageMargin"] = compact ? new Thickness(16, 12, 8, 12) : new Thickness(24, 18, 12, 18);
        r["GridRowHeight"] = compact ? 22.0 : 28.0;
        r["ControlMinHeight"] = compact ? 24.0 : 28.0;
        r["NavWidth"] = NavWidthFor(s);

        var (radius, card) = s.Corners switch
        {
            CornerStyle.Sharp => (0.0, 0.0),
            CornerStyle.Soft => (4.0, 6.0),
            _ => (2.0, 2.0),   // the reference apps use 2 px everywhere
        };
        r["Radius"] = new CornerRadius(radius);
        r["RadiusCard"] = new CornerRadius(card);

        ReducedMotion = s.Motion switch
        {
            MotionLevel.Reduced => true,
            MotionLevel.Standard => false,
            _ => !SystemParameters.ClientAreaAnimation,
        };
        r[typeof(ProgressBar)] = r[ReducedMotion ? "ProgressBarStatic" : "ProgressBarAnimated"];
    }
}
