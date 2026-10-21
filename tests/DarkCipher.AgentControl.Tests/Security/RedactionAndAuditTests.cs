using System.Diagnostics;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Security.Redaction;

namespace DarkCipher.AgentControl.Tests.Security;

public class RedactorTests
{
    private readonly Redactor _r = new();

    [Theory]
    [InlineData("token hf_abcdefghijklmnopqrstuvwxyz012345", "hf_abcdef")]
    [InlineData("key sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "sk-ant-api03")]
    [InlineData("OPENAI sk-proj-abcdefghijklmnopqrstuvwx", "sk-proj-abc")]
    [InlineData("gh ghp_abcdefghijklmnopqrstuvwxyz0123456789", "ghp_abc")]
    [InlineData("Authorization: Bearer abcdefghijklmnop.qrstuv", "abcdefghijklmnop")]
    [InlineData("api_key=SuperSecretValue123", "SuperSecretValue123")]
    [InlineData("\"password\": \"hunter2hunter2\"", "hunter2hunter2")]
    [InlineData("https://user:pa55word@example.com/x", "pa55word")]
    [InlineData("AKIAABCDEFGHIJKLMNOP", "AKIAABCDEFGHIJKLMNOP")]
    public void Masks_known_secret_shapes(string input, string mustNotRemain) =>
        Assert.DoesNotContain(mustNotRemain, _r.Redact(input), StringComparison.Ordinal);

    [Fact]
    public void Masks_pem_private_keys()
    {
        var pem = "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\nabc123==\n-----END PRIVATE KEY-----";
        Assert.DoesNotContain("MIIEvQ", _r.Redact("x " + pem + " y"), StringComparison.Ordinal);
    }

    [Fact]
    public void Masks_registered_secrets_verbatim()
    {
        _r.RegisterSecret("my-unusual-secret-42");
        Assert.Equal("value [REDACTED] end", _r.Redact("value my-unusual-secret-42 end"));
    }

    [Fact]
    public void Leaves_normal_text_alone() => Assert.Equal("Ollama 0.40.6 is running", _r.Redact("Ollama 0.40.6 is running"));

    [Fact]
    public void Is_linear_time_on_hostile_input()
    {
        var hostile = "api_key=" + new string('a', 200_000) + " " + string.Concat(Enumerable.Repeat("Bearer ", 20_000));
        var sw = Stopwatch.StartNew();
        _r.Redact(hostile);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }
}

public class AuditTests
{
    [Fact]
    public void Chain_verifies_and_survives_restart()
    {
        using var t = new TempDir();
        var a = Make.Audit(t.Path);
        for (var i = 0; i < 5; i++) a.Record(new AuditEvent("Test" + i, "Tests", AuditResult.Success, "r", SecurityClassification.Low));
        var b = Make.Audit(t.Path); // new instance continues the chain
        b.Record(new AuditEvent("After", "Tests", AuditResult.Success, null, SecurityClassification.Low));
        var v = b.Verify();
        Assert.True(v.Intact, v.Message);
        Assert.Equal(6, v.Lines);
    }

    [Fact]
    public void Editing_a_line_is_detected()
    {
        using var t = new TempDir();
        var a = Make.Audit(t.Path);
        a.Record(new AuditEvent("One", "Tests", AuditResult.Blocked, "x", SecurityClassification.High));
        a.Record(new AuditEvent("Two", "Tests", AuditResult.Success, "y", SecurityClassification.Low));
        var file = Path.Combine(t.Path, "security-audit.jsonl");
        File.WriteAllText(file, File.ReadAllText(file).Replace("Blocked", "Success", StringComparison.Ordinal));
        var v = Make.Audit(t.Path).Verify();
        Assert.False(v.Intact);
        Assert.Equal(1, v.FirstBrokenLine);
    }

    [Fact]
    public void Removing_a_line_is_detected()
    {
        using var t = new TempDir();
        var a = Make.Audit(t.Path);
        foreach (var n in new[] { "A", "B", "C" }) a.Record(new AuditEvent(n, "Tests", AuditResult.Success, null, SecurityClassification.Low));
        var file = Path.Combine(t.Path, "security-audit.jsonl");
        var lines = File.ReadAllLines(file).ToList();
        lines.RemoveAt(1);
        File.WriteAllLines(file, lines);
        Assert.False(Make.Audit(t.Path).Verify().Intact);
    }

    [Fact]
    public void Secrets_in_details_are_redacted_before_writing()
    {
        using var t = new TempDir();
        Make.Audit(t.Path).Record(new AuditEvent("X", "Tests", AuditResult.Success, null, SecurityClassification.Low, Detail: "token=hf_abcdefghijklmnopqrstuvwxyz0123"));
        Assert.DoesNotContain("hf_abcdefghijklmnop", File.ReadAllText(Path.Combine(t.Path, "security-audit.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_key_is_dpapi_protected_not_plaintext()
    {
        using var t = new TempDir();
        Make.Audit(t.Path);
        var key = File.ReadAllBytes(Path.Combine(t.Path, "audit.key"));
        Assert.True(key.Length > 32); // DPAPI blob, not a bare 32-byte key
    }
}
