using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Infrastructure.Observability;

/// <summary>
/// Structured JSON-lines application log with correlation IDs, redaction of every message and exception, size-based
/// rotation (5 files × 5 MB) and an in-memory ring buffer for the Logs screen. Logging never throws.
/// </summary>
public sealed class StructuredLogger : IAppLogger
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int KeepFiles = 5;
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly object _gate = new();
    private readonly string _dir;
    private readonly IRedactor _redactor;
    private readonly LinkedList<LogEntry> _ring = new();
    private readonly LogLevel _minimum;

    public StructuredLogger(string directory, IRedactor redactor, LogLevel minimum = LogLevel.Information)
    {
        _dir = directory;
        _redactor = redactor;
        _minimum = minimum;
        Directory.CreateDirectory(_dir);
    }

    public event EventHandler<LogEntry>? Logged;

    public string CurrentFile => Path.Combine(_dir, "agentcontrol.log.jsonl");

    public void Log(LogLevel level, string component, string message, CorrelationId? correlation = null, Exception? exception = null)
    {
        if (level < _minimum) return;
        var entry = new LogEntry(DateTimeOffset.UtcNow, level, component, _redactor.Redact(message), correlation?.Value,
            exception is null ? null : _redactor.Redact($"{exception.GetType().Name}: {exception.Message}"));
        lock (_gate)
        {
            _ring.AddLast(entry);
            while (_ring.Count > 5000) _ring.RemoveFirst();
            try
            {
                Rotate();
                File.AppendAllText(CurrentFile, JsonSerializer.Serialize(entry, Json) + "\n", Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try { Logged?.Invoke(this, entry); } catch (InvalidOperationException) { }
    }

    public IReadOnlyList<LogEntry> Recent(int max = 2000)
    {
        lock (_gate) return _ring.Reverse().Take(max).ToList();
    }

    private void Rotate()
    {
        var f = new FileInfo(CurrentFile);
        if (!f.Exists || f.Length < MaxFileBytes) return;
        for (var i = KeepFiles - 1; i >= 1; i--)
        {
            var src = CurrentFile + "." + i;
            var dst = CurrentFile + "." + (i + 1);
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        }
        File.Move(CurrentFile, CurrentFile + ".1", overwrite: true);
    }
}
