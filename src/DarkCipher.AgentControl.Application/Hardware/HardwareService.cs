using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Hardware;

namespace DarkCipher.AgentControl.Application.Hardware;

/// <summary>Caches the latest hardware scan so screens don't re-run WMI queries; Rescan refreshes it.</summary>
public sealed class HardwareService
{
    private readonly IHardwareDiscoveryProvider _provider;
    private readonly IAppLogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HardwareService(IHardwareDiscoveryProvider provider, IAppLogger log)
    {
        _provider = provider;
        _log = log;
    }

    public HardwareProfile? Current { get; private set; }
    public HardwareAssessment? Assessment { get; private set; }
    public event EventHandler? Changed;

    public async Task<HardwareProfile> GetAsync(CancellationToken ct, bool rescan = false)
    {
        if (Current is not null && !rescan) return Current;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Current is not null && !rescan) return Current;
            var p = await _provider.ScanAsync(ct).ConfigureAwait(false);
            Current = p;
            Assessment = HardwareAssessor.Assess(p);
            _log.Info("Hardware", $"Scan complete: {HardwareAssessor.Summary(p)} → {Assessment.Headline}");
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        return Current;
    }
}
