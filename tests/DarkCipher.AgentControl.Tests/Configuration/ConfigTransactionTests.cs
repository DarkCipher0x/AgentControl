using DarkCipher.AgentControl.Application.Configuration;
using DarkCipher.AgentControl.Domain.Configuration;
using DarkCipher.AgentControl.Security.Configuration;
using DarkCipher.AgentControl.Security.Paths;

namespace DarkCipher.AgentControl.Tests.Configuration;

public class ConfigDocumentTests
{
    [Fact]
    public void Toml_merge_keeps_comments_order_and_unrelated_keys()
    {
        var doc = TomlSubsetDocument.Parse("# my notes\nmodel = \"old\"\nmy_key = 42 # mine\n\n[profiles]\nfast = true\n");
        doc.Set("model", "gpt-oss:20b");
        doc.Set("model_context_window", "65536");
        doc.Set("profiles.slow", "false");
        var text = doc.Render();
        Assert.StartsWith("# my notes\nmodel = \"gpt-oss:20b\"\nmy_key = 42 # mine\n", text, StringComparison.Ordinal);
        Assert.Contains("model_context_window = 65536", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("model_context_window", StringComparison.Ordinal) < text.IndexOf("[profiles]", StringComparison.Ordinal));
        Assert.Contains("[profiles]\nfast = true\nslow = false", text, StringComparison.Ordinal);
        var again = TomlSubsetDocument.Parse(text).Values;
        Assert.Equal("gpt-oss:20b", again["model"]);
        Assert.Equal("42", again["my_key"]);
    }

    [Theory]
    [InlineData("tags = [\"a\", \"b\"]")]
    [InlineData("[[servers]]\nname = \"x\"")]
    [InlineData("a.b = 1")]
    [InlineData("s = \"\"\"multi\"\"\"")]
    [InlineData("x = { y = 1 }")]
    [InlineData("k = 1\nk = 2")]
    public void Unsupported_toml_is_refused_rather_than_risked(string text) =>
        Assert.Throws<ConfigFormatException>(() => TomlSubsetDocument.Parse(text));

    [Fact]
    public void Json_merge_sets_nested_values_and_keeps_others()
    {
        var doc = JsonConfigDocument.Parse("""{ "theme": "dark", "env": { "KEEP": "1" } }""");
        doc.Set("env.ANTHROPIC_BASE_URL", "http://127.0.0.1:11434");
        doc.Set("count", "3");
        var v = JsonConfigDocument.Parse(doc.Render()).Values;
        Assert.Equal("dark", v["theme"]);
        Assert.Equal("1", v["env.KEEP"]);
        Assert.Equal("http://127.0.0.1:11434", v["env.ANTHROPIC_BASE_URL"]);
        Assert.Equal("3", v["count"]);
        Assert.Throws<ConfigFormatException>(() => JsonConfigDocument.Parse("""{ "theme": "dark" }""").Set("theme.inner", "x"));
    }
}

