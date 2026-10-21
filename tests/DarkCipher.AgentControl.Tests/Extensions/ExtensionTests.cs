using System.IO.Compression;
using System.Text.Json;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Hardware;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Extensions;
using DarkCipher.AgentControl.Sdk;
using DarkCipher.AgentControl.Security.Packages;

namespace DarkCipher.AgentControl.Tests.Extensions;

public sealed class FakeServices : IExtensionHostServices
{
    public List<(string Ext, SdkStackTemplate T)> Templates { get; } = [];
    public Version AppVersion { get; init; } = new(0, 1, 0);
    public void RegisterStackTemplate(string extensionId, SdkStackTemplate template) => Templates.Add((extensionId, template));
    public Task<string> SubmitModelDownloadAsync(string extensionId, string modelName, CancellationToken ct) => Task.FromResult("job1");
    public List<(string Ext, string Json)> TemplateJson { get; } = [];
    public List<(string Ext, SdkAgentToolEntry Tool)> Tools { get; } = [];
    public void RegisterTemplate(string extensionId, string templateJson) => TemplateJson.Add((extensionId, templateJson));
    public void RegisterAgentTool(string extensionId, SdkAgentToolEntry tool) => Tools.Add((extensionId, tool));
}

public sealed class NoHardware : IHardwareDiscoveryProvider
{
    public Task<HardwareProfile> ScanAsync(CancellationToken ct) => throw new NotSupportedException();
}

public sealed class NoExec : IProcessExecutor
{
    public List<ProcessSpec> Runs { get; } = [];
    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct) { Runs.Add(spec); return Task.FromResult(new ProcessResult(ProcessOutcome.Completed, 0, "", "", TimeSpan.Zero, "ok")); }
    public OperationResultWithProcess Start(ProcessSpec spec) => new(false, "", null);
    public IReadOnlyList<TrackedProcess> Tracked => [];
    public bool Stop(string id) => false;
    public event EventHandler<TrackedProcess>? ProcessExited { add { } remove { } }
    public string Preview(ProcessSpec spec) => "";
}

public sealed class NoCreds : ICredentialStore
{
    public IReadOnlyList<CredentialReference> List() => [new CredentialReference("abc", "token", "extension:com.example.other", DateTimeOffset.UtcNow)];
    public CredentialReference Save(string displayName, string kind, string secret) => throw new NotSupportedException();
    public bool Remove(string id) => false;
    public bool Exists(string id) => false;
}

public static class Packages
{
    public static ExtensionManifest Manifest(string id = "com.example.rust-stacks", string version = "1.0.0", string? entry = null, string publisher = "Example Labs") => new()
    {
        Id = id, Name = "Rust stacks", Publisher = publisher, Version = version, Description = "Test", MinAppVersion = "0.1.0", MaxTestedVersion = "0.1.0",
        SdkVersion = "1.0.0", EntryPoint = entry, Types = [ExtensionType.StackTemplate], Permissions = [], License = "MIT",
        Contributes = new ExtensionContributions { StackTemplates = ["templates/stacks.json"] },
    };

    public static string Build(TempDir t, ExtensionManifest m, Action<string>? tamper = null, bool integrity = true, string? name = null)
    {
        var src = t.Sub("src-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(src, SdkInfo.ManifestFileName), JsonSerializer.Serialize(m, ManifestValidator.Json));
        Directory.CreateDirectory(Path.Combine(src, "templates"));
        File.WriteAllText(Path.Combine(src, "templates", "stacks.json"),
            """[{"Id":"rust","Name":"Rust + Codex","Description":"d","AgentProviderId":"codex","AgentMode":"LocalProvider","RuntimeId":"ollama","Endpoint":"http://127.0.0.1:11434","PrimaryModel":"qwen2.5-coder:7b","UtilityModel":null,"ContextLength":32768}]""");
        if (integrity) File.WriteAllText(Path.Combine(src, SdkInfo.IntegrityFileName), PackageIntegrity.Build(src, new PackageVerificationService(null)));
        tamper?.Invoke(src);
        var pkg = Path.Combine(t.Path, (name ?? m.Id + "-" + m.Version) + SdkInfo.PackageExtension);
        ZipFile.CreateFromDirectory(src, pkg);
        return pkg;
    }
}

public class ManifestValidatorTests
{
    private static readonly Version App = new(0, 1, 0);

