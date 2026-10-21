using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Security.Credentials;

namespace DarkCipher.AgentControl.Security.Network;

/// <summary>
/// The only HTTP client in the application. Enforces, for every request and every redirect hop:
/// http/https only; HTTPS for anything that is not loopback (unless the user approved a plain-HTTP local endpoint);
/// privacy-mode rules (Fully Local never sends inference off the PC); explicit approval for non-loopback endpoints;
/// default certificate validation (no overrides); timeouts; bounded GET-only retries; response size caps; manual
/// redirects with downgrade and cross-host checks; credentials never forwarded across hosts or to non-approved
/// destinations; no ambient Windows credentials. Blocks are audited.
/// </summary>
public sealed class NetworkSecurityService : INetworkClient, IDisposable
{
    private const int MaxRedirects = 3;
    private const int MaxRetries = 2;
    private readonly HttpClient _http;
    private readonly ISecurityPolicyService _policy;
    private readonly IAuditLogger _audit;
    private readonly SecureCredentialService? _credentials;
    private readonly IAppLogger? _log;

    public NetworkSecurityService(ISecurityPolicyService policy, IAuditLogger audit, SecureCredentialService? credentials, IAppLogger? log)
        : this(policy, audit, credentials, log, null) { }

    /// <summary>Test constructor: inject a fake handler (no real network in unit tests).</summary>
    internal NetworkSecurityService(ISecurityPolicyService policy, IAuditLogger audit, SecureCredentialService? credentials, IAppLogger? log, HttpMessageHandler? handler)
    {
        _policy = policy;
        _audit = audit;
        _credentials = credentials;
        _log = log;
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            DefaultProxyCredentials = null,
            PreAuthenticate = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxResponseHeadersLength = 64,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DarkCipher-AgentControl/0.1");
    }

    public NetworkClass Classify(Uri uri) => NetworkClassifier.Classify(uri);

