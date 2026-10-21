using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Security.Audit;

/// <summary>
/// Append-only, tamper-evident security audit log. Each JSON line carries an HMAC-SHA256 over the previous line's MAC
/// plus its own content, keyed with a random 256-bit key protected by DPAPI (current user). Deleting, editing or
/// reordering a line breaks the chain from that point. Detail and resource fields are redacted before writing.
/// The log never contains secret values.
/// </summary>
public sealed class SecurityAuditService : IAuditLogger
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string _file;
    private readonly byte[] _key;
    private readonly Func<IRedactor?> _redactor;
    private readonly LinkedList<AuditEvent> _recent = new();
    private string _lastMac;

    public SecurityAuditService(IAppPaths paths, Func<IRedactor?> redactor)
        : this(paths.AuditDirectory, redactor) { }

    public SecurityAuditService(string auditDirectory, Func<IRedactor?> redactor)
    {
        Directory.CreateDirectory(auditDirectory);
        _file = Path.Combine(auditDirectory, "security-audit.jsonl");
        _redactor = redactor;
        _key = LoadOrCreateKey(Path.Combine(auditDirectory, "audit.key"));
        _lastMac = ReadLastMac() ?? "genesis";
    }

    private sealed record Line(
        DateTimeOffset Timestamp, string Action, string Component, AuditResult Result, string? AffectedResource,
        SecurityClassification Classification, string? CorrelationId, string? Detail, string Prev, string Mac);

    public void Record(AuditEvent e)
    {
        var red = _redactor();
        var safe = e with
        {
            AffectedResource = Truncate(red?.Redact(e.AffectedResource) ?? e.AffectedResource, 512),
            Detail = Truncate(red?.Redact(e.Detail) ?? e.Detail, 2048),
        };
        lock (_gate)
        {
            var unsigned = new Line(safe.Timestamp, safe.Action, safe.Component, safe.Result, safe.AffectedResource, safe.Classification,
                safe.Correlation?.Value, safe.Detail, _lastMac, "");
            var mac = ComputeMac(unsigned);
            var line = unsigned with { Mac = mac };
            try
            {
                using var fs = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
                fs.Write(bytes);
                _lastMac = mac;
            }
            catch (IOException)
            {
                // The in-memory copy below still shows the event; Verify() will report the gap. Never throw from auditing.
            }
            _recent.AddFirst(safe);
            while (_recent.Count > 2000) _recent.RemoveLast();
        }
    }

    public IReadOnlyList<AuditEvent> Recent(int max = 500)
    {
        lock (_gate)
        {
            if (_recent.Count == 0) LoadRecentFromDisk(max);
            return _recent.Take(max).ToList();
        }
    }

    public AuditVerification Verify()
    {
        lock (_gate)
        {
            if (!File.Exists(_file)) return new AuditVerification(true, 0, null, "No audit entries yet.");
            var prev = "genesis";
            var n = 0;
            foreach (var raw in ReadLines())
            {
                n++;
                Line? line;
                try { line = JsonSerializer.Deserialize<Line>(raw, Json); }
                catch (JsonException) { return new AuditVerification(false, n, n, $"Line {n} is not valid JSON."); }
                if (line is null || line.Prev != prev || !CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(ComputeMac(line with { Mac = "" })), Encoding.ASCII.GetBytes(line.Mac)))
                    return new AuditVerification(false, n, n, $"The audit chain is broken at line {n}: an entry was edited, removed or reordered.");
                prev = line.Mac;
            }
            return new AuditVerification(true, n, null, $"All {n} entries verified.");
        }
    }

    private string ComputeMac(Line l)
    {
        var payload = string.Join('\u001f', l.Timestamp.ToUniversalTime().ToString("O"), l.Action, l.Component, l.Result, l.AffectedResource,
            l.Classification, l.CorrelationId, l.Detail, l.Prev);
        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));
    }

    private string? ReadLastMac()
    {
        string? last = null;
        foreach (var raw in ReadLines()) last = raw;
        if (last is null) return null;
        try { return JsonSerializer.Deserialize<Line>(last, Json)?.Mac; } catch (JsonException) { return null; }
    }

    private void LoadRecentFromDisk(int max)
    {
        var lines = ReadLines().TakeLast(max).Reverse();
        foreach (var raw in lines)
        {
            try
            {
                var l = JsonSerializer.Deserialize<Line>(raw, Json);
                if (l is null) continue;
                _recent.AddLast(new AuditEvent(l.Action, l.Component, l.Result, l.AffectedResource, l.Classification,
                    l.CorrelationId is null ? null : new CorrelationId(l.CorrelationId), l.Detail) { Timestamp = l.Timestamp });
            }
            catch (JsonException) { }
        }
    }

    private IEnumerable<string> ReadLines()
    {
        if (!File.Exists(_file)) yield break;
        using var fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        while (sr.ReadLine() is { } line)
            if (line.Length > 0) yield return line;
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            try { return ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser); }
            catch (CryptographicException)
            {
                // Key unreadable (different user/machine): keep the old file for forensics and start a new chain.
                File.Move(path, path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            }
        }
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
        return key;
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";
}