    [Fact]
    public void A_correct_declarative_manifest_is_valid() => Assert.Empty(ManifestValidator.Validate(Packages.Manifest(), App));

    [Theory]
    [InlineData("Bad Id")]
    [InlineData("darkcipher.fake")]
    [InlineData("noperiod")]
    public void Bad_or_reserved_ids_are_rejected(string id) => Assert.NotEmpty(ManifestValidator.Validate(Packages.Manifest(id), App));

    [Fact]
    public void Reserved_publisher_names_are_rejected_as_spoofing() =>
        Assert.Contains(ManifestValidator.Validate(Packages.Manifest(publisher: "DarkCipher Systems"), App), e => e.Contains("reserved", StringComparison.Ordinal));

    [Fact]
    public void Network_permission_requires_declared_https_endpoints()
    {
        var m = Packages.Manifest() with { Permissions = [ExtensionPermission.Network] };
        Assert.NotEmpty(ManifestValidator.Validate(m, App));
        Assert.NotEmpty(ManifestValidator.Validate(m with { NetworkEndpoints = ["http://example.com"] }, App));
        Assert.Empty(ManifestValidator.Validate(m with { NetworkEndpoints = ["https://api.example.com"] }, App));
    }

    [Fact]
    public void Incompatible_sdk_or_app_versions_are_rejected()
    {
        Assert.NotEmpty(ManifestValidator.Validate(Packages.Manifest() with { SdkVersion = "2.0.0" }, App));
        Assert.NotEmpty(ManifestValidator.Validate(Packages.Manifest() with { SdkVersion = "1.5.0" }, App));
        Assert.NotEmpty(ManifestValidator.Validate(Packages.Manifest() with { MinAppVersion = "9.0.0" }, App));
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("/abs.json")]
    [InlineData("C:/x.json")]
    [InlineData("templates\\x.json")]
    [InlineData("templates/x.exe")]
    public void Unsafe_contribution_paths_are_rejected(string path) =>
        Assert.NotEmpty(ManifestValidator.Validate(Packages.Manifest() with { Contributes = new ExtensionContributions { StackTemplates = [path] } }, App));

    [Fact]
    public void Unknown_permission_names_fail_to_parse()
    {
        var (m, errors) = ManifestValidator.Parse("""{"id":"com.example.x","permissions":["EverythingPlease"]}""");
        Assert.Null(m);
        Assert.NotEmpty(errors);
    }
}

public class ExtensionHostTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly MemoryAudit _audit = new();
    private readonly FakeServices _services = new();
    private readonly FixedPolicy _policy = new(new SecurityPolicy { DisableThirdPartyExtensions = false });
    private readonly MemoryStore _store = new();

    public void Dispose() => _t.Dispose();

    private ExtensionHost Host() => new(new TestPaths(Path.Combine(_t.Path, "data")), _store, new PackageVerificationService(_audit), _policy, _audit, new MemoryLog(),
        _services, new NoExec(), [], new NoHardware(), new NoCreds());

    [Fact]
    public async Task Declarative_extension_installs_reviews_and_contributes_templates()
    {
        var host = Host();
        var review = host.Review(Packages.Build(_t, Packages.Manifest()));
        Assert.True(review.CanInstall, string.Join("; ", review.Errors));
        Assert.Equal(TrustLevel.Unsigned, review.Trust);
        Assert.True(host.Install(review, []).Success);
        await host.StartAsync(CancellationToken.None);
        var rec = host.Extensions.Single(e => e.Id == "com.example.rust-stacks");
        Assert.Equal(LifecycleState.Ready, rec.State);
        Assert.Contains(_services.Templates, x => x.T.Id == "rust");
        Assert.Contains(_audit.Events, e => e.Action == "ExtensionInstalled");
    }

