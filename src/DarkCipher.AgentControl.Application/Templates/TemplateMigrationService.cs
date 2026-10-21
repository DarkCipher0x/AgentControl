using System.Text.Json;
using System.Text.Json.Nodes;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Templates;

namespace DarkCipher.AgentControl.Application.Templates;

public sealed record MigrationResult(TemplateDefinition? Template, IReadOnlyList<string> Notes, string? Error);

/// <summary>
/// Moves older template formats to schema 2. Schema 1 is the 0.1.x stack-template shape (SDK 1.0 SdkStackTemplate / an
/// exported StackProfile: id, name, description, agentProviderId, agentMode, runtimeId, endpoint, primaryModel,
/// contextLength). Newer schemas are refused rather than guessed. Migration never adds permissions: the result always uses
/// the safe "Ask Before Changes" profile.
/// </summary>
public static class TemplateMigrationService
{
    public static MigrationResult Migrate(JsonNode? node)
    {
        if (node is not JsonObject o) return new(null, [], "Not a JSON object.");
        var schema = Int(o, "schemaVersion") ?? (o.ContainsKey("agentProviderId") ? 1 : TemplateDefinition.CurrentSchemaVersion);
        if (schema > TemplateDefinition.CurrentSchemaVersion) return new(null, [], $"This template was made by a newer version (schema {schema}).");
        if (schema == TemplateDefinition.CurrentSchemaVersion)
        {
            try { return new(TemplateValidationService.Deserialize(o), [], null); }
            catch (JsonException ex) { return new(null, [], "Not a valid template: " + ex.Message.Split('\n')[0]); }
        }
        if (schema != 1) return new(null, [], $"Unknown schema {schema}.");

        var notes = new List<string> { "Migrated from the 0.1.x stack-template format (schema 1) to schema 2." };
        var agent = Str(o, "agentProviderId") ?? "";
        var modeText = Str(o, "agentMode") ?? nameof(AgentMode.LocalProvider);
        if (!Enum.TryParse<AgentMode>(modeText, ignoreCase: true, out var mode)) return new(null, notes, $"Unknown agent mode '{modeText}'.");
        var ctx = Int(o, "contextLength") ?? 0;
        var local = mode is AgentMode.LocalProvider or AgentMode.VerifiedLocalGateway or AgentMode.ExperimentalLocalGateway;
        var id = Slug(Str(o, "id") ?? Str(o, "name") ?? "imported-template");
        if (Str(o, "permissionProfileId") is { } p && p != BuiltInPermissionProfiles.DefaultId) notes.Add($"Permission profile '{p}' replaced by 'Ask Before Changes' (migration never grants more).");
        if (Str(o, "endpoint") is { } ep && Uri.TryCreate(ep, UriKind.Absolute, out var u) && !u.IsLoopback) notes.Add("The remote endpoint was dropped; templates only target this PC's runtime.");
        var t = new TemplateDefinition
        {
            Id = id,
            Version = "1.0.0",
            DisplayName = Trim(Str(o, "name") ?? id, 80),
            Description = Trim(Str(o, "description") ?? "", 600),
            Category = local ? TemplateCategory.GeneralCoding : TemplateCategory.HybridLocalAndCloud,
            Origin = TemplateOrigin.Imported,
            Agents = [new AgentRequirement(agent, [mode])],
            Runtime = local ? new RuntimeRequirement(Str(o, "runtimeId") ?? "ollama") : null,
            Protocols = [],
            RequiredCapabilities = local ? ModelCapabilities.Completion | ModelCapabilities.Tools : ModelCapabilities.None,
            MinimumContext = ctx > 0 ? Math.Min(ctx, 32768) : 0,
            RecommendedContext = ctx,
            RequiresToolCalling = local,
            DefaultModel = Str(o, "primaryModel"),
            FallbackModel = Str(o, "fallbackModel"),
            PermissionProfileId = BuiltInPermissionProfiles.DefaultId,
            ServiceDependencies = local ? ["ollama"] : [],
            LaunchOrder = local ? ["ollama", "agent"] : [],
            ValidationTests = local ? ["runtime-reachable", "model-installed", "basic-inference", "tool-call", "agent-version"] : ["agent-version"],
        };
        if (ctx > 32768) notes.Add($"Minimum context set to 32,768 (recommended {ctx:N0}).");
        return new(t, notes, null);
    }

    private static string? Str(JsonObject o, string name)
    {
        foreach (var (k, v) in o)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase) && v is JsonValue j && j.TryGetValue<string>(out var s)) return s;
        return null;
    }

    private static int? Int(JsonObject o, string name)
    {
        foreach (var (k, v) in o)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase) && v is JsonValue j && j.TryGetValue<int>(out var i)) return i;
        return null;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    private static string Slug(string s)
    {
        var slug = new string(s.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Length > 60 ? slug[..60].Trim('-') : slug;
        return slug.Length < 3 ? "imported-template" : slug;
    }
}
