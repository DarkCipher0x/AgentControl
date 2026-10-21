using System.Net;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Security.Network;

namespace DarkCipher.AgentControl.Tests.Security;

public class NetworkClassifierTests
{
    [Theory]
    [InlineData("http://localhost:11434", NetworkClass.Loopback)]
    [InlineData("http://127.0.0.1:11434", NetworkClass.Loopback)]
    [InlineData("http://[::1]:11434", NetworkClass.Loopback)]
    [InlineData("http://10.0.0.5", NetworkClass.LocalNetwork)]
    [InlineData("http://192.168.1.20", NetworkClass.LocalNetwork)]
    [InlineData("http://172.20.0.1", NetworkClass.LocalNetwork)]
    [InlineData("http://172.32.0.1", NetworkClass.PublicInternet)]
    [InlineData("http://100.100.1.1", NetworkClass.PrivateRemote)]
    [InlineData("http://[fd00::1]", NetworkClass.PrivateRemote)]
    [InlineData("http://nas", NetworkClass.LocalNetwork)]
    [InlineData("http://server.lan", NetworkClass.LocalNetwork)]
    [InlineData("https://huggingface.co", NetworkClass.PublicInternet)]
    [InlineData("https://evil-example.net", NetworkClass.Unknown)]
    [InlineData("http://0.0.0.0", NetworkClass.Unknown)]
    public void Classifies_without_dns(string url, NetworkClass expected) => Assert.Equal(expected, NetworkClassifier.Classify(new Uri(url)));
}