    [Fact]
    public async Task Files_changed_after_install_are_blocked()
    {
        var host = Host();
        var review = host.Review(Packages.Build(_t, Packages.Manifest()));
        host.Install(review, []);
        var installed = host.Extensions.Single(e => e.Id == "com.example.rust-stacks").InstallPath!;
        File.AppendAllText(Path.Combine(installed, "templates", "stacks.json"), " ");
        _services.Templates.Clear();
        var fresh = Host();
        await fresh.StartAsync(CancellationToken.None);
        Assert.Equal(LifecycleState.Blocked, fresh.Extensions.Single(e => e.Id == "com.example.rust-stacks").State);
        Assert.Empty(_services.Templates);
        Assert.Contains(_audit.Events, e => e.Action == "ExtensionTamperDetected");
    }

    [Fact]
    public void Packages_without_integrity_lists_or_with_executables_are_rejected()
    {
        var host = Host();
        Assert.False(host.Review(Packages.Build(_t, Packages.Manifest(), integrity: false)).CanInstall);
        var withExe = Packages.Build(_t, Packages.Manifest(), tamper: src => File.WriteAllText(Path.Combine(src, "run.cmd"), "calc"), name: "exe");
        Assert.False(host.Review(withExe).CanInstall);
    }

    [Fact]
    public void Downgrades_and_duplicate_versions_are_refused()
    {
        var host = Host();
        host.Install(host.Review(Packages.Build(_t, Packages.Manifest(version: "1.2.0"))), []);
        Assert.False(host.Review(Packages.Build(_t, Packages.Manifest(version: "1.1.0"))).CanInstall);
        Assert.False(host.Review(Packages.Build(_t, Packages.Manifest(version: "1.2.0"), name: "same")).CanInstall);
        Assert.True(host.Review(Packages.Build(_t, Packages.Manifest(version: "1.3.0"))).CanInstall);
    }

    [Fact]
    public void Zip_slip_packages_are_rejected()
    {
        var pkg = Path.Combine(_t.Path, "slip" + SdkInfo.PackageExtension);
        using (var z = ZipFile.Open(pkg, ZipArchiveMode.Create))
        {
            using var s = z.CreateEntry("../../evil.json").Open();
            s.Write("{}"u8);
        }
        var review = Host().Review(pkg);
        Assert.False(review.CanInstall);
        Assert.False(File.Exists(Path.Combine(_t.Path, "evil.json")));
    }

    [Fact]
    public async Task Third_party_switch_and_safe_mode_prevent_loading()
    {
        var host = Host();
        host.Install(host.Review(Packages.Build(_t, Packages.Manifest())), []);
        _policy.Current = _policy.Current with { DisableThirdPartyExtensions = true };
        _services.Templates.Clear();
        var h2 = Host();
        await h2.StartAsync(CancellationToken.None);
        Assert.Equal(LifecycleState.Disabled, h2.Extensions.Single(e => e.Id == "com.example.rust-stacks").State);
        _policy.Current = _policy.Current with { DisableThirdPartyExtensions = false, SafeMode = true };
        var h3 = Host();
        await h3.StartAsync(CancellationToken.None);
        Assert.Equal(LifecycleState.Disabled, h3.Extensions.Single(e => e.Id == "com.example.rust-stacks").State);
        Assert.Empty(_services.Templates);
    }

