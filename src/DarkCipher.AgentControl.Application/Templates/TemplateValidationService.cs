using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DarkCipher.AgentControl.Application.Tools;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Templates;

namespace DarkCipher.AgentControl.Application.Templates;

/// <summary>
/// Schema validation for templates. A template is data: it may choose among options the providers understand, but it can
/// never carry executables, free-form arguments, environment variables, paths, endpoints with credentials or secrets.
/// </summary>
public sealed partial class TemplateValidationService
{
    public static readonly IReadOnlySet<string> KnownTests = new HashSet<string>(["runtime-reachable", "model-installed", "basic-inference", "tool-call", "structured-output", "agent-version", "gateway"]);
    public static readonly IReadOnlySet<string> KnownServices = new HashSet<string>(["ollama"]);
    public static readonly IReadOnlySet<string> KnownRuntimes = new HashSet<string>(["ollama"]);
    public static readonly IReadOnlySet<string> KnownOs = new HashSet<string>(["windows-x64"]);

    private readonly IPermissionService _permissions;

    public TemplateValidationService(IPermissionService permissions) => _permissions = permissions;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex SemVer();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public IReadOnlyList<TemplateValidationIssue> Validate(TemplateDefinition t, IEnumerable<string>? knownAgentIds = null)
    {
        var issues = new List<TemplateValidationIssue>();
        void I(string field, string problem) => issues.Add(new(field, problem));
        var agents = knownAgentIds?.ToHashSet() ?? ["codex", "claude-code"];

        if (t.SchemaVersion != TemplateDefinition.CurrentSchemaVersion) I("schemaVersion", $"Expected schema {TemplateDefinition.CurrentSchemaVersion} (migrate older templates first).");
        if (!IdPattern().IsMatch(t.Id ?? "")) I("id", "3-64 lower-case letters, digits or '-'.");
        if (!SemVer().IsMatch(t.Version ?? "")) I("version", "Use major.minor.patch, e.g. 1.0.0.");
        if (string.IsNullOrWhiteSpace(t.DisplayName) || t.DisplayName.Length > 80) I("displayName", "1-80 characters.");
        if (t.Description.Length > 600) I("description", "At most 600 characters.");
        if (!Enum.IsDefined(t.Category)) I("category", "Unknown category.");

        if (t.Agents.Count is 0 or > 6) I("agents", "Name 1-6 agents.");
        foreach (var a in t.Agents)
        {
            if (!agents.Contains(a.AgentId) && !a.AgentId.StartsWith("custom-", StringComparison.Ordinal)) I("agents", $"Unknown agent '{a.AgentId}'.");
            if (a.Modes.Count == 0 || a.Modes.Any(m => !Enum.IsDefined(m) || m == AgentMode.Unsupported)) I("agents", $"'{a.AgentId}' needs at least one valid mode.");
            if (a.MinimumVersion is not null && Domain.Common.VersionText.Parse(a.MinimumVersion) is null) I("agents", $"'{a.AgentId}' minimum version is not a version.");
        }
        var localModes = t.Agents.SelectMany(a => a.Modes).Any(m => m is AgentMode.LocalProvider or AgentMode.VerifiedLocalGateway or AgentMode.ExperimentalLocalGateway);
        if (t.Runtime is { } r && !KnownRuntimes.Contains(r.RuntimeId)) I("runtime", $"Unknown runtime '{r.RuntimeId}'.");
        if (localModes && t.Runtime is null) I("runtime", "Local agent modes need a runtime.");
        if (t.MinimumContext is < 0 or > 1_048_576 || t.RecommendedContext is < 0 or > 1_048_576) I("context", "Context must be 0-1,048,576 tokens.");
        if (t.RecommendedContext > 0 && t.MinimumContext > t.RecommendedContext) I("context", "Minimum context is larger than the recommended context.");
        if (t.AllowedPrivacy.Count == 0) I("allowedPrivacy", "Allow at least one privacy mode.");
        if (t.DefaultModel is not null && !ModelNameRules.IsValid(t.DefaultModel)) I("defaultModel", "Not a valid model name.");
        if (t.FallbackModel is not null && !ModelNameRules.IsValid(t.FallbackModel)) I("fallbackModel", "Not a valid model name.");
        if (_permissions.Profiles.All(p => p.Id != t.PermissionProfileId)) I("permissionProfileId", $"Unknown permission profile '{t.PermissionProfileId}'.");
        foreach (var (k, v) in t.Options)
            if (!TemplateOptions.Allowed.TryGetValue(k, out var allowed) || !allowed.Contains(v)) I("options", $"Option '{k}' = '{v}' is not supported.");
        if (t.SystemInstructions is { } si)
        {
            if (si.Length > TemplateOptions.MaxSystemInstructions) I("systemInstructions", $"At most {TemplateOptions.MaxSystemInstructions} characters.");
            if (si.Any(c => c < 0x20 && c is not '\n' and not '\t' and not '\r')) I("systemInstructions", "Control characters are not allowed.");
        }
        foreach (var x in t.ServiceDependencies.Where(s => !KnownServices.Contains(s))) I("serviceDependencies", $"Unknown service '{x}'.");
        foreach (var x in t.LaunchOrder.Where(s => s != "agent" && !t.ServiceDependencies.Contains(s))) I("launchOrder", $"'{x}' is not a dependency.");
        foreach (var x in t.HealthChecks.Concat(t.ValidationTests).Where(s => !KnownTests.Contains(s))) I("validationTests", $"Unknown test '{x}'.");
        foreach (var x in t.SupportedOperatingSystems.Where(s => !KnownOs.Contains(s))) I("supportedOperatingSystems", $"Unsupported OS '{x}'.");
        if (t.KnownLimitations.Count > 20 || t.KnownLimitations.Any(l => l.Length > 300)) I("knownLimitations", "At most 20 items of 300 characters.");
        foreach (var text in AllText(t))
            if (CustomAgentRegistry.LooksLikeSecret(text)) { I("secrets", "Something in this template looks like a credential. Templates never contain secrets."); break; }
        return issues;
    }

    private static IEnumerable<string> AllText(TemplateDefinition t) =>
        new[] { t.Id, t.DisplayName, t.Description, t.SystemInstructions ?? "", t.DefaultModel ?? "", t.FallbackModel ?? "" }
            .Concat(t.Options.Values).Concat(t.KnownLimitations);

    /// <summary>Scans raw JSON for credential-like content before it is even parsed (exports and imports).</summary>
    public static bool ContainsSecret(string json) => CustomAgentRegistry.LooksLikeSecret(json);

    /// <summary>Unknown properties are refused (a template cannot smuggle fields such as executables or environment).</summary>
    public static TemplateDefinition Deserialize(JsonNode node) =>
        node.Deserialize<TemplateDefinition>(Json) ?? throw new JsonException("Empty template.");
}
