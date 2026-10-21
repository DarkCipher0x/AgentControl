using System.Text.Json;
using System.Text.Json.Nodes;
using DarkCipher.AgentControl.Application.Downloads;
using DarkCipher.AgentControl.Application.Stacks;
using DarkCipher.AgentControl.Application.Templates;
using DarkCipher.AgentControl.Application.Tools;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Stacks;
using DarkCipher.AgentControl.Extensions;
using DarkCipher.AgentControl.Integrations.Tools;
using DarkCipher.AgentControl.Sdk;

namespace DarkCipher.AgentControl.App.Composition;

/// <summary>Connects the extension host (which knows only the SDK and Domain) to application services. Every contribution
/// is validated here; failures are raised to the extension as CapabilityDeniedException (contained by the host).</summary>
public sealed class HostServicesAdapter(StackService stacks, DownloadManager downloads, TemplateCatalogueService templates, AgentToolCatalogueService tools, WingetClient winget)
    : IExtensionHostServices
{
    public Version AppVersion => Bootstrapper.AppVersion;

    public void RegisterStackTemplate(string extensionId, SdkStackTemplate t)
    {
        if (!Enum.TryParse<AgentMode>(t.AgentMode, ignoreCase: false, out var mode)) throw new CapabilityDeniedException($"Unknown agent mode '{t.AgentMode}'.");
        var id = "ext-" + new string(t.Id.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(50).ToArray());
        stacks.RegisterContributed(new StackProfile
        {
            Id = id,
            Name = t.Name,
            Description = t.Description,
            AgentProviderId = t.AgentProviderId,
            AgentMode = mode,
            RuntimeId = t.RuntimeId,
            Endpoint = t.Endpoint,
            PrimaryModel = t.PrimaryModel,
            UtilityModel = t.UtilityModel,
            ContextLength = t.ContextLength,
        }, extensionId);
    }

    public Task<string> SubmitModelDownloadAsync(string extensionId, string modelName, CancellationToken ct)
    {
        var r = downloads.Enqueue(new DownloadRequest("ollama", modelName, $"{modelName} (requested by {extensionId})") { Source = "Ollama library (extension request)" });
        return Task.FromResult(r.Success ? r.Value!.Id : throw new CapabilityDeniedException(r.Message));
    }

    public void RegisterTemplate(string extensionId, string templateJson)
    {
        if (TemplateValidationService.ContainsSecret(templateJson)) throw new CapabilityDeniedException("The template contains something that looks like a credential.");
        JsonNode? node;
        try { node = JsonNode.Parse(templateJson, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException ex) { throw new CapabilityDeniedException("Template is not valid JSON: " + ex.Message.Split('\n')[0]); }
        var migrated = TemplateMigrationService.Migrate(node);
        if (migrated.Template is null) throw new CapabilityDeniedException(migrated.Error ?? "Not a template.");
        var r = templates.RegisterExtensionTemplate(migrated.Template, extensionId);
        if (!r.Success) throw new CapabilityDeniedException(r.Message);
    }

    public void RegisterAgentTool(string extensionId, SdkAgentToolEntry t)
    {
        ExtensionWingetToolProvider provider;
        try { provider = new ExtensionWingetToolProvider(winget, extensionId, t.Id, t.DisplayName, t.Publisher, t.Description, t.WingetPackageId, t.Homepage, t.Licence, t.Protocols); }
        catch (ArgumentException ex) { throw new CapabilityDeniedException(ex.Message); }
        var r = tools.RegisterExternal(provider);
        if (!r.Success) throw new CapabilityDeniedException(r.Message);
    }
}