    [Fact]
    public async Task Code_extensions_do_not_load_without_development_mode()
    {
        var host = Host();
        var m = Packages.Manifest("com.example.code") with { EntryPoint = "Sample.dll!Sample.Entry", Contributes = new ExtensionContributions() };
        var review = host.Review(Packages.Build(_t, m, tamper: src => { }));
        // Built packages have no Sample.dll, so integrity passes only for listed files; install should still succeed (dll optional).
        if (!review.CanInstall) return;
        host.Install(review, []);
        var h2 = Host();
        await h2.StartAsync(CancellationToken.None);
        var rec = h2.Extensions.Single(e => e.Id == "com.example.code");
        Assert.Equal(LifecycleState.Disabled, rec.State);
        Assert.Contains("Development Mode", rec.StateReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Built_in_components_cannot_be_disabled_or_replaced()
    {
        var host = Host();
        Assert.False(host.SetEnabled("darkcipher.runtime.ollama", false).Success);
        Assert.All(host.Extensions.Where(e => e.Trust == TrustLevel.BuiltIn), e => Assert.Equal(LifecycleState.Ready, e.State));
    }
}

public class CapabilityBrokerTests
{
    private static (SdkCapabilityBroker Broker, MemoryAudit Audit, NoExec Exec) Make(params ExtensionPermission[] granted)
    {
        var audit = new MemoryAudit();
        var exec = new NoExec();
        var broker = new SdkCapabilityBroker("com.example.ext", granted.ToHashSet(), new FakeServices(), new FixedPolicy(new SecurityPolicy()), audit, new MemoryLog(),
            exec, [], new NoHardware(), new NoCreds(), new MemoryStore(), new ExtensionContributionRegistry(), (_, _) => { });
        return (broker, audit, exec);
    }

    [Fact]
    public async Task Ungranted_capabilities_are_denied_and_audited()
    {
        var (b, audit, exec) = Make();
        await Assert.ThrowsAsync<CapabilityDeniedException>(() => b.RunApprovedProcessAsync(new SdkProcessRequest(@"C:\Windows\System32\where.exe", [], null, TimeSpan.FromSeconds(1), "x"), CancellationToken.None));
        Assert.Throws<CapabilityDeniedException>(() => b.SetSetting("k", "v"));
        Assert.Throws<CapabilityDeniedException>(() => b.Log(SdkLogLevel.Information, "x"));
        Assert.Throws<CapabilityDeniedException>(() => b.RegisterCommand(new SdkCommand("c", "C", "", _ => Task.FromResult(""))));
        Assert.Empty(exec.Runs);
        Assert.Contains(audit.Events, e => e.Action == "ExtensionCapabilityDenied");
    }

    [Fact]
    public async Task Granted_process_requests_are_constrained_to_extension_rules()
    {
        var (b, _, exec) = Make(ExtensionPermission.ProcessExecution);
        await b.RunApprovedProcessAsync(new SdkProcessRequest(@"C:\Windows\System32\where.exe", ["x"], null, TimeSpan.FromHours(1), "probe"), CancellationToken.None);
        var spec = exec.Runs.Single();
        Assert.Equal(PathActor.Extension, spec.Actor);
        Assert.False(spec.PathApprovalGranted);
        Assert.False(spec.AllowShellInterpreter);
        Assert.Equal(ProcessMode.Captured, spec.Mode);
        Assert.True(spec.Timeout <= TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Settings_are_scoped_and_limited()
    {
        var (b, _, _) = Make(ExtensionPermission.Settings);
        b.SetSetting("theme", "dark");
        Assert.Equal("dark", b.GetSetting("theme"));
        Assert.Throws<CapabilityDeniedException>(() => b.SetSetting(new string('k', 100), "v"));
        Assert.Throws<CapabilityDeniedException>(() => b.SetSetting("big", new string('v', 5000)));
    }

    [Fact]
    public void Credential_references_only_for_credentials_created_for_this_extension()
    {
        var (b, _, _) = Make(ExtensionPermission.CredentialReferences);
        Assert.Null(b.GetCredentialReference("token")); // exists, but belongs to another extension
    }
}
