using System.Reflection;
using System.Xml.Linq;

namespace DarkCipher.AgentControl.Tests.Architecture;

/// <summary>Dependency rules enforced by tests (and by the project graph): Domain depends on nothing; Application only on
/// Domain; the SDK on nothing internal; Integrations and Extensions never on Security/Infrastructure/Application
/// implementations; only the App references WPF; no project-reference cycles.</summary>
public class ArchitectureTests
{
    private const string Prefix = "DarkCipher.AgentControl.";

    private static Assembly Load(string name) => Assembly.Load(Prefix + name);

    private static string[] InternalRefs(string name) =>
        Load(name).GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Select(n => n[Prefix.Length..]).ToArray();

    [Fact] public void Domain_depends_on_nothing_internal() => Assert.Empty(InternalRefs("Domain"));
    [Fact] public void Sdk_depends_on_nothing_internal() => Assert.Empty(InternalRefs("Sdk"));
    [Fact] public void Application_depends_only_on_domain() => Assert.All(InternalRefs("Application"), r => Assert.Equal("Domain", r));
    [Fact] public void Integrations_depend_only_on_domain() => Assert.All(InternalRefs("Integrations"), r => Assert.Equal("Domain", r));
    [Fact] public void Extensions_depend_only_on_domain_and_sdk() => Assert.All(InternalRefs("Extensions"), r => Assert.Contains(r, new[] { "Domain", "Sdk" }));
    [Fact] public void Security_depends_only_on_domain() => Assert.All(InternalRefs("Security"), r => Assert.Equal("Domain", r));
    [Fact] public void Infrastructure_depends_only_on_domain_and_security() => Assert.All(InternalRefs("Infrastructure"), r => Assert.Contains(r, new[] { "Domain", "Security" }));

    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    [InlineData("Security")]
    [InlineData("Infrastructure")]
    [InlineData("Integrations")]
    [InlineData("Extensions")]
    [InlineData("Sdk")]
    public void Only_the_app_references_ui_frameworks(string name)
    {
        var refs = Load(name).GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain("PresentationFramework", refs);
        Assert.DoesNotContain("PresentationCore", refs);
        Assert.DoesNotContain("System.Windows.Forms", refs);
    }

    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    [InlineData("Integrations")]
    [InlineData("Extensions")]
    public void Layers_outside_security_never_start_processes_or_open_sockets_directly(string name)
    {
        // Inspect the type references in metadata: using a type such as HttpMethod is fine; using a client, socket or
        // process type means the layer is bypassing the central services.
        string[] forbidden = ["HttpClient", "HttpMessageHandler", "SocketsHttpHandler", "HttpClientHandler", "Socket", "TcpClient", "TcpListener",
                              "UdpClient", "WebClient", "WebRequest", "Process", "ProcessStartInfo"];
        using var fs = File.OpenRead(Load(name).Location);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var used = md.TypeReferences.Select(h => md.GetTypeReference(h)).Select(t => md.GetString(t.Namespace) + "." + md.GetString(t.Name)).ToList();
        foreach (var f in forbidden)
            Assert.DoesNotContain(used, u => u.EndsWith("." + f, StringComparison.Ordinal) && (u.StartsWith("System.Net", StringComparison.Ordinal) || u.StartsWith("System.Diagnostics", StringComparison.Ordinal)));
    }

    [Fact]
    public void Project_graph_has_no_cycles()
    {
        var repo = TempDir.RepoRoot;
        var projects = Directory.GetFiles(Path.Combine(repo, "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetFileNameWithoutExtension(p), p => XDocument.Load(p).Descendants("ProjectReference")
                .Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value)).ToList());
        var state = new Dictionary<string, int>();
        bool Visit(string n)
        {
            if (state.TryGetValue(n, out var s)) return s == 2;
            state[n] = 1;
            foreach (var d in projects.GetValueOrDefault(n) ?? []) if (!Visit(d)) return false;
            state[n] = 2;
            return true;
        }
        Assert.All(projects.Keys, p => Assert.True(Visit(p), $"cycle through {p}"));
    }

    [Fact]
    public void Credential_values_are_only_readable_inside_the_security_assembly()
    {
        var reveal = typeof(DarkCipher.AgentControl.Security.Credentials.SecureCredentialService).GetMethod("Reveal", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        Assert.True(reveal.IsAssembly, "Reveal must be internal");
    }
}

public class SdkContractTests
{
    [Fact]
    public void Sdk_version_is_semver_1x() => Assert.Equal(1, DarkCipher.AgentControl.Sdk.SdkInfo.Version.Major);

    [Fact]
    public void Sdk_exposes_only_contracts_records_and_enums()
    {
        foreach (var t in typeof(DarkCipher.AgentControl.Sdk.IExtension).Assembly.GetExportedTypes())
        {
            var ok = t.IsInterface || t.IsEnum || t.IsAbstract && t.IsSealed /* static */ || t.GetMethod("<Clone>$") is not null /* record */ || typeof(Exception).IsAssignableFrom(t);
            Assert.True(ok, $"{t.FullName} is not a contract type");
        }
    }

    [Fact]
    public void Sdk_records_carry_no_mutable_public_setters()
    {
        foreach (var t in typeof(DarkCipher.AgentControl.Sdk.IExtension).Assembly.GetExportedTypes().Where(t => t.GetMethod("<Clone>$") is not null))
            foreach (var p in t.GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
                Assert.True(p.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit"), $"{t.Name}.{p.Name} must be init-only");
    }

    [Fact]
    public void Compatibility_negotiation_rules()
    {
        Assert.True(DarkCipher.AgentControl.Sdk.SdkInfo.IsCompatible(new Version(1, 0, 0)));
        Assert.False(DarkCipher.AgentControl.Sdk.SdkInfo.IsCompatible(new Version(2, 0, 0)));
        Assert.False(DarkCipher.AgentControl.Sdk.SdkInfo.IsCompatible(new Version(1, 9, 0)));
    }

    [Fact]
    public void Broker_contract_lists_every_documented_capability()
    {
        var names = typeof(DarkCipher.AgentControl.Sdk.ISdkCapabilityBroker).GetMethods().Select(m => m.Name).ToHashSet();
        foreach (var n in new[] { "RegisterStackTemplate", "RegisterCommand", "RegisterDashboardPanel", "GetInstalledModelsAsync", "GetHardwareSummaryAsync",
                                  "RunApprovedProcessAsync", "SubmitModelDownloadAsync", "GetCredentialReference", "GetSetting", "SetSetting", "Log", "ReportHealth" })
            Assert.Contains(n, names);
    }
}
