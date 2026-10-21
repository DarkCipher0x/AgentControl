using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Models;

namespace DarkCipher.AgentControl.Application.Compatibility;

/// <summary>Persists compatibility results (model, digest, runtime version, suite version, hardware, date, results).
/// A result applies only to the exact model digest it was run against.</summary>
public sealed class CompatibilityStore
{
    private const string StoreName = "compatibility";
    private readonly IDocumentStore _store;
    private readonly object _gate = new();
    private readonly List<CompatibilityReport> _reports;
    private readonly List<GatewayResult> _gateways;

    public CompatibilityStore(IDocumentStore store)
    {
        _store = store;
        var doc = store.Load(StoreName, () => new Doc());
        _reports = doc.Reports;
        _gateways = doc.Gateways;
    }

    public sealed record GatewayResult(string Agent, AgentProtocol Protocol, string Model, string? ModelDigest, CompatibilityVerdict Verdict, DateTimeOffset RunAt, string Detail);

    public event EventHandler? Changed;

    public IReadOnlyList<CompatibilityReport> Reports { get { lock (_gate) return [.. _reports]; } }

    public CompatibilityReport? Latest(string model, string? digest = null)
    {
        lock (_gate)
            return _reports.Where(r => Same(r.Model, model) && (digest is null || r.ModelDigest is null || r.ModelDigest == digest))
                           .OrderByDescending(r => r.RunAt).FirstOrDefault();
    }

    public GatewayResult? LatestGateway(string agent, string model)
    {
        lock (_gate) return _gateways.Where(g => g.Agent == agent && Same(g.Model, model)).OrderByDescending(g => g.RunAt).FirstOrDefault();
    }

    public void Add(CompatibilityReport report)
    {
        lock (_gate)
        {
            _reports.Add(report);
            if (_reports.Count > 200) _reports.RemoveRange(0, _reports.Count - 200);
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddGateway(GatewayResult result)
    {
        lock (_gate)
        {
            _gateways.Add(result);
            if (_gateways.Count > 200) _gateways.RemoveRange(0, _gateways.Count - 200);
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The agent compatibility matrix for the given installed models. Rows without a test say NotRun.</summary>
    public IReadOnlyList<AgentMatrixEntry> Matrix(IEnumerable<ModelDescriptor> models)
    {
        var rows = new List<AgentMatrixEntry>();
        foreach (var m in models.Where(m => m.Supports(ModelCapabilities.Completion)))
        {
            var report = Latest(m.Name, m.Digest);
            var tools = m.Supports(ModelCapabilities.Tools);
            var codexVerdict = !tools ? CompatibilityVerdict.TextGenerationOnly : report?.Verdict ?? CompatibilityVerdict.NotRun;
            rows.Add(new AgentMatrixEntry("Codex CLI", "Ollama", AgentProtocol.OpenAIResponses, m.Name, ModelCapabilities.Tools, 32768, codexVerdict,
                !tools ? "Model does not advertise tool calling." : report is null ? "Run the compatibility test." : $"Suite {report.SuiteVersion}, {report.RunAt:yyyy-MM-dd}"));
            var gw = LatestGateway("claude-code", m.Name);
            rows.Add(new AgentMatrixEntry("Claude Code (local gateway)", "Ollama", AgentProtocol.AnthropicMessages, m.Name, ModelCapabilities.Tools, 65536,
                !tools ? CompatibilityVerdict.TextGenerationOnly : gw?.Verdict ?? CompatibilityVerdict.NotRun,
                "Experimental; unsupported by Anthropic. " + (gw is null ? "Run the gateway test." : gw.Detail)));
        }
        rows.Add(new AgentMatrixEntry("Claude Code (native)", "Anthropic API", AgentProtocol.ProviderNative, "Claude models", ModelCapabilities.Tools, 0,
            CompatibilityVerdict.NotRun, "Vendor-supported; not tested by this app (cloud)."));
        return rows;
    }

    private void Persist() => _store.Save(StoreName, new Doc { Reports = _reports, Gateways = _gateways });

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || string.Equals(Strip(a), Strip(b), StringComparison.OrdinalIgnoreCase);

    private static string Strip(string n) => n.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? n[..^7] : n;

    public sealed class Doc
    {
        public int SchemaVersion { get; set; } = 1;
        public List<CompatibilityReport> Reports { get; set; } = [];
        public List<GatewayResult> Gateways { get; set; } = [];
    }
}