public class ConfigTransactionTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly MemoryAudit _audit = new();
    private readonly MemoryStore _store = new();
    private readonly ConfigurationTransactionService _tx;
    private readonly string _dir;

    public ConfigTransactionTests()
    {
        _dir = _t.Sub("profile");
        var paths = new ProtectedPathService([], null, null);
        _tx = new ConfigurationTransactionService(new ConfigurationProtectionService(paths, _audit, _t.Sub("backups")), paths, new ConfigurationOwnershipLedger(_store), _store, _audit);
    }

    public void Dispose() => _t.Dispose();

    private static IReadOnlyList<ConfigValue> V(params (string K, string V)[] v) => v.Select(x => new ConfigValue(x.K, x.V, ConfigOwnership.ApplicationManaged)).ToList();

    [Fact]
    public async Task New_file_is_created_verified_and_owned()
    {
        var path = Path.Combine(_dir, "config.toml");
        var p = _tx.PrepareFromGenerated(path, ConfigFormat.TomlSubset, "# Managed by DarkCipher AgentControl\nmodel = \"a\"\n", "test");
        Assert.True(p.HasChanges);
        Assert.Empty(p.Conflicts);
        var r = await _tx.CommitAsync(p, null, null, CancellationToken.None);
        Assert.Equal(TransactionState.Committed, r.State);
        Assert.Contains(r.Steps, s => s.Name == "Re-read" && s.Ok);
        Assert.Contains(r.Steps, s => s.Name == "Confirm intended values" && s.Ok);
        Assert.StartsWith("# Managed by DarkCipher AgentControl", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_file_is_merged_backed_up_and_unrelated_settings_kept()
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "# Managed by DarkCipher AgentControl\nmodel = \"a\"\nmy_setting = \"keep me\"\n");
        var p = _tx.Prepare(path, ConfigFormat.TomlSubset, V(("model", "b"), ("model_context_window", "65536")), "test");
        Assert.Equal(2, p.Changes.Count);
        Assert.Contains(p.Diff, l => l.StartsWith("+ model = \"b\"", StringComparison.Ordinal));
        var r = await _tx.CommitAsync(p, null, null, CancellationToken.None);
        Assert.Equal(TransactionState.Committed, r.State);
        Assert.NotNull(r.BackupPath);
        Assert.True(File.Exists(r.BackupPath));
        Assert.Contains("my_setting = \"keep me\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_changed_values_are_conflicts_never_silent_overwrites()
    {
        var path = Path.Combine(_dir, "config.toml");
        var first = await _tx.CommitAsync(_tx.Prepare(path, ConfigFormat.TomlSubset, V(("model", "a")), "test"), null, null, CancellationToken.None);
        Assert.Equal(TransactionState.Committed, first.State);
        File.WriteAllText(path, File.ReadAllText(path).Replace("model = \"a\"", "model = \"user-choice\"", StringComparison.Ordinal));

        var p = _tx.Prepare(path, ConfigFormat.TomlSubset, V(("model", "b")), "test");
        var conflict = Assert.Single(p.Conflicts);
        Assert.Equal("user-choice", conflict.CurrentValue);
        Assert.Equal("a", conflict.LastWrittenByApp);
        Assert.Equal(TransactionState.Failed, (await _tx.CommitAsync(p, null, null, CancellationToken.None)).State);
        Assert.Contains("user-choice", File.ReadAllText(path), StringComparison.Ordinal);

        var keep = _tx.Resolve(p, V(("model", "b")), new Dictionary<string, ConflictDecision> { ["model"] = ConflictDecision.KeepUserValue });
        Assert.Empty(keep.Conflicts);
        Assert.False(keep.HasChanges);

        var take = _tx.Resolve(p, V(("model", "b")), new Dictionary<string, ConflictDecision> { ["model"] = ConflictDecision.UseGeneratedValue });
        Assert.Empty(take.Conflicts);
        Assert.Equal(TransactionState.Committed, (await _tx.CommitAsync(take, null, null, CancellationToken.None)).State);
        Assert.Contains("model = \"b\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_files_without_a_record_are_treated_as_user_managed()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "model": "theirs" }""");
        var p = _tx.Prepare(path, ConfigFormat.Json, V(("model", "ours"), ("added", "1")), "test");
        Assert.Single(p.Conflicts);
        Assert.Single(p.Changes);   // the new key is fine; the existing user value needs a decision
    }

    [Fact]
    public async Task Failed_health_check_rolls_back_to_the_previous_file()
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "# Managed by DarkCipher AgentControl\nmodel = \"a\"\n");
        var before = File.ReadAllText(path);
        var p = _tx.Prepare(path, ConfigFormat.TomlSubset, V(("model", "b")), "test");
        var r = await _tx.CommitAsync(p, _ => Task.FromResult((false, "service did not come back")), null, CancellationToken.None);
        Assert.Equal(TransactionState.RolledBack, r.State);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Contains(_audit.Events, e => e.Action == "ConfigTransactionRolledBack");
    }

    [Fact]
    public async Task Failed_integration_test_removes_a_newly_created_file()
    {
        var path = Path.Combine(_dir, "new.toml");
        var p = _tx.Prepare(path, ConfigFormat.TomlSubset, V(("model", "b")), "test");
        var r = await _tx.CommitAsync(p, null, _ => throw new InvalidOperationException("boom"), CancellationToken.None);
        Assert.Equal(TransactionState.RolledBack, r.State);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Invalid_existing_file_is_never_changed()
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "tags = [\"a\"]\n");
        var p = _tx.Prepare(path, ConfigFormat.TomlSubset, V(("model", "b")), "test");
        Assert.Contains(p.Steps, s => !s.Ok);
        Assert.Equal(TransactionState.Failed, (await _tx.CommitAsync(p, null, null, CancellationToken.None)).State);
        Assert.Equal("tags = [\"a\"]\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task Roll_back_last_change_restores_the_backup_and_releases_ownership()
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "# Managed by DarkCipher AgentControl\nmodel = \"a\"\n");
        await _tx.CommitAsync(_tx.Prepare(path, ConfigFormat.TomlSubset, V(("model", "b")), "test"), null, null, CancellationToken.None);
        Assert.True(_tx.RollBackLast(path).Success);
        Assert.Contains("model = \"a\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains(_tx.History, h => h.State == TransactionState.RolledBack);
        Assert.False(_tx.RollBackLast(Path.Combine(_dir, "never.toml")).Success);
    }

    [Fact]
    public async Task A_file_changed_after_the_preview_is_not_overwritten()
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "# Managed by DarkCipher AgentControl\nmodel = \"a\"\n");
        var p = _tx.Prepare(path, ConfigFormat.TomlSubset, V(("model", "b")), "test");
        File.AppendAllText(path, "late = 1\n");
        Assert.Equal(TransactionState.Failed, (await _tx.CommitAsync(p, null, null, CancellationToken.None)).State);
        Assert.Contains("late = 1", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
