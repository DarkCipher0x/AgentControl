using System.Windows;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.Application.Appearance;
using Microsoft.Win32;

namespace DarkCipher.AgentControl.App.Appearance;

/// <summary>Applies appearance settings to the application resources at start-up and live on every change, and follows
/// Windows theme / high-contrast / animation changes. The services stay focused; this only sequences them.</summary>
public sealed class AppearanceCoordinator
{
    private readonly AppearanceSettingsService _settings;
    private bool _watching;

    public AppearanceCoordinator(AppearanceSettingsService settings, ThemeService theme, TypographyService typography, LayoutPreferenceService layout)
    {
        _settings = settings;
        Theme = theme;
        Typography = typography;
        Layout = layout;
        settings.Changed += (_, _) => Ui.OnUi(Apply);
    }

    public ThemeService Theme { get; }
    public TypographyService Typography { get; }
    public LayoutPreferenceService Layout { get; }

    public void Apply()
    {
        var res = System.Windows.Application.Current?.Resources;
        if (res is null) return;
        var s = _settings.Current;
        Theme.Apply(s, res);
        Typography.Apply(s, res);
        Layout.Apply(s, res);
    }

    /// <summary>Start-up: install class handlers, apply, and follow Windows changes (interactive runs only).</summary>
    public void Install(bool followSystem)
    {
        TypographyService.InstallClassHandlers();
        Apply();
        if (!followSystem || _watching) return;
        _watching = true;
        SystemEvents.UserPreferenceChanged += OnSystemChanged;
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation)) Ui.OnUi(Apply);
        };
    }

    private void OnSystemChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility) Ui.OnUi(Apply);
    }
}
