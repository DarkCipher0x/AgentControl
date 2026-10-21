using DarkCipher.AgentControl.Domain.Agents;

namespace DarkCipher.AgentControl.Integrations.Common;

/// <summary>Keeps an agent detection result for a short time so several screens don't each start "--version" probes.
/// <see cref="Invalidate"/> forces the next call to re-detect (Rescan buttons).</summary>
public sealed class DetectionCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (AgentInstallation Value, DateTimeOffset At)? _cached;

    public void Invalidate() => _cached = null;

    public async Task<AgentInstallation> GetAsync(Func<CancellationToken, Task<AgentInstallation>> detect, CancellationToken ct)
    {
        if (_cached is { } c && DateTimeOffset.UtcNow - c.At < Lifetime) return c.Value;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } c2 && DateTimeOffset.UtcNow - c2.At < Lifetime) return c2.Value;
            var v = await detect(ct).ConfigureAwait(false);
            _cached = (v, DateTimeOffset.UtcNow);
            return v;
        }
        finally { _gate.Release(); }
    }
}
