using System.Text.Json;
using System.Text.Json.Nodes;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Stacks;
using DarkCipher.AgentControl.Domain.Templates;

namespace DarkCipher.AgentControl.Application.Templates;

/// <summary>The inputs that fully determine a generated configuration.</summary>
public sealed record ConfigurationInputs(
    TemplateDefinition Template,
    TemplateSelection Selection,
    IReadOnlyDictionary<string, string> ComponentVersions,
    string? ModelDigest,
    DarkCipher.AgentControl.Domain.Models.ModelCapabilities? ModelCapabilities = null);

/// <summary>A generated configuration: the stack, the launch plan rendered in a machine-independent form, and a fingerprint.</summary>
public sealed record GeneratedConfiguration(
    StackProfile Stack,
    string CanonicalJson,
    string Fingerprint,
    IReadOnlyList<string> PlanArguments,
    IReadOnlyDictionary<string, string> PlanEnvironment,
    IReadOnlyList<string> PlanFiles,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Deterministic configuration generation. Given the same template version, selection, component versions and model
/// digest, the output (and its fingerprint) is identical: no clocks, no random ids, sorted keys, and machine-specific paths
/// replaced by placeholders. The launch plan comes from the agent's typed provider; no AI model generates configuration.
/// </summary>
public sealed class ConfigurationBuilderService
{
    private readonly IReadOnlyList<IAgentProvider> _agents;

    public ConfigurationBuilderService(IEnumerable<IAgentProvider> agents) => _agents = agents.ToList();

    public const string Loopback = "http://127.0.0.1:11434";

    public StackProfile BuildStack(ConfigurationInputs i)
    {
        var t = i.Template;
        var s = i.Selection;
        var mode = s.AgentMode ?? t.Agents.First(a => a.AgentId == s.AgentId).Modes[0];
        var local = mode is AgentMode.LocalProvider or AgentMode.VerifiedLocalGateway or AgentMode.ExperimentalLocalGateway;
        var model = local ? s.Model ?? t.DefaultModel : null;
        var ctx = local ? s.ContextLength ?? (t.RecommendedContext > 0 ? t.RecommendedContext : t.MinimumContext) : (int?)null;
        var options = new SortedDictionary<string, string>(t.Options.ToDictionary(k => k.Key, k => k.Value), StringComparer.Ordinal);
        if (s.Reasoning is { } r) options[TemplateOptions.Reasoning] = r;
        // Reasoning levels reach the runtime as "think" requests; a model without thinking support rejects every request
        // (found live: Codex + llama3.2 failed with "does not support thinking"). Drop the option for such models.
        if (i.ModelCapabilities is { } caps && (caps & DarkCipher.AgentControl.Domain.Models.ModelCapabilities.Thinking) == 0) options.Remove(TemplateOptions.Reasoning);
        var selectionKey = Hashing.Sha256Hex(Canonical(new JsonObject
        {
            ["agent"] = s.AgentId, ["mode"] = mode.ToString(), ["model"] = model, ["ctx"] = ctx,
            ["permission"] = s.PermissionProfileId ?? t.PermissionProfileId, ["reasoning"] = s.Reasoning,
        }))[..8];
        return new StackProfile
        {
            Id = $"tpl-{t.Id}-{selectionKey}",
            Name = model is null ? t.DisplayName : $"{t.DisplayName} · {model}",
            Description = t.Description,
            AgentProviderId = s.AgentId,
            AgentMode = mode,
            RuntimeId = local ? t.Runtime?.RuntimeId : null,
            Endpoint = local ? Loopback : null,
            PrimaryModel = model,
            FallbackModel = t.Fallback == FallbackPolicy.Suggest ? t.FallbackModel : null,
            ContextLength = ctx,
            PermissionProfileId = s.PermissionProfileId ?? t.PermissionProfileId,
            Privacy = local ? PrivacyMode.FullyLocal : PrivacyMode.CloudAllowed,
            TemplateSource = "template:" + t.Id,
            TemplateVersion = t.Version,
            Options = options.Count == 0 ? null : options,
            SystemInstructions = t.SystemInstructions,
        };
    }

    public GeneratedConfiguration Generate(ConfigurationInputs i, PermissionProfile permissions)
    {
        var stack = BuildStack(i);
        var agent = _agents.FirstOrDefault(a => a.Id == stack.AgentProviderId);
        var warnings = new List<string>();
        if ((i.Selection.Reasoning ?? i.Template.Options.GetValueOrDefault(TemplateOptions.Reasoning)) is not null && stack.Options?.ContainsKey(TemplateOptions.Reasoning) != true)
            warnings.Add("The reasoning level was left out: this model does not support thinking.");
        IReadOnlyList<string> args = [];
        var env = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var files = new List<string>();
        if (agent is null) warnings.Add($"No provider for '{stack.AgentProviderId}'; only the stack was generated.");
        else
        {
            // Placeholder installation and folders: the plan must not depend on where things live on this PC.
            var install = new AgentInstallation(agent.Id, true, @"<agent>\" + agent.Id + ".exe", i.ComponentVersions.GetValueOrDefault(agent.Id), ComponentHealth.Healthy, "", []);
            var plan = agent.BuildLaunchPlan(new AgentLaunchRequest
            {
                Installation = install, Mode = stack.AgentMode, ProjectDirectory = @"<project>", Permissions = permissions,
                Model = stack.PrimaryModel, Endpoint = stack.Endpoint is null ? null : new Uri(stack.Endpoint), ContextLength = stack.ContextLength,
                ProfileDirectory = @"<profiles>\" + stack.Id, Options = stack.Options, SystemInstructions = stack.SystemInstructions,
            });
            args = plan.Process.Arguments;
            foreach (var (k, v) in plan.Process.Environment) env[k] = v;
            foreach (var f in plan.FileChanges.OrderBy(f => f.Path, StringComparer.Ordinal)) files.Add($"{f.Path}\n{f.NewContent}");
            warnings.AddRange(plan.Warnings);
        }

        var doc = new JsonObject
        {
            ["inputs"] = new JsonObject
            {
                ["template"] = i.Template.Id, ["templateVersion"] = i.Template.Version, ["modelCapabilities"] = i.ModelCapabilities?.ToString(),
                ["versions"] = Sorted(i.ComponentVersions), ["modelDigest"] = i.ModelDigest,
                ["selection"] = JsonSerializer.SerializeToNode(i.Selection, TemplateValidationService.Json),
            },
            ["stack"] = JsonSerializer.SerializeToNode(stack, TemplateValidationService.Json),
            ["arguments"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["environment"] = Sorted(env),
            ["files"] = new JsonArray(files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
        };
        var canonical = Canonical(doc);
        return new GeneratedConfiguration(stack with { ConfigurationFingerprint = Hashing.Sha256Hex(canonical)[..16] }, canonical, Hashing.Sha256Hex(canonical)[..16], args, env, files, warnings);
    }

    private static JsonObject Sorted(IEnumerable<KeyValuePair<string, string>> d)
    {
        var o = new JsonObject();
        foreach (var (k, v) in d.OrderBy(x => x.Key, StringComparer.Ordinal)) o[k] = v;
        return o;
    }

    /// <summary>Canonical JSON: object keys sorted ordinally at every level, no whitespace.</summary>
    public static string Canonical(JsonNode? node)
    {
        static JsonNode? Sort(JsonNode? n) => n switch
        {
            JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value?.DeepClone())))),
            JsonArray a => new JsonArray(a.Select(x => Sort(x?.DeepClone())).ToArray()),
            _ => n?.DeepClone(),
        };
        return Sort(node)?.ToJsonString() ?? "null";
    }
}
