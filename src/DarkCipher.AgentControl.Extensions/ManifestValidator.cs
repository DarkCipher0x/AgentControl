using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DarkCipher.AgentControl.Sdk;

namespace DarkCipher.AgentControl.Extensions;

/// <summary>Validates extension manifests. Shared by the host, the package builder and the developer tooling so that
/// every component applies exactly the same rules.</summary>
public static partial class ManifestValidator
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        MaxDepth = 16,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>Publisher names reserved for the application's own built-in extensions (anti-spoofing).</summary>
    public static readonly string[] ReservedPublishers = ["DarkCipher", "DarkCipher Systems", "darkcipher0x", "DarkCipherSystems"];
    public static readonly string[] ReservedIdPrefixes = ["darkcipher.", "com.darkciphersystems."];

    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9][a-z0-9\-]*){1,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(0|[1-9]\d{0,3})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,5})$", RegexOptions.CultureInvariant)]
    private static partial Regex SemVerPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,100}\.dll![A-Za-z_][A-Za-z0-9_.]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex EntryPointPattern();

    public static (ExtensionManifest? Manifest, List<string> Errors) Parse(string json)
    {
        var errors = new List<string>();
        if (json.Length > 64 * 1024) { errors.Add("Manifest is larger than 64 KB."); return (null, errors); }
        try
        {
            var m = JsonSerializer.Deserialize<ExtensionManifest>(json, Json);
            if (m is null) errors.Add("Manifest is empty.");
            return (m, errors);
        }
        catch (JsonException ex)
        {
            errors.Add("Manifest is not valid JSON: " + ex.Message.Split('\n')[0]);
            return (null, errors);
        }
    }

    public static List<string> Validate(ExtensionManifest m, Version appVersion, bool builtIn = false)
    {
        var e = new List<string>();
        if (!IdPattern().IsMatch(m.Id)) e.Add("id must be reverse-DNS style, lower case (e.g. com.example.my-extension).");
        if (!builtIn && ReservedIdPrefixes.Any(p => m.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase))) e.Add("id uses a prefix reserved for built-in extensions.");
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 80) e.Add("name must be 1–80 characters.");
        if (string.IsNullOrWhiteSpace(m.Publisher) || m.Publisher.Length > 80) e.Add("publisher must be 1–80 characters.");
        if (!builtIn && ReservedPublishers.Any(p => string.Equals(p, m.Publisher.Trim(), StringComparison.OrdinalIgnoreCase))) e.Add("publisher name is reserved (spoofing protection).");
        if (m.Description.Length > 1000) e.Add("description is longer than 1000 characters.");
        if (!SemVerPattern().IsMatch(m.Version)) e.Add("version must be MAJOR.MINOR.PATCH.");
        if (!SemVerPattern().IsMatch(m.SdkVersion)) e.Add("sdkVersion must be MAJOR.MINOR.PATCH.");
        else if (!SdkInfo.IsCompatible(System.Version.Parse(m.SdkVersion))) e.Add($"sdkVersion {m.SdkVersion} is not compatible with host SDK {SdkInfo.Version}.");
        if (!SemVerPattern().IsMatch(m.MinAppVersion)) e.Add("minAppVersion must be MAJOR.MINOR.PATCH.");
        else if (System.Version.Parse(m.MinAppVersion) > appVersion) e.Add($"Requires app version {m.MinAppVersion} or newer.");
        if (m.MaxTestedVersion.Length > 0 && !SemVerPattern().IsMatch(m.MaxTestedVersion)) e.Add("maxTestedVersion must be MAJOR.MINOR.PATCH.");
        if (m.Types.Count == 0) e.Add("types must list at least one extension type.");
        if (m.Types.Distinct().Count() != m.Types.Count) e.Add("types contains duplicates.");
        if (m.Permissions.Distinct().Count() != m.Permissions.Count) e.Add("permissions contains duplicates.");
        if (m.EntryPoint is { Length: > 0 } ep && !EntryPointPattern().IsMatch(ep)) e.Add("entryPoint must look like 'Assembly.dll!Namespace.Type'.");
        if (string.IsNullOrWhiteSpace(m.License)) e.Add("license is required (SPDX id or 'Proprietary').");
        if (m.Source is { Length: > 0 } src && (!Uri.TryCreate(src, UriKind.Absolute, out var su) || su.Scheme != Uri.UriSchemeHttps)) e.Add("source must be an https URL.");

        foreach (var ep2 in m.NetworkEndpoints)
            if (!Uri.TryCreate(ep2, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)))
                e.Add($"networkEndpoints entry '{ep2}' must be https (or http on loopback).");
        if (m.NetworkEndpoints.Count > 0 && !m.Permissions.Contains(ExtensionPermission.Network)) e.Add("networkEndpoints require the Network permission.");
        if (m.Permissions.Contains(ExtensionPermission.Network) && m.NetworkEndpoints.Count == 0) e.Add("The Network permission requires declaring networkEndpoints.");

        foreach (var d in m.Dependencies)
        {
            if (!IdPattern().IsMatch(d.Id)) e.Add($"dependency id '{d.Id}' is invalid.");
            if (!SemVerPattern().IsMatch(d.MinVersion)) e.Add($"dependency '{d.Id}' minVersion is invalid.");
            if (d.Id == m.Id) e.Add("An extension cannot depend on itself.");
        }

        foreach (var path in m.Contributes.StackTemplates.Concat(m.Contributes.ModelCatalogs))
            if (!IsSafeRelativePath(path)) e.Add($"contributes path '{path}' must be a relative path inside the package.");
        if (m.Contributes.StackTemplates.Count > 0 && !m.Types.Contains(ExtensionType.StackTemplate)) e.Add("stackTemplates contributions require the StackTemplate type.");
        if (m.Contributes.ModelCatalogs.Count > 0 && !m.Types.Contains(ExtensionType.ModelSource)) e.Add("modelCatalogs contributions require the ModelSource type.");
        if (m.IsDeclarative && m.Contributes.StackTemplates.Count == 0 && m.Contributes.ModelCatalogs.Count == 0)
            e.Add("A declarative extension (no entryPoint) must contribute at least one file.");
        return e;
    }

    public static bool IsSafeRelativePath(string p) =>
        !string.IsNullOrWhiteSpace(p) && p.Length < 200 && !Path.IsPathRooted(p) && !p.Contains(':') && !p.Contains('\\')
        && p.Split('/').All(s => s.Length > 0 && s != "." && s != "..") && p.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Plain-language meaning and risk for each permission (review screen and permission analyser).</summary>
    public static (string Meaning, string Risk) Describe(ExtensionPermission p) => p switch
    {
        ExtensionPermission.Network => ("Contact the network endpoints listed in the manifest", "Medium: data could leave this PC to those endpoints"),
        ExtensionPermission.FileReads => ("Read files the host hands to it", "Medium"),
        ExtensionPermission.ProjectReads => ("Read files in projects you select", "High: source code access"),
        ExtensionPermission.ProjectWrites => ("Change files in projects you select", "High"),
        ExtensionPermission.ProcessExecution => ("Ask the host to start programs (validated and audited)", "High"),
        ExtensionPermission.ModelManagement => ("List models and queue model downloads", "Low"),
        ExtensionPermission.RuntimeManagement => ("Manage local runtimes", "Medium"),
        ExtensionPermission.CredentialReferences => ("Receive references (not values) to credentials you choose", "Medium"),
        ExtensionPermission.UI => ("Add commands and dashboard panels", "Low"),
        ExtensionPermission.Settings => ("Store its own settings", "Low"),
        ExtensionPermission.Logging => ("Write to the application log", "Low"),
        ExtensionPermission.Hardware => ("Read a hardware summary", "Low"),
        ExtensionPermission.Sessions => ("See agent session status", "Medium"),
        _ => ("Unknown", "Unknown"),
    };
}
