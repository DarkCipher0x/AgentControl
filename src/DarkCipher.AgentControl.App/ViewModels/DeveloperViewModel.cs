using System.IO;
using System.Text;
using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Extensions;
using DarkCipher.AgentControl.Sdk;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed class DeveloperViewModel : PageViewModel
{
    private readonly ISecurityPolicyService _policy;
    private readonly IPackageVerifier _verifier;
    private string _validation = "Choose an extension folder (containing extension.json) to validate it with the same rules the host uses.";

    public DeveloperViewModel(ISecurityPolicyService policy, IPackageVerifier verifier)
    {
        _policy = policy;
        _verifier = verifier;
        policy.Changed += (_, _) => Ui.OnUi(() => Raise(nameof(DevelopmentMode)));
        ToggleDevModeCommand = new RelayCommand(ToggleDevMode);
        ValidateCommand = new RelayCommand(Validate);
    }

    public override string Title => "Developer / SDK";
    public override string Subtitle => "Build extensions against the public, versioned SDK. Development Mode is off by default.";

    public string SdkVersion => SdkInfo.Version.ToString(3);
    public bool DevelopmentMode => _policy.Current.DevelopmentMode;
    public string Validation { get => _validation; private set => Set(ref _validation, value); }

    public IReadOnlyList<string> Tooling { get; } =
    [
        "dcx-ext new <stack-template|model-source|agent-provider|dashboard-panel> <folder> — create an extension from a template",
        "dcx-ext validate <folder> — manifest schema + rules (same validator as the host)",
        "dcx-ext analyse <folder> — permission analyser: what each requested permission allows and its risk",
        "dcx-ext check-compat <folder> --app <version> — SDK/app compatibility check",
        "dcx-ext pack <folder> <out.dcxext> — builds files.sha256 and the package",
        "Mock host + contract tests: tests/DarkCipher.AgentControl.Tests (SdkContractTests, ExtensionHostTests)",
        "Docs: docs/sdk (overview, architecture, security model, manifest, permissions, lifecycle, versioning, tutorials, packaging, signing, debugging, publishing)",
        "Samples: samples/ (stack template, model source, agent provider, dashboard panel)",
    ];

    public ICommand ToggleDevModeCommand { get; }
    public ICommand ValidateCommand { get; }

    private void ToggleDevMode()
    {
        if (!DevelopmentMode)
        {
            if (!Dialogs.Confirm("Turn on Development Mode?", "Development Mode lets unsigned CODE extensions load inside this app.",
                    ["Code extensions are NOT isolated: they run with the same rights as this app and could bypass its permission checks.",
                     "Only enable this on a development PC, for extensions you wrote or fully trust.",
                     "A warning badge stays visible while it is on.", "The change is recorded in the security audit log."], "Turn on", danger: true))
                return;
        }
        var r = _policy.Update(p => p with { DevelopmentMode = !p.DevelopmentMode }, "Development Mode toggled from Developer / SDK");
        Message = r.Message + " Restart the app to load or unload code extensions.";
    }

    private void Validate()
    {
        if (Dialogs.Headless) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Extension folder" };
        if (dlg.ShowDialog() != true) return;
        var manifestPath = Path.Combine(dlg.FolderName, SdkInfo.ManifestFileName);
        var sb = new StringBuilder();
        if (!File.Exists(manifestPath)) { Validation = "No extension.json in that folder."; return; }
        var (m, errors) = ManifestValidator.Parse(File.ReadAllText(manifestPath));
        if (m is not null) errors.AddRange(ManifestValidator.Validate(m, Composition.Bootstrapper.AppVersion));
        if (m is not null && File.Exists(Path.Combine(dlg.FolderName, SdkInfo.IntegrityFileName)))
            errors.AddRange(PackageIntegrity.Verify(dlg.FolderName, _verifier, allowDlls: !m.IsDeclarative));
        else sb.AppendLine("(No files.sha256 yet — 'dcx-ext pack' creates it.)");
        sb.Insert(0, errors.Count == 0 ? "✔ Valid.\n" : string.Join("\n", errors.Select(e => "✖ " + e)) + "\n");
        if (m is not null) foreach (var p in m.Permissions) { var (meaning, risk) = ManifestValidator.Describe(p); sb.AppendLine($"Permission {p}: {meaning} — risk {risk}"); }
        Validation = sb.ToString();
    }
}
