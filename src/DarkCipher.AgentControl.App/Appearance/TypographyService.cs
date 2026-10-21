using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DarkCipher.AgentControl.Application.Appearance;

namespace DarkCipher.AgentControl.App.Appearance;

/// <summary>
/// The one place that decides fonts and text sizes — the same system as ServerManagement's Typography: Segoe UI Variable
/// Text (Segoe UI on Windows 10) for the interface, Cascadia Mono → Cascadia Code → Consolas → Courier New for code, and
/// the same 14-step type ramp. Both families ship with Windows, so nothing is redistributed and no font licence applies.
/// Views reference the resources (UiFont, ChromeFont, MonoFont, EditorFont, Fs*) with {DynamicResource …}.
/// </summary>
public sealed class TypographyService
{
    public const string DefaultUi = "Segoe UI Variable Text, Segoe UI";
    public const string DefaultMono = "Cascadia Mono, Cascadia Code, Consolas, Courier New";

    /// <summary>ServerManagement's Typography.Ramp (designed sizes at 100 %), unchanged.</summary>
    public static readonly (string Key, double Px)[] Ramp =
    [
        ("FsMicro", 10.5), ("FsCaption", 11), ("FsSmall", 11.5), ("FsMeta", 12), ("FsBody", 12.5), ("FsControl", 13), ("FsBase", 13.5),
        ("FsInput", 14), ("FsTitle", 15), ("FsHeading", 16), ("FsH1", 17), ("FsCode", 18), ("FsValue", 20), ("FsIcon", 26),
    ];

    /// <summary>A ramp step at a text scale, rounded to 0.5 px like the reference (crisp text; 100 % = the design exactly).</summary>
    public static double Px(double designed, double textScale) => Math.Round(designed * textScale * 2, MidpointRounding.AwayFromZero) / 2;

    public void Apply(AppearanceSettings s, ResourceDictionary resources)
    {
        resources["UiFont"] = new FontFamily(DefaultUi);
        resources["ChromeFont"] = new FontFamily(DefaultUi);
        resources["MonoFont"] = new FontFamily(DefaultMono);
        resources["EditorFont"] = new FontFamily(DefaultMono);
        foreach (var (key, px) in Ramp) resources[key] = Px(px, s.TextScale);
        resources["FsConsole"] = Px(s.ConsoleFontSize, s.TextScale);
        resources["FsNavIcon"] = Px(s.Navigation == NavigationStyle.IconOnly ? 17 : 14, s.TextScale);
        var zoom = Math.Abs(s.UiScale - 1) < 0.001 ? (Transform)Transform.Identity : new ScaleTransform(s.UiScale, s.UiScale);
        zoom.Freeze();
        resources["UiScaleTransform"] = zoom;
    }

    private static bool _installed;

    /// <summary>Every window gets the UI font through a resource reference when it loads (dialogs included); menus and
    /// tooltips are separate visual trees, so their font, size and zoom are re-bound as they open.</summary>
    public static void InstallClassHandlers()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((o, _) =>
        {
            if (o is not Window w) return;
            w.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
            ApplicationIconService.ApplyTo(w);
        }));
        static void Rebind(object o, string size)
        {
            if (o is not FrameworkElement fe) return;
            fe.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
            fe.SetResourceReference(TextElement.FontSizeProperty, size);
            fe.SetResourceReference(FrameworkElement.LayoutTransformProperty, "UiScaleTransform");
        }
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((o, _) => Rebind(o, "FsBase")));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((o, _) => Rebind(o, "FsBody")));
    }
}
