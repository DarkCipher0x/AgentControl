using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Tools;

namespace DarkCipher.AgentControl.Application.Tools;

/// <summary>A catalogue row: what the tool is, what is installed, what is available, and how that compares with what this
/// app has verified.</summary>
public sealed record ToolEntry(AgentToolDescriptor Descriptor, ToolStatus Status, string? AvailableVersion, DateTimeOffset CheckedAt)
{
    public bool UpdateAvailable => Status.Installed && AvailableVersion is not null && Status.Version is not null && VersionText.Compare(AvailableVersion, Status.Version) > 0;

    /// <summary>How the installed version relates to the verified range (shown next to the version).</summary>
    public string VersionVerdict
    {
        get
        {
            var d = Descriptor;
            if (!Status.Installed || Status.Version is null) return "";
            if (d.MinimumSupportedVersion is not null && !VersionText.AtLeast(Status.Version, d.MinimumSupportedVersion))
                return $"Older than the minimum supported version {d.MinimumSupportedVersion}";
            if (d.LatestVerifiedVersion is null) return "Not yet verified by this app";
            return VersionText.Compare(Status.Version, d.LatestVerifiedVersion) > 0
                ? $"Newer than the version this app was verified with ({d.LatestVerifiedVersion})"
                : "Within the verified range";
        }
    }

    public string TrustLabel => Descriptor.Trust switch
    {
        ToolTrust.BuiltInVerified => "Built-in provider (pinned package identity)",
        ToolTrust.ValidatedLocally => "Custom — validated on this PC",
        _ => "Custom — Unverified",
    };
}

/// <summary>
/// The agent-tool catalogue: built-in providers plus the user's custom tools. Detection runs with bounded concurrency;
/// results are cached with their time so the UI can show freshness, and "available version" lookups (network) only run
/// when asked for.
/// </summary>
public sealed class AgentToolCatalogueService
{
    private readonly IReadOnlyList<IAgentToolProvider> _builtIn;
    private readonly CustomAgentRegistry _custom;
    private readonly Func<CustomAgentDefinition, IAgentToolProvider> _customFactory;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Dictionary<string, ToolEntry> _cache = [];

    public AgentToolCatalogueService(IEnumerable<IAgentToolProvider> builtIn, CustomAgentRegistry custom, Func<CustomAgentDefinition, IAgentToolProvider> customFactory)
    {
        _builtIn = builtIn.ToList();
        _custom = custom;
        _customFactory = customFactory;
    }

    private readonly List<IAgentToolProvider> _external = [];

    public IReadOnlyList<IAgentToolProvider> Providers { get { lock (_external) return [.. _builtIn, .. _external, .. _custom.All.Select(_customFactory)]; } }

    /// <summary>Extension-contributed entries (always Unverified; an id that collides with a built-in is refused).</summary>
    public OperationResult RegisterExternal(IAgentToolProvider provider)
    {
        if (provider.Descriptor.Trust != ToolTrust.Unverified) return OperationResult.Fail("Contributed tools must be Unverified.");
        if (_builtIn.Any(b => b.Descriptor.Id == provider.Descriptor.Id || b.Descriptor.Source?.PackageId == provider.Descriptor.Source?.PackageId))
            return OperationResult.Fail("That tool is already provided by the app.");
        lock (_external)
        {
            _external.RemoveAll(p => p.Descriptor.Id == provider.Descriptor.Id);
            _external.Add(provider);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("Registered.");
    }

    public IAgentToolProvider? Find(string toolId) => Providers.FirstOrDefault(p => p.Descriptor.Id == toolId);

    public IReadOnlyList<ToolEntry> Cached => [.. _cache.Values];

    public event EventHandler? Changed;

    public async Task<IReadOnlyList<ToolEntry>> RefreshAsync(bool checkAvailable, CancellationToken ct)
    {
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var limit = new SemaphoreSlim(3, 3);
            var tasks = Providers.Select(async p =>
            {
                await limit.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var status = await p.DetectAsync(ct).ConfigureAwait(false);
                    var available = checkAvailable && p.Descriptor.Source is not null ? await p.GetAvailableVersionAsync(ct).ConfigureAwait(false)
                        : _cache.GetValueOrDefault(p.Descriptor.Id)?.AvailableVersion;
                    return new ToolEntry(p.Descriptor, status with { AvailableVersion = available }, available, DateTimeOffset.UtcNow);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    return new ToolEntry(p.Descriptor, new ToolStatus(p.Descriptor.Id, ToolInstallState.Unknown, null, null, ToolAuthState.Unknown, ComponentHealth.Unknown,
                        "Detection failed: " + ex.Message), null, DateTimeOffset.UtcNow);
                }
                finally { limit.Release(); }
            });
            var entries = await Task.WhenAll(tasks).ConfigureAwait(false);
            _cache = entries.ToDictionary(e => e.Descriptor.Id);
        }
        finally { _refresh.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        return Cached;
    }
}
