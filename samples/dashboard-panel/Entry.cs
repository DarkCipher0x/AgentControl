using DarkCipher.AgentControl.Sdk;

namespace Sample.ModelPanel;

/// <summary>A dashboard panel described as data. The host renders it; the extension never touches UI types.</summary>
public sealed class Entry : IExtension
{
    public Task InitializeAsync(IExtensionContext context, CancellationToken cancellationToken)
    {
        var broker = context.Broker;
        broker.RegisterDashboardPanel(new SdkDashboardPanel("models", "Installed models", async ct =>
        {
            var models = await broker.GetInstalledModelsAsync(ct);
            var rows = new List<KeyValuePair<string, string>> { new("Installed", models.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            rows.AddRange(models.Select(m => new KeyValuePair<string, string>(m.Name, $"{m.ParameterSize} {m.Quantization}, tools: {m.Capabilities.Contains("Tools")}")));
            return rows;
        }));
        return Task.CompletedTask;
    }
}
