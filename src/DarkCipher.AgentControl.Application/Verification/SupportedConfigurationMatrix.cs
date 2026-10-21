using System.Text;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Verification;

namespace DarkCipher.AgentControl.Application.Verification;

/// <summary>
/// The versioned record of exactly which combinations were verified on this PC, with what result and when. A label of
/// "Supported and Verified" is shown only for an entry whose full end-to-end verification passed; one successful
/// combination never implies support for others.
/// </summary>
public sealed class SupportedConfigurationMatrix
{
    public const int SchemaVersion = 1;
    private const string StoreName = "supported-configurations";
    private readonly IDocumentStore _store;
    private readonly object _gate = new();
    private readonly List<SupportedConfigurationEntry> _entries;

    public SupportedConfigurationMatrix(IDocumentStore store)
    {
        _store = store;
        _entries = store.Load(StoreName, () => new Doc()).Entries;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<SupportedConfigurationEntry> Entries { get { lock (_gate) return [.. _entries]; } }

    public void Record(SupportedConfigurationEntry e)
    {
        lock (_gate)
        {
            _entries.RemoveAll(x => x.Label == e.Label && x.ConfigurationFingerprint == e.ConfigurationFingerprint);
            _entries.Add(e);
            if (_entries.Count > 300) _entries.RemoveRange(0, _entries.Count - 300);
            _store.Save(StoreName, new Doc { Entries = _entries });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Markdown table of the precise combinations (for docs and support requests).</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"| Result | OS | App | Agent | Runtime | Gateway | Model | Template | Context | Tool calls | Structured | Install | Launch | E2E | Verified | Limitations |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var e in Entries.OrderByDescending(x => x.VerifiedAt))
            sb.AppendLine($"| {VerificationClassifier.Label(e.Result)} | {e.OperatingSystem} | {e.AppVersion} | {e.Agent} {e.AgentVersion} | {e.Runtime} {e.RuntimeVersion} | " +
                          $"{(e.Gateway is null ? "—" : e.Gateway + " " + e.GatewayVersion)} | {e.Model} | {e.Template} {e.TemplateVersion} | {e.Context?.ToString("N0") ?? "—"} | " +
                          $"{e.ToolCalling} | {e.StructuredOutput} | {e.InstallResult} | {e.LaunchResult} | {e.EndToEndResult} | {e.VerifiedAt:yyyy-MM-dd} | {string.Join("; ", e.Limitations)} |");
        return sb.ToString();
    }

    public sealed class Doc
    {
        public int SchemaVersion { get; set; } = SupportedConfigurationMatrix.SchemaVersion;
        public List<SupportedConfigurationEntry> Entries { get; set; } = [];
    }
}
