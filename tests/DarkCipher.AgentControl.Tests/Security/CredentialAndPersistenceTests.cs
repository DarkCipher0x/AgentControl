using DarkCipher.AgentControl.Infrastructure.Observability;
using DarkCipher.AgentControl.Infrastructure.Persistence;
using DarkCipher.AgentControl.Security.Credentials;
using DarkCipher.AgentControl.Security.Policy;
using DarkCipher.AgentControl.Security.Redaction;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Network;

namespace DarkCipher.AgentControl.Tests.Security;

/// <summary>Uses the real Windows Credential Manager under a test-only prefix and removes everything it creates.</summary>
public class CredentialTests
{
    private const string Prefix = "DarkCipher.AgentControl.UnitTest";

    [Fact]
    public void Secrets_round_trip_only_inside_the_security_assembly_and_are_redacted()
    {
        var redactor = new Redactor();
        var audit = new MemoryAudit();
        var svc = new SecureCredentialService(redactor, audit, Prefix);
        var secret = "unit-test-secret-" + Guid.NewGuid().ToString("N");
        var reference = svc.Save("Unit test", "test", secret);
        try
        {
            Assert.True(svc.Exists(reference.Id));
            Assert.Equal(secret, svc.Reveal(reference.Id));
            Assert.Contains(svc.List(), r => r.Id == reference.Id && r.DisplayName == "Unit test");
            Assert.DoesNotContain(secret, reference.ToString(), StringComparison.Ordinal);
            Assert.Equal("x [REDACTED] y", redactor.Redact($"x {secret} y"));
            Assert.All(audit.Events, e => Assert.DoesNotContain(secret, e.Detail ?? "", StringComparison.Ordinal));
        }
        finally { Assert.True(svc.Remove(reference.Id)); }
        Assert.False(svc.Exists(reference.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\0b")]
    public void Invalid_secrets_are_refused(string secret) =>
        Assert.Throws<ArgumentException>(() => new SecureCredentialService(new Redactor(), new MemoryAudit(), Prefix).Save("x", "test", secret));

    [Fact]
    public void Ids_cannot_address_other_credentials()
    {
        var svc = new SecureCredentialService(new Redactor(), new MemoryAudit(), Prefix);
        Assert.Null(svc.Reveal("../../git:https://github.com"));
        Assert.False(svc.Remove("*"));
    }
}

public class PersistenceTests
{
    public sealed class Doc { public int Value { get; set; } }

    [Fact]
    public void Malformed_documents_are_quarantined_and_previous_copy_restored()
    {
        using var t = new TempDir();
        var log = new MemoryLog();
        var store = new JsonDocumentStore(t.Path, log);
        store.Save("thing", new Doc { Value = 1 });
        store.Save("thing", new Doc { Value = 2 }); // .prev now holds 1
        File.WriteAllText(Path.Combine(t.Path, "thing.json"), "{ broken");
        var fresh = new JsonDocumentStore(t.Path, log);
        Assert.Equal(1, fresh.Load("thing", () => new Doc { Value = 99 }).Value);
        Assert.Single(fresh.RecoveryNotices);
        Assert.Single(Directory.GetFiles(t.Path, "thing.json.corrupt-*"));
    }

    [Fact]
    public void Invalid_document_names_are_refused()
    {
        using var t = new TempDir();
        Assert.Throws<ArgumentException>(() => new JsonDocumentStore(t.Path).Save("../escape", new Doc()));
    }

    [Fact]
    public void Data_root_inside_the_protected_projects_tree_is_refused()
    {
        var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects", "should-never-be-created");
        Assert.Throws<InvalidOperationException>(() => new AppPaths(projects));
        Assert.False(Directory.Exists(projects));
    }

    [Fact]
    public void Structured_log_redacts_and_rotates()
    {
        using var t = new TempDir();
        var log = new StructuredLogger(t.Path, new Redactor());
        log.Log(Domain.Common.LogLevel.Information, "Test", "token hf_abcdefghijklmnopqrstuvwxyz0123");
        Assert.DoesNotContain("hf_abcdefghij", File.ReadAllText(log.CurrentFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_changes_persist_and_weakening_is_audited_high()
    {
        var store = new MemoryStore();
        var audit = new MemoryAudit();
        var p = new SecurityPolicyService(store, audit);
        Assert.Equal(PrivacyMode.FullyLocal, p.Current.Privacy);
        Assert.True(p.Current.DisableThirdPartyExtensions);
        p.Update(x => x with { DevelopmentMode = true }, "test");
        Assert.Contains(audit.Events, e => e.Action == "SecurityPolicyChanged" && e.Classification == SecurityClassification.High);
        Assert.True(new SecurityPolicyService(store, audit).Current.DevelopmentMode);
    }

    [Fact]
    public void Command_line_safe_mode_cannot_be_turned_off_in_session()
    {
        var p = new SecurityPolicyService(new MemoryStore(), new MemoryAudit(), safeModeFromCommandLine: true);
        p.Update(x => x with { SafeMode = false }, "test");
        Assert.True(p.Current.SafeMode);
    }

    [Fact]
    public void Plain_http_approval_only_for_local_network()
    {
        var p = new SecurityPolicyService(new MemoryStore(), new MemoryAudit());
        Assert.False(p.ApproveEndpoint(new Uri("http://example.com"), "t").Success);
        Assert.True(p.ApproveEndpoint(new Uri("http://192.168.1.10:11434"), "t").Success);
        Assert.True(p.IsEndpointApproved(new Uri("http://192.168.1.10:11434/api/tags")));
    }
}
