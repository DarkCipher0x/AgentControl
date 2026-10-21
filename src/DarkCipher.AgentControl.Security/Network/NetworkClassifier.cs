using System.Net;
using System.Net.Sockets;
using DarkCipher.AgentControl.Domain.Network;

namespace DarkCipher.AgentControl.Security.Network;

/// <summary>
/// Classifies a destination without DNS resolution (a lookup would leak the name and split-horizon DNS makes it
/// unreliable). IP literals are classified by range; names are Loopback only for "localhost", LocalNetwork only for
/// single-label and reserved local suffixes, PublicInternet for a short list of known official services, and Unknown
/// otherwise. Unknown is never treated as local.
/// </summary>
public static class NetworkClassifier
{
    private static readonly string[] LocalSuffixes = [".local", ".lan", ".home.arpa", ".internal", ".localdomain"];

    private static readonly string[] KnownPublicHosts =
    [
        "huggingface.co", "cdn-lfs.huggingface.co", "cdn-lfs-us-1.huggingface.co", "ollama.com", "registry.ollama.ai",
        "api.anthropic.com", "api.openai.com", "github.com", "objects.githubusercontent.com", "api.github.com",
    ];

    public static NetworkClass Classify(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return NetworkClass.Unknown;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return NetworkClass.Unknown;
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)) return NetworkClass.Loopback;

        var bare = host.Trim('[', ']');
        if (IPAddress.TryParse(bare, out var ip)) return Classify(ip);

        if (!host.Contains('.')) return NetworkClass.LocalNetwork;
        if (LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal))) return NetworkClass.LocalNetwork;
        if (KnownPublicHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal))) return NetworkClass.PublicInternet;
        return NetworkClass.Unknown;
    }

    public static NetworkClass Classify(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return NetworkClass.Loopback;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return NetworkClass.Unknown;
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254))
                return NetworkClass.LocalNetwork;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return NetworkClass.PrivateRemote; // CGNAT / overlay VPNs
            if (b[0] >= 224) return NetworkClass.Unknown; // multicast / reserved
            return NetworkClass.PublicInternet;
        }
        if (ip.Equals(IPAddress.IPv6Any)) return NetworkClass.Unknown;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return NetworkClass.LocalNetwork;
        var v6 = ip.GetAddressBytes();
        if ((v6[0] & 0xFE) == 0xFC) return NetworkClass.PrivateRemote; // ULA fc00::/7
        if (ip.IsIPv6Multicast) return NetworkClass.Unknown;
        return NetworkClass.PublicInternet;
    }

    /// <summary>"scheme://host:port" key used for endpoint approvals.</summary>
    public static string EndpointKey(Uri uri) => $"{uri.Scheme}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}";
}