public class NetworkSecurityTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static (NetworkSecurityService Svc, FakeHandler Handler, MemoryAudit Audit, FixedPolicy Policy) Make(SecurityPolicy policy, Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new FakeHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        var audit = new MemoryAudit();
        var p = new FixedPolicy(policy);
        return (new NetworkSecurityService(p, audit, null, null, handler), handler, audit, p);
    }

    [Fact]
    public async Task Loopback_is_allowed_over_http()
    {
        var (svc, h, _, _) = Make(new SecurityPolicy());
        var r = await svc.SendAsync(new NetworkRequest(HttpMethod.Get, new Uri("http://127.0.0.1:11434/api/version"), NetworkPurpose.RuntimeManagement), CancellationToken.None);
        Assert.Equal(200, r.StatusCode);
        Assert.Single(h.Seen);
    }

    [Fact]
    public async Task Unapproved_public_endpoint_is_blocked_before_connecting_and_audited()
    {
        var (svc, h, audit, _) = Make(new SecurityPolicy());
        await Assert.ThrowsAsync<NetworkBlockedException>(() => svc.SendAsync(new NetworkRequest(HttpMethod.Get, new Uri("https://huggingface.co/api/models"), NetworkPurpose.ModelCatalog), CancellationToken.None));
        Assert.Empty(h.Seen);
        Assert.Contains(audit.Events, e => e.Action == "NetworkRequestBlocked");
    }

    [Fact]
    public async Task Plain_http_to_public_is_refused_even_when_approved()
    {
        var (svc, h, _, _) = Make(new SecurityPolicy { ApprovedEndpoints = ["http://example.com:80"], Privacy = PrivacyMode.CloudAllowed });
        await Assert.ThrowsAsync<NetworkBlockedException>(() => svc.SendAsync(new NetworkRequest(HttpMethod.Get, new Uri("http://example.com/"), NetworkPurpose.ModelCatalog), CancellationToken.None));
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task Fully_local_never_sends_inference_to_the_internet()
    {
        var (svc, h, _, _) = Make(new SecurityPolicy { Privacy = PrivacyMode.FullyLocal, ApprovedEndpoints = ["https://api.example.com:443"] });
        await Assert.ThrowsAsync<NetworkBlockedException>(() => svc.SendAsync(new NetworkRequest(HttpMethod.Post, new Uri("https://api.example.com/v1/chat"), NetworkPurpose.Inference), CancellationToken.None));
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task Approved_local_network_inference_is_allowed_in_fully_local()
    {
        var (svc, h, _, p) = Make(new SecurityPolicy { Privacy = PrivacyMode.FullyLocal, ApprovedEndpoints = ["http://192.168.1.50:11434"] });
        await svc.SendAsync(new NetworkRequest(HttpMethod.Post, new Uri("http://192.168.1.50:11434/api/chat"), NetworkPurpose.Inference), CancellationToken.None);
        Assert.Single(h.Seen);
    }

    [Fact]
    public async Task Https_to_http_redirect_is_refused()
    {
        var (svc, _, _, _) = Make(new SecurityPolicy { ApprovedEndpoints = ["https://huggingface.co:443"] }, req =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("http://huggingface.co/plain");
            return r;
        });
        await Assert.ThrowsAsync<NetworkBlockedException>(() => svc.SendAsync(new NetworkRequest(HttpMethod.Get, new Uri("https://huggingface.co/api/models"), NetworkPurpose.ModelCatalog), CancellationToken.None));
    }

    [Fact]
    public async Task Redirect_to_unapproved_host_is_refused()
    {
        var (svc, h, _, _) = Make(new SecurityPolicy { ApprovedEndpoints = ["https://huggingface.co:443"] }, req =>
        {
            if (req.RequestUri!.Host != "huggingface.co") return new HttpResponseMessage(HttpStatusCode.OK);
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://attacker.example/steal");
            return r;
        });
        await Assert.ThrowsAsync<NetworkBlockedException>(() => svc.SendAsync(new NetworkRequest(HttpMethod.Get, new Uri("https://huggingface.co/x"), NetworkPurpose.ModelCatalog), CancellationToken.None));
        Assert.Single(h.Seen);
    }

    [Fact]
    public async Task Embedded_credentials_and_raw_authorization_headers_are_refused()
    {
        var (svc, _, _, _) = Make(new SecurityPolicy());
        await Assert.ThrowsAsync<NetworkBlockedException>(() => svc.SendAsync(new NetworkRequest(HttpMethod.Get, new Uri("http://user:pw@127.0.0.1/"), NetworkPurpose.RuntimeManagement), CancellationToken.None));
        await Assert.ThrowsAsync<NetworkBlockedException>(() => svc.SendAsync(new NetworkRequest(HttpMethod.Get, new Uri("http://127.0.0.1/"), NetworkPurpose.RuntimeManagement)
        { Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer x" } }, CancellationToken.None));
    }

    [Fact]
    public async Task Response_size_is_capped()
    {
        var (svc, _, _, _) = Make(new SecurityPolicy(), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 5000)) });
        await Assert.ThrowsAsync<InvalidDataException>(() => svc.SendAsync(new NetworkRequest(HttpMethod.Get, new Uri("http://127.0.0.1/"), NetworkPurpose.RuntimeManagement) { MaxResponseBytes = 1000 }, CancellationToken.None));
    }

    [Fact]
    public async Task Non_http_schemes_are_refused()
    {
        var (svc, _, _, _) = Make(new SecurityPolicy());
        await Assert.ThrowsAsync<NetworkBlockedException>(() => svc.SendAsync(new NetworkRequest(HttpMethod.Get, new Uri("file:///C:/Windows/win.ini"), NetworkPurpose.RuntimeManagement), CancellationToken.None));
    }

    [Fact]
    public async Task Remote_runtime_management_requires_opt_in()
    {
        var (svc, _, _, _) = Make(new SecurityPolicy { ApprovedEndpoints = ["https://gpu.example.com:443"], AllowRemoteRuntimes = false });
        await Assert.ThrowsAsync<NetworkBlockedException>(() => svc.SendAsync(new NetworkRequest(HttpMethod.Get, new Uri("https://gpu.example.com/api/tags"), NetworkPurpose.RuntimeManagement), CancellationToken.None));
    }
}
