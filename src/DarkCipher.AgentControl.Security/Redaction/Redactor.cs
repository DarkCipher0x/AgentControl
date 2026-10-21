using System.Text.RegularExpressions;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Security.Redaction;

/// <summary>
/// Removes secrets from text before it is logged, displayed, copied or exported. Patterns use the non-backtracking
/// engine so hostile input cannot cause catastrophic backtracking (ReDoS); every secret stored through the credential
/// service is also registered here and replaced verbatim.
/// </summary>
public sealed class Redactor : IRedactor
{
    public const string Mask = "[REDACTED]";
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    private static readonly Regex[] TokenPatterns =
    [
        new(@"hf_[A-Za-z0-9]{20,}", Options),
        new(@"sk-ant-[A-Za-z0-9_\-]{20,}", Options),
        new(@"sk-(proj-)?[A-Za-z0-9_\-]{20,}", Options),
        new(@"gh[pousr]_[A-Za-z0-9]{30,}", Options),
        new(@"github_pat_[A-Za-z0-9_]{30,}", Options),
        new(@"glpat-[A-Za-z0-9_\-]{20,}", Options),
        new(@"xox[abprs]-[A-Za-z0-9\-]{10,}", Options),
        new(@"AKIA[0-9A-Z]{16}", Options),
        new(@"AIza[0-9A-Za-z_\-]{35}", Options),
        new(@"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", Options), // JWT
        // PEM bodies are base64 (no '-'), so "[^-]*" spans the key without a large automaton.
        new(@"-----BEGIN [A-Z ]{0,20}PRIVATE KEY-----[^-]*-----END [A-Z ]{0,20}PRIVATE KEY-----", Options),
    ];

    private static readonly Regex Bearer = new(@"([Bb]earer|[Bb]asic)\s+[A-Za-z0-9._\-~+/]{8,}=*", Options);

    // key = value / "key": "value" for well-known secret names. Group 1 = name + separator (kept), group 2 = value.
    private static readonly Regex KeyValue = new(
        @"((?:[A-Za-z0-9_\-]*(?:api[_\-]?key|apikey|access[_\-]?key|secret|token|password|passwd|pwd|auth[_\-]?token|client[_\-]?secret|private[_\-]?key))[""']?\s*[:=]\s*[""']?)([^\s""',;&]{4,})",
        Options | RegexOptions.IgnoreCase);

    private static readonly Regex UrlUserInfo = new(@"(://)[^/\s:@]+:[^/\s@]+@", Options);

    private readonly object _gate = new();
    private readonly List<string> _known = [];

    public void RegisterSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 4) return;
        lock (_gate)
        {
            if (!_known.Contains(secret, StringComparer.Ordinal))
            {
                _known.Add(secret);
                _known.Sort((a, b) => b.Length.CompareTo(a.Length)); // longest first
            }
        }
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = text;
        string[] known;
        lock (_gate) known = [.. _known];
        foreach (var k in known) s = s.Replace(k, Mask, StringComparison.Ordinal);

        foreach (var r in TokenPatterns) s = r.Replace(s, Mask);
        s = Bearer.Replace(s, m => m.Value.Split(' ', 2)[0] + " " + Mask);
        s = KeyValue.Replace(s, m => m.Groups[1].Value + Mask);
        s = UrlUserInfo.Replace(s, "$1" + Mask + "@");
        return s;
    }
}