    /// <summary>Policy decision for one destination. Public so the UI can explain a block before trying.</summary>
    public (bool Allowed, string Reason) Evaluate(Uri uri, NetworkPurpose purpose)
    {
        if (!uri.IsAbsoluteUri) return (false, "Only absolute URLs are allowed.");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return (false, $"The '{uri.Scheme}' scheme is not allowed.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) return (false, "URLs with embedded user names or passwords are refused.");

        var cls = Classify(uri);
        var p = _policy.Current;
        var approved = _policy.IsEndpointApproved(uri);

        if (cls == NetworkClass.Loopback) return (true, "Loopback");

        if (!approved) return (false, $"{Describe(cls)} destination {NetworkClassifier.EndpointKey(uri)} has not been approved. Approve it in Security → Network.");

        if (uri.Scheme == Uri.UriSchemeHttp && cls != NetworkClass.LocalNetwork)
            return (false, "Plain HTTP is only allowed on this PC or an approved local-network address.");

        if (purpose == NetworkPurpose.Inference)
        {
            switch (p.Privacy)
            {
                case PrivacyMode.FullyLocal when cls != NetworkClass.LocalNetwork:
                    return (false, "Fully Local mode: prompts and code never leave this PC or your local network.");
                case PrivacyMode.LocalPreferred when cls is NetworkClass.PublicInternet or NetworkClass.Unknown:
                    return (false, "Local Preferred mode: cloud inference is not allowed. Switch to Hybrid or Cloud Allowed to use it.");
            }
        }
        if (purpose == NetworkPurpose.RuntimeManagement && cls != NetworkClass.LocalNetwork && !p.AllowRemoteRuntimes)
            return (false, "Remote runtimes are disabled. Enable them in Security → Network.");

        return (true, Describe(cls));
    }

    public async Task<NetworkResponse> SendAsync(NetworkRequest request, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                using var response = await SendWithRedirectsAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                var body = await ReadBoundedAsync(response.Content, request.MaxResponseBytes, ct).ConfigureAwait(false);
                var headers = response.Headers.Concat(response.Content.Headers)
                    .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
                var status = (int)response.StatusCode;
                if (status >= 500 && request.Method == HttpMethod.Get && attempt < MaxRetries)
                {
                    attempt++;
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
                    continue;
                }
                return new NetworkResponse(status, body, headers);
            }
            catch (HttpRequestException ex) when (request.Method == HttpMethod.Get && attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                attempt++;
                _log?.Warn("Network", $"Retrying {request.Uri.GetLeftPart(UriPartial.Path)} after: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    public async IAsyncEnumerable<string> StreamLinesAsync(NetworkRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendWithRedirectsAsync(request, HttpCompletionOption.ResponseHeadersRead, ct, headerTimeoutOnly: true).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var err = await ReadBoundedAsync(response.Content, 64 * 1024, ct).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Truncate(err, 400)}");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            var line = await ReadLineBoundedAsync(reader, 64 * 1024, ct).ConfigureAwait(false);
            if (line is null) yield break;
            if (line.Length > 0) yield return line;
        }
    }

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(NetworkRequest request, HttpCompletionOption completion, CancellationToken ct, bool headerTimeoutOnly = false)
    {
        var uri = request.Uri;
        var originHost = uri.IdnHost;
        var method = request.Method;
        var body = request.JsonBody;
        for (var hop = 0; ; hop++)
        {
            var (allowed, reason) = Evaluate(uri, request.Purpose);
            if (!allowed)
            {
                _audit.Record(new AuditEvent("NetworkRequestBlocked", "NetworkSecurityService", AuditResult.Blocked,
                    NetworkClassifier.EndpointKey(uri), SecurityClassification.Medium, Detail: $"{request.Purpose}: {reason}"));
                throw new NetworkBlockedException(reason);
            }

            using var msg = new HttpRequestMessage(method, uri);
            if (body is not null) msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (request.Headers is not null)
                foreach (var (k, v) in request.Headers)
                {
                    if (k.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || k.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                        throw new NetworkBlockedException("Authorization headers must be supplied as a credential reference.");
                    msg.Headers.TryAddWithoutValidation(k, v);
                }
            // Credentials only to the original host (never across a redirect to another host) and only over HTTPS/loopback.
            if (request.BearerCredentialId is { } credId && string.Equals(uri.IdnHost, originHost, StringComparison.OrdinalIgnoreCase)
                && (uri.Scheme == Uri.UriSchemeHttps || Classify(uri) == NetworkClass.Loopback))
            {
                var secret = _credentials?.Reveal(credId) ?? throw new NetworkBlockedException("The credential for this request was not found.");
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(request.Timeout ?? TimeSpan.FromSeconds(30));
            HttpResponseMessage response;
            try { response = await _http.SendAsync(msg, completion, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException($"No response from {NetworkClassifier.EndpointKey(uri)} within {(request.Timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds:0} s."); }

            var code = (int)response.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } loc)
            {
                response.Dispose();
                if (hop >= MaxRedirects) throw new NetworkBlockedException("Too many redirects.");
                var next = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                if (uri.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                    throw new NetworkBlockedException("Redirect from HTTPS to HTTP refused.");
                if (code == 303) { method = HttpMethod.Get; body = null; }
                uri = next;
                continue;
            }
            _ = headerTimeoutOnly; // stream reads are bounded by the caller's token, not the header timeout
            return response;
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, long max, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } len && len > max)
            throw new InvalidDataException($"The response is too large ({len} bytes, limit {max}).");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > max) throw new InvalidDataException($"The response exceeded the {max}-byte limit.");
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static async Task<string?> ReadLineBoundedAsync(StreamReader reader, int max, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[1];
        while (true)
        {
            var n = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            if (buf[0] == '\n') return sb.ToString().TrimEnd('\r');
            if (sb.Length >= max) throw new InvalidDataException("A streamed line exceeded the size limit.");
            sb.Append(buf[0]);
        }
    }

    private static string Describe(NetworkClass c) => c switch
    {
        NetworkClass.Loopback => "This PC",
        NetworkClass.LocalNetwork => "Local network",
        NetworkClass.PrivateRemote => "Private remote",
        NetworkClass.PublicInternet => "Public internet",
        _ => "Unknown",
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose() => _http.Dispose();
}
