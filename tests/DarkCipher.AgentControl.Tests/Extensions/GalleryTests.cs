using System.Text.Json;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Extensions;
using DarkCipher.AgentControl.Sdk;
using DarkCipher.AgentControl.Security.Packages;

namespace DarkCipher.AgentControl.Tests.Extensions;

/// <summary>The bundled gallery (src/DarkCipher.AgentControl.App/Gallery) and the shared packager.</summary>
public class GalleryTests : IDisposable
{
    private static readonly Version App = new(0, 1, 9);
    private readonly TempDir _t = new();
    private readonly MemoryAudit _audit = new();
    private readonly FakeServices _services = new() { AppVersion = App };
    private readonly FixedPolicy _policy = new(new SecurityPolicy { DisableThirdPartyExtensions = true });

    public void Dispose() => _t.Dispose();

    private ExtensionHost Host() => new(new TestPaths(Path.Combine(_t.Path, "data")), new MemoryStore(), new PackageVerificationService(_audit), _policy, _audit, new MemoryLog(),
        _services, new NoExec(), [], new NoHardware(), new NoCreds());

    public static string GalleryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DarkCipherAgentControl.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "DarkCipher.AgentControl.App", "Gallery");
    }

    public static IEnumerable<object[]> Folders() => Directory.EnumerateDirectories(GalleryRoot()).Select(d => new object[] { Path.GetFileName(d) });

    [Theory]
    [MemberData(nameof(Folders))]
    public void Every_gallery_extension_packs_verifies_and_passes_the_bundled_review(string folder)
    {
        var src = Path.Combine(GalleryRoot(), folder);
        var pkg = Path.Combine(_t.Path, folder + SdkInfo.PackageExtension);
        var packed = ExtensionPackager.Pack(src, pkg, App, new PackageVerificationService(null), bundled: true);
        Assert.True(packed.Success, string.Join("; ", packed.Errors));
        Assert.True(packed.Manifest!.IsDeclarative);
        Assert.StartsWith(ExtensionHost.GalleryIdPrefix, packed.Manifest.Id, StringComparison.Ordinal);
        Assert.Empty(packed.Manifest.Permissions);
        // The dcx-ext tool path (not bundled) refuses the reserved identity.
        Assert.False(ExtensionPackager.Pack(src, Path.Combine(_t.Path, folder + "-tool" + SdkInfo.PackageExtension), App, new PackageVerificationService(null)).Success);

        var host = Host();
        var review = host.ReviewBundled(pkg);
        Assert.True(review.CanInstall, string.Join("; ", review.Errors));
        Assert.Equal(TrustLevel.Verified, review.Trust);
        host.DiscardReview(review);

        // The same package through the normal path is refused: the DarkCipher identity is reserved (anti-spoofing).
        var normal = host.Review(pkg);
        Assert.False(normal.CanInstall);
        host.DiscardReview(normal);
    }

    [Fact]
    public void Gallery_stack_templates_use_known_agents_modes_and_curated_models()
    {
        var curated = DarkCipher.AgentControl.Application.Recommendations.ModelCatalog.Curated.Select(m => m.OllamaName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(GalleryRoot(), "stacks.json", SearchOption.AllDirectories))
        {
            var templates = JsonSerializer.Deserialize<List<SdkStackTemplate>>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.InRange(templates.Count, 1, 20);
            foreach (var t in templates)
            {
                Assert.True(ids.Add(t.Id), "duplicate template id " + t.Id);
                Assert.Contains(t.AgentProviderId, new[] { "codex", "claude-code" });
                Assert.True(Enum.TryParse<Domain.Agents.AgentMode>(t.AgentMode, out _), t.AgentMode);
                Assert.Contains(t.PrimaryModel!, curated);
                if (t.UtilityModel is not null) Assert.Contains(t.UtilityModel, curated);
                Assert.Equal("http://127.0.0.1:11434", t.Endpoint);
            }
        }
        Assert.True(ids.Count >= 10);
    }

    [Fact]
    public async Task Bundled_extensions_install_as_verified_and_load_even_when_third_party_is_disabled()
    {
        var host = Host();
        var pkg = Path.Combine(_t.Path, "py" + SdkInfo.PackageExtension);
        Assert.True(ExtensionPackager.Pack(Path.Combine(GalleryRoot(), "stacks-python"), pkg, App, new PackageVerificationService(null), bundled: true).Success);
        var review = host.ReviewBundled(pkg);
        Assert.True(host.Install(review, []).Success);
        await host.StartAsync(CancellationToken.None);
        var rec = host.Extensions.Single(e => e.Id == "darkcipher.gallery.stacks-python");
        Assert.Equal((LifecycleState.Ready, TrustLevel.Verified), (rec.State, rec.Trust));
        Assert.Contains(_services.Templates, x => x.T.Id == "gallery-python-codex");
    }

    [Fact]
    public void The_bundled_path_refuses_ids_outside_the_gallery_prefix()
    {
        var host = Host();
        var foreign = Packages.Build(_t, Packages.Manifest(publisher: "Example Labs") with { MinAppVersion = "0.1.0" }, name: "foreign");
        var r = host.ReviewBundled(foreign);
        Assert.False(r.CanInstall);
        Assert.Contains(r.Errors, e => e.Contains(ExtensionHost.GalleryIdPrefix, StringComparison.Ordinal));
        host.DiscardReview(r);
    }

    [Fact]
    public void The_packager_rejects_a_manifest_that_does_not_validate()
    {
        var src = _t.Sub("bad");
        File.WriteAllText(Path.Combine(src, SdkInfo.ManifestFileName), JsonSerializer.Serialize(Packages.Manifest(id: "Bad Id"), ManifestValidator.Json));
        var r = ExtensionPackager.Pack(src, Path.Combine(_t.Path, "bad" + SdkInfo.PackageExtension), App, new PackageVerificationService(null));
        Assert.False(r.Success);
        Assert.NotEmpty(r.Errors);
    }
}
