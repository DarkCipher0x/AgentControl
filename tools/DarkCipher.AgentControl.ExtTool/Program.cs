using System.IO.Compression;
using System.Text.Json;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Extensions;
using DarkCipher.AgentControl.Sdk;
using DarkCipher.AgentControl.Security.Packages;

// dcx-ext — DarkCipher AgentControl extension tooling.
// Every check here is the SAME code the host runs (ManifestValidator, PackageIntegrity, safe extraction).
Console.OutputEncoding = System.Text.Encoding.UTF8;
var verifier = new PackageVerificationService(null);
var appVersion = new Version(0, 1, 0);

if (args.Length == 0 || args[0] is "-h" or "--help" or "help") return Usage();

try
{
    return args[0] switch
    {
        "new" when args.Length == 3 => New(args[1], args[2]),
        "validate" when args.Length == 2 => Validate(args[1], out _),
        "analyse" or "analyze" when args.Length == 2 => Analyse(args[1]),
        "check-compat" when args.Length >= 2 => CheckCompat(args[1], args.Length >= 4 && args[2] == "--app" ? Version.Parse(args[3]) : appVersion),
        "pack" when args.Length == 3 => Pack(args[1], args[2]),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 2;
}

int Usage()
{
    Console.WriteLine("""
        dcx-ext — DarkCipher AgentControl extension tooling (SDK 1.0)

          dcx-ext new <stack-template|model-source|agent-detector|dashboard-panel> <folder>
          dcx-ext validate <folder>          manifest + integrity, using the host's validator
          dcx-ext analyse <folder>           what each requested permission allows, and its risk
          dcx-ext check-compat <folder> [--app <version>]
          dcx-ext pack <folder> <out.dcxext> builds files.sha256 in a staging copy, zips, re-verifies
        """);
    return 1;
}

ExtensionManifest? ReadManifest(string folder, List<string> errors)
{
    var path = Path.Combine(folder, SdkInfo.ManifestFileName);
    if (!File.Exists(path)) { errors.Add($"No {SdkInfo.ManifestFileName} in {folder}."); return null; }
    var (m, parse) = ManifestValidator.Parse(File.ReadAllText(path));
    errors.AddRange(parse);
    return m;
}

int Validate(string folder, out ExtensionManifest? manifest)
{
    var errors = new List<string>();
    manifest = ReadManifest(folder, errors);
    if (manifest is not null) errors.AddRange(ManifestValidator.Validate(manifest, appVersion));
    if (manifest is not null)
        foreach (var rel in manifest.Contributes.StackTemplates.Concat(manifest.Contributes.ModelCatalogs))
        {
            var f = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(f)) errors.Add($"Contributed file '{rel}' is missing.");
            else try { using var _ = JsonDocument.Parse(File.ReadAllText(f)); } catch (JsonException ex) { errors.Add($"'{rel}' is not valid JSON: {ex.Message}"); }
        }
    if (manifest is not null && File.Exists(Path.Combine(folder, SdkInfo.IntegrityFileName)))
        errors.AddRange(PackageIntegrity.Verify(folder, verifier, allowDlls: !manifest.IsDeclarative));
    foreach (var e in errors) Console.WriteLine("✖ " + e);
    if (errors.Count == 0) Console.WriteLine($"✔ {manifest!.Id} {manifest.Version} is valid ({(manifest.IsDeclarative ? "declarative" : "code")}).");
    return errors.Count == 0 ? 0 : 1;
}

int Analyse(string folder)
{
    var errors = new List<string>();
    var m = ReadManifest(folder, errors);
    if (m is null) { foreach (var e in errors) Console.WriteLine("✖ " + e); return 1; }
    Console.WriteLine($"{m.Name} ({m.Id}) requests {m.Permissions.Count} permission(s):");
    foreach (var p in m.Permissions)
    {
        var (meaning, risk) = ManifestValidator.Describe(p);
        Console.WriteLine($"  {p,-22} {meaning}  [risk: {risk}]");
    }
    foreach (var ep in m.NetworkEndpoints) Console.WriteLine($"  network endpoint: {ep}");
    Console.WriteLine(m.IsDeclarative
        ? "Isolation: data only — runs no code (enforced)."
        : "Isolation: in-process code — NOT isolated; loads only in Development Mode. Request the fewest permissions possible.");
    return 0;
}

