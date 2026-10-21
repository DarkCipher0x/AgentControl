using System.Net;
using System.Net.NetworkInformation;
using DarkCipher.AgentControl.Integrations.Common;

namespace DarkCipher.AgentControl.Integrations.Ollama;

/// <summary>Finds an Ollama installation (official installer, AMD AI Bundle build, PATH) and checks whether the API is
/// exposed beyond this PC. Read-only: never changes Ollama's configuration or binding.</summary>
public sealed class OllamaDetector
{
    private readonly Func<IEnumerable<IPEndPoint>> _listeners;

    public OllamaDetector() : this(() => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()) { }

    internal OllamaDetector(Func<IEnumerable<IPEndPoint>> listeners) => _listeners = listeners;

    public static IEnumerable<string> CandidatePaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(local, "Programs", "Ollama", "ollama.exe");
        yield return Path.Combine(local, "AMD", "AI_Bundle", "Ollama", "ollama.exe");
        yield return Path.Combine(pf, "Ollama", "ollama.exe");
    }

    public string? FindInstallation() => ExecutableLocator.Find("ollama.exe", CandidatePaths());

    /// <summary>Returns a warning when something listens for the Ollama port on all interfaces or a non-loopback
    /// address (i.e. other machines on the network may reach the model API), otherwise null.</summary>
    public string? CheckExposure(int port)
    {
        try
        {
            var exposed = _listeners().Where(e => e.Port == port && !IPAddress.IsLoopback(e.Address)).ToList();
            if (exposed.Count == 0) return null;
            var addrs = string.Join(", ", exposed.Select(e => e.Address.Equals(IPAddress.Any) || e.Address.Equals(IPAddress.IPv6Any) ? "all interfaces" : e.Address.ToString()).Distinct());
            return $"Warning: the Ollama API on port {port} is listening on {addrs}, so other devices on your network may use your models. " +
                   "Unset OLLAMA_HOST (or set it to 127.0.0.1) and restart Ollama unless you intend this.";
        }
        catch (NetworkInformationException) { return null; }
    }
}
