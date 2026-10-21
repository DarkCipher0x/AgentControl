using System.Text.Json;
using System.Text.Json.Serialization;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Security.Configuration;

namespace DarkCipher.AgentControl.Infrastructure.Persistence;

/// <summary>
/// Typed JSON documents in the config directory. Writes are atomic (temp + replace) and keep one ".prev" copy.
/// A malformed document is quarantined (renamed *.corrupt-&lt;timestamp&gt;), the previous good copy is tried, and only
/// then are defaults used; every recovery is recorded in <see cref="RecoveryNotices"/> and logged, never silent.
/// Documents never contain secrets (credentials live in Windows Credential Manager).
/// </summary>
public sealed class JsonDocumentStore : IDocumentStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 32,
    };

    private const long MaxDocumentBytes = 4 * 1024 * 1024;
    private readonly string _dir;
    private readonly object _gate = new();
    private readonly List<string> _notices = [];
    private readonly IAppLogger? _log;

    public JsonDocumentStore(string directory, IAppLogger? log = null)
    {
        _dir = directory;
        _log = log;
        Directory.CreateDirectory(_dir);
    }

    public IReadOnlyList<string> RecoveryNotices { get { lock (_gate) return [.. _notices]; } }

    public T Load<T>(string name, Func<T> createDefault) where T : class
    {
        var path = PathFor(name);
        lock (_gate)
        {
            if (!File.Exists(path)) return createDefault();
            var value = TryRead<T>(path, out var error);
            if (value is not null) return value;

            var quarantine = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try { File.Move(path, quarantine); } catch (IOException) { }
            var prev = TryRead<T>(path + ".prev", out _);
            var notice = prev is not null
                ? $"'{name}' was unreadable ({error}); restored the previous saved copy. The damaged file was kept as {Path.GetFileName(quarantine)}."
                : $"'{name}' was unreadable ({error}); defaults were used. The damaged file was kept as {Path.GetFileName(quarantine)}.";
            _notices.Add(notice);
            _log?.Warn("Config", notice);
            return prev ?? createDefault();
        }
    }

    public void Save<T>(string name, T value) where T : class
    {
        var path = PathFor(name);
        var json = JsonSerializer.Serialize(value, Options);
        lock (_gate)
        {
            if (File.Exists(path))
            {
                try { File.Copy(path, path + ".prev", overwrite: true); } catch (IOException) { }
            }
            ConfigurationProtectionService.AtomicWrite(path, json);
        }
    }

    private static T? TryRead<T>(string path, out string? error) where T : class
    {
        error = null;
        try
        {
            if (!File.Exists(path)) { error = "missing"; return null; }
            if (new FileInfo(path).Length > MaxDocumentBytes) { error = "too large"; return null; }
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new JsonException("empty document");
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            error = ex.Message.Split('\n')[0];
            return null;
        }
    }

    private string PathFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Invalid document name.");
        return Path.Combine(_dir, name + ".json");
    }
}
