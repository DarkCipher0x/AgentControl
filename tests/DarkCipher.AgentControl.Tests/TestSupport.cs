using System.Diagnostics;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Infrastructure.Persistence;
using DarkCipher.AgentControl.Security.Audit;
using DarkCipher.AgentControl.Security.Redaction;

namespace DarkCipher.AgentControl.Tests;

/// <summary>Scratch folders live under the developer scratch folder (DCAC_DEV_SCRATCH, default %USERPROFILE%\.dcac-dev),
/// never inside the repository: the repository itself sits in the %USERPROFILE%\Projects tree, which the app protects
/// by default. Not under AppData either: packaged (MSIX) hosts redirect AppData writes, which breaks junction tests.</summary>
public sealed class TempDir : IDisposable
{
    public static readonly string RepoRoot = FindRepoRoot();
    public static readonly string Root = FindRoot();

    public TempDir()
    {
        Path = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts)
    {
        var p = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(System.IO.Path.Combine(d.FullName, "DarkCipherAgentControl.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>The developer scratch folder shared by tests, build.ps1 and tools\run-selftest.ps1.</summary>
    public static string DevScratch()
    {
        var configured = Environment.GetEnvironmentVariable("DCAC_DEV_SCRATCH");
        return string.IsNullOrWhiteSpace(configured)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dcac-dev")
            : System.IO.Path.GetFullPath(configured);
    }

    private static string FindRoot()
    {
        var root = System.IO.Path.Combine(DevScratch(), "tests");
        Directory.CreateDirectory(root);
        return root;
    }

    public void Dispose()
    {
        try
        {
            // Remove junctions first without following them.
            foreach (var dir in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
                if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(dir);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Creates a directory junction with mklink (test harness only; the app itself never shells out).</summary>
    public static bool TryCreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10000);
        return Directory.Exists(link);
    }
}

public sealed class TestPaths : IAppPaths
{
    public TestPaths(string root)
    {
        DataRoot = root;
        foreach (var d in new[] { ConfigDirectory, LogDirectory, AuditDirectory, BackupDirectory, ExtensionsDirectory, StagingDirectory, AgentProfilesDirectory, TestProjectsDirectory })
            Directory.CreateDirectory(d);
    }

    public string DataRoot { get; }
    public string ConfigDirectory => System.IO.Path.Combine(DataRoot, "config");
    public string LogDirectory => System.IO.Path.Combine(DataRoot, "logs");
    public string AuditDirectory => System.IO.Path.Combine(DataRoot, "audit");
    public string BackupDirectory => System.IO.Path.Combine(DataRoot, "backups");
    public string ExtensionsDirectory => System.IO.Path.Combine(DataRoot, "extensions");
    public string StagingDirectory => System.IO.Path.Combine(DataRoot, "staging");
    public string AgentProfilesDirectory => System.IO.Path.Combine(DataRoot, "agent-profiles");
    public string TestProjectsDirectory => System.IO.Path.Combine(DataRoot, "test-projects");
}

public sealed class MemoryAudit : IAuditLogger
{
    public List<AuditEvent> Events { get; } = [];
    public void Record(AuditEvent auditEvent) { lock (Events) Events.Add(auditEvent); }
    public IReadOnlyList<AuditEvent> Recent(int max = 500) { lock (Events) return Events.TakeLast(max).Reverse().ToList(); }
    public AuditVerification Verify() => new(true, Events.Count, null, "memory");
}

public sealed class MemoryStore : IDocumentStore
{
    private readonly Dictionary<string, object> _docs = [];
    public T Load<T>(string name, Func<T> createDefault) where T : class => _docs.TryGetValue(name, out var v) ? (T)v : createDefault();
    public void Save<T>(string name, T value) where T : class => _docs[name] = value;
    public IReadOnlyList<string> RecoveryNotices => [];
}

public sealed class MemoryLog : IAppLogger
{
    public List<LogEntry> Entries { get; } = [];
    public event EventHandler<LogEntry>? Logged;
    public void Log(LogLevel level, string component, string message, CorrelationId? correlation = null, Exception? exception = null)
    {
        var e = new LogEntry(DateTimeOffset.UtcNow, level, component, message, correlation?.Value, exception?.Message);
        lock (Entries) Entries.Add(e);
        Logged?.Invoke(this, e);
    }
    public IReadOnlyList<LogEntry> Recent(int max = 2000) { lock (Entries) return Entries.TakeLast(max).Reverse().ToList(); }
}

public sealed class FixedPolicy(SecurityPolicy policy) : ISecurityPolicyService
{
    public SecurityPolicy Current { get; set; } = policy;
    public event EventHandler<SecurityPolicy>? Changed;
    public OperationResult Update(Func<SecurityPolicy, SecurityPolicy> change, string reason) { Current = change(Current); Changed?.Invoke(this, Current); return OperationResult.Ok(); }
    public bool IsEndpointApproved(Uri uri) => Current.ApprovedEndpoints.Contains(DarkCipher.AgentControl.Security.Network.NetworkClassifier.EndpointKey(uri), StringComparer.OrdinalIgnoreCase);
}

public sealed class ScriptedApproval(bool answer) : IApprovalService
{
    public List<ApprovalRequest> Requests { get; } = [];
    public Task<bool> RequestAsync(ApprovalRequest request, CancellationToken ct) { Requests.Add(request); return Task.FromResult(answer); }
}

/// <summary>Fake network client for adapter tests (no real network).</summary>
public sealed class FakeNetwork : INetworkClient
{
    public Func<NetworkRequest, NetworkResponse> Handler { get; set; } = _ => new NetworkResponse(404, "", new Dictionary<string, string>());
    public Func<NetworkRequest, IEnumerable<string>> StreamHandler { get; set; } = _ => [];
    public List<NetworkRequest> Requests { get; } = [];
    public NetworkClass Classify(Uri uri) => DarkCipher.AgentControl.Security.Network.NetworkClassifier.Classify(uri);
    public Task<NetworkResponse> SendAsync(NetworkRequest request, CancellationToken ct) { Requests.Add(request); return Task.FromResult(Handler(request)); }
    public async IAsyncEnumerable<string> StreamLinesAsync(NetworkRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        foreach (var l in StreamHandler(request)) { await Task.Yield(); yield return l; }
    }
    public static NetworkResponse Json(string body, int status = 200) => new(status, body, new Dictionary<string, string>());
}

public static class Make
{
    public static Redactor Redactor() => new();
    public static SecurityAuditService Audit(string dir) => new(dir, () => new Redactor());
}
