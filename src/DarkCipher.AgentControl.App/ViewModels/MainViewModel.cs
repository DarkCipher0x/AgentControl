using System.Windows.Input;
using DarkCipher.AgentControl.App.Composition;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Appearance;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ISecurityPolicyService _policy;
    private readonly AppearanceSettingsService _appearance;

    public MainViewModel(Navigator nav, ISecurityPolicyService policy, IDocumentStore store, IAppLogger log, AppearanceSettingsService appearance)
    {
        Nav = nav;
        _policy = policy;
        _appearance = appearance;
        policy.Changed += (_, _) => Ui.OnUi(RaiseBadges);
        appearance.Changed += (_, _) => Ui.OnUi(() => Raise(nameof(ShowNavTitles)));
        nav.Recent.CollectionChanged += (_, _) => Raise(nameof(HasRecent));
        foreach (var notice in store.RecoveryNotices.Concat(appearance.LoadNotes)) log.Warn("Config", notice);
        var notices = store.RecoveryNotices.Concat(appearance.LoadNotes.Select(n => "Appearance: " + n)).ToList();
        RecoveryNotice = notices.Count == 0 ? null : string.Join(" ", notices);
        GoCommand = new RelayCommand(p => { if (p is string key) Nav.Go(key); });
        nav.Go(appearance.Current.LandingPage);
    }

    public Navigator Nav { get; }
    public string Version => "v" + Bootstrapper.AppVersion.ToString(3);
    public string? RecoveryNotice { get; }
    public ICommand GoCommand { get; }

    /// <summary>False in icon-only navigation (titles become tooltips and accessible names).</summary>
    public bool ShowNavTitles => _appearance.Current.Navigation != NavigationStyle.IconOnly;
    public bool HasRecent => Nav.Recent.Count > 0;

    public string PrivacyBadge => _policy.Current.Privacy switch
    {
        PrivacyMode.FullyLocal => "FULLY LOCAL",
        PrivacyMode.LocalPreferred => "LOCAL PREFERRED",
        PrivacyMode.Hybrid => "HYBRID",
        _ => "CLOUD ALLOWED",
    };

    public string PrivacyTooltip => _policy.Current.Privacy switch
    {
        PrivacyMode.FullyLocal => "Fully Local: prompts and code never leave this PC (or approved local-network endpoints). No cloud fallback.",
        PrivacyMode.LocalPreferred => "Local Preferred: local inference; cloud inference blocked.",
        PrivacyMode.Hybrid => "Hybrid: cloud agents allowed when you choose a cloud stack.",
        _ => "Cloud Allowed: cloud agents and endpoints may be used.",
    };

    public bool SafeMode => _policy.Current.SafeMode;
    public bool DevelopmentMode => _policy.Current.DevelopmentMode;

    private void RaiseBadges()
    {
        Raise(nameof(PrivacyBadge));
        Raise(nameof(PrivacyTooltip));
        Raise(nameof(SafeMode));
        Raise(nameof(DevelopmentMode));
    }
}