int CheckCompat(string folder, Version app)
{
    var errors = new List<string>();
    var m = ReadManifest(folder, errors);
    if (m is null) return 1;
    var sdkOk = Version.TryParse(m.SdkVersion, out var sdk) && SdkInfo.IsCompatible(sdk);
    var appOk = Version.TryParse(m.MinAppVersion, out var min) && min <= app;
    Console.WriteLine($"SDK {m.SdkVersion} vs host SDK {SdkInfo.Version}: {(sdkOk ? "compatible" : "INCOMPATIBLE")}");
    Console.WriteLine($"minAppVersion {m.MinAppVersion} vs app {app}: {(appOk ? "ok" : "TOO NEW")}");
    if (Version.TryParse(m.MaxTestedVersion, out var max) && max < app) Console.WriteLine($"warning: only tested up to {m.MaxTestedVersion}.");
    return sdkOk && appOk ? 0 : 1;
}

int Pack(string folder, string output)
{
    // The same packager the app uses for its bundled gallery: stage, files.sha256, zip, then re-verify as the host will.
    var r = ExtensionPackager.Pack(folder, output, appVersion, verifier);
    foreach (var e in r.Errors) Console.WriteLine("✖ " + e);
    if (!r.Success) return 1;
    Console.WriteLine($"✔ Packed {r.Manifest!.Id} {r.Manifest.Version} → {output} (sha256 {r.Sha256})");
    return 0;
}

int New(string template, string folder)
{
    if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) throw new ArgumentException("The folder must be new or empty.");
    Directory.CreateDirectory(folder);
    var name = new DirectoryInfo(folder).Name.ToLowerInvariant();
    var slug = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
    if (slug.Length == 0) slug = "my-extension";
    ExtensionManifest m = template switch
    {
        "stack-template" => new() { Types = [ExtensionType.StackTemplate], Contributes = new() { StackTemplates = ["templates/stacks.json"] } },
        "model-source" => new() { Types = [ExtensionType.ModelSource], Contributes = new() { ModelCatalogs = ["catalogs/models.json"] } },
        "agent-detector" => new() { Types = [ExtensionType.Command], EntryPoint = $"{slug}.dll!Sample.Entry", Permissions = [ExtensionPermission.ProcessExecution, ExtensionPermission.UI, ExtensionPermission.Logging] },
        "dashboard-panel" => new() { Types = [ExtensionType.DashboardPanel], EntryPoint = $"{slug}.dll!Sample.Entry", Permissions = [ExtensionPermission.ModelManagement, ExtensionPermission.UI] },
        _ => throw new ArgumentException("Unknown template. Use stack-template, model-source, agent-detector or dashboard-panel."),
    };
    m = m with
    {
        Id = "com.example." + slug, Name = name, Publisher = "Your Name", Version = "0.1.0", Description = "Describe what it does.",
        MinAppVersion = "0.1.0", MaxTestedVersion = "0.1.0", SdkVersion = SdkInfo.Version.ToString(3), License = "MIT",
    };
    File.WriteAllText(Path.Combine(folder, SdkInfo.ManifestFileName), JsonSerializer.Serialize(m, new JsonSerializerOptions(ManifestValidator.Json) { WriteIndented = true }));
    if (template == "stack-template")
    {
        Directory.CreateDirectory(Path.Combine(folder, "templates"));
        File.WriteAllText(Path.Combine(folder, "templates", "stacks.json"),
            """[{ "Id": "my-stack", "Name": "My stack", "Description": "", "AgentProviderId": "codex", "AgentMode": "LocalProvider", "RuntimeId": "ollama", "Endpoint": "http://127.0.0.1:11434", "PrimaryModel": "qwen2.5-coder:7b", "UtilityModel": null, "ContextLength": 32768 }]""");
    }
    else if (template == "model-source")
    {
        Directory.CreateDirectory(Path.Combine(folder, "catalogs"));
        File.WriteAllText(Path.Combine(folder, "catalogs", "models.json"), """{ "models": [ { "ollamaName": "qwen2.5-coder:7b", "notes": "Fast coder" } ] }""");
    }
    else
    {
        File.WriteAllText(Path.Combine(folder, slug + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><AssemblyName>{slug}</AssemblyName></PropertyGroup>
              <ItemGroup>
                <!-- Reference the SDK only; it is supplied by the host at run time (Private=false). -->
                <Reference Include="DarkCipher.AgentControl.Sdk"><HintPath>PATH\TO\DarkCipher.AgentControl.Sdk.dll</HintPath><Private>false</Private></Reference>
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(folder, "Entry.cs"), """
            using DarkCipher.AgentControl.Sdk;
            namespace Sample;
            public sealed class Entry : IExtension
            {
                public Task InitializeAsync(IExtensionContext context, CancellationToken cancellationToken)
                {
                    // Register contributions through context.Broker. Every call is permission-checked and audited.
                    return Task.CompletedTask;
                }
            }
            """);
    }
    Console.WriteLine($"✔ Created {template} extension in {folder}. Edit extension.json, then: dcx-ext validate \"{folder}\"");
    return 0;
}
