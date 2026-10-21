using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Security.Paths;

namespace DarkCipher.AgentControl.Tests.Security;

public class PathCanonicalizerTests
{
    [Fact]
    public void Resolves_dot_segments_and_forward_slashes()
    {
        using var t = new TempDir();
        var a = t.Sub("a", "b");
        var weird = t.Path.Replace('\\', '/') + "/a/./b/../b/";
        Assert.Equal(PathCanonicalizer.Canonicalize(a), PathCanonicalizer.Canonicalize(weird), ignoreCase: true);
    }

    [Fact]
    public void Strips_win32_file_namespace_prefix()
    {
        using var t = new TempDir();
        Assert.Equal(PathCanonicalizer.Canonicalize(t.Path), PathCanonicalizer.Canonicalize(@"\\?\" + t.Path), ignoreCase: true);
    }

    [Fact]
    public void Normalises_trailing_dots_and_spaces_like_windows()
    {
        using var t = new TempDir();
        var a = t.Sub("proj");
        Assert.Equal(PathCanonicalizer.Canonicalize(a), PathCanonicalizer.Canonicalize(a + ". "), ignoreCase: true);
    }

    [Theory]
    [InlineData(@"C:\temp\file.txt:secret")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1")]
    [InlineData(@"C:\temp\CON")]
    [InlineData(@"C:\temp\nul.txt")]
    [InlineData(@"C:\temp\a|b")]
    [InlineData("C:\\temp\\a\0b")]
    [InlineData("")]
    public void Rejects_dangerous_forms(string path) => Assert.Throws<UnsafePathException>(() => PathCanonicalizer.Canonicalize(path));

    [Fact]
    public void Refuses_relative_paths_without_a_base() => Assert.Throws<UnsafePathException>(() => PathCanonicalizer.Canonicalize(@"..\x"));

    [Fact]
    public void Segment_aware_containment()
    {
        Assert.True(PathCanonicalizer.IsSameOrUnder(@"C:\Projects\a", @"C:\Projects"));
        Assert.True(PathCanonicalizer.IsSameOrUnder(@"c:\projects", @"C:\Projects"));
        Assert.False(PathCanonicalizer.IsSameOrUnder(@"C:\Projects2\a", @"C:\Projects"));
        Assert.True(PathCanonicalizer.IsSameOrUnder(@"C:\x", @"C:\"));
    }

    [Fact]
    public void Junction_resolves_to_its_target()
    {
        using var t = new TempDir();
        var target = t.Sub("protected");
        var link = Path.Combine(t.Path, "innocent");
        if (!TempDir.TryCreateJunction(link, target)) return; // environment without mklink: covered by other tests
        Assert.Equal(PathCanonicalizer.Canonicalize(Path.Combine(target, "x.txt")), PathCanonicalizer.Canonicalize(Path.Combine(link, "x.txt")), ignoreCase: true);
    }

    [Fact]
    public void Short_83_name_resolves_to_long_name()
    {
        using var t = new TempDir();
        var longDir = t.Sub("A very long directory name for 8dot3");
        var buf = new char[512];
        var n = GetShortPathNameW(longDir, buf, (uint)buf.Length);
        if (n == 0) return;
        var shortName = new string(buf, 0, (int)n);
        if (string.Equals(shortName, longDir, StringComparison.OrdinalIgnoreCase)) return; // 8.3 disabled on this volume
        Assert.Equal(PathCanonicalizer.Canonicalize(longDir), PathCanonicalizer.Canonicalize(shortName), ignoreCase: true);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, char[] shortPath, uint bufferLength);
}

public class ProtectedPathServiceTests
{
    private static ProtectedPathService Service(string protectedRoot, ProtectedPathRule rules, MemoryAudit? audit = null) =>
        new([new ProtectedPathEntry(protectedRoot, rules, "Test protected", true)], null, audit);

    [Fact]
    public void Read_only_blocks_writes_and_deletes_but_allows_reads()
    {
        using var t = new TempDir();
        var root = t.Sub("work");
        var svc = Service(root, ProtectedPathRule.ReadOnly);
        Assert.Equal(PathDecisionKind.Deny, svc.Evaluate(Path.Combine(root, "a.cs"), PathOperation.Write).Kind);
        Assert.Equal(PathDecisionKind.Deny, svc.Evaluate(Path.Combine(root, "a.cs"), PathOperation.Delete).Kind);
        Assert.Equal(PathDecisionKind.Allow, svc.Evaluate(Path.Combine(root, "a.cs"), PathOperation.Read).Kind);
        Assert.Equal(PathDecisionKind.RequiresApproval, svc.Evaluate(root, PathOperation.UseAsWorkingDirectory, PathActor.Agent).Kind);
    }

    [Fact]
    public void Traversal_out_of_an_allowed_folder_into_a_protected_one_is_denied()
    {
        using var t = new TempDir();
        var root = t.Sub("protected");
        t.Sub("allowed");
        var svc = Service(root, ProtectedPathRule.BlockAllAccess);
        var sneaky = Path.Combine(t.Path, "allowed", "..", "protected", "secret.txt");
        Assert.Equal(PathDecisionKind.Deny, svc.Evaluate(sneaky, PathOperation.Read).Kind);
    }

    [Fact]
    public void Case_and_prefix_variants_cannot_bypass()
    {
        using var t = new TempDir();
        var root = t.Sub("Protected");
        var svc = Service(root, ProtectedPathRule.ReadOnly);
        Assert.Equal(PathDecisionKind.Deny, svc.Evaluate(@"\\?\" + root.ToUpperInvariant() + @"\X.TXT", PathOperation.Write).Kind);
    }

    [Fact]
    public void Junction_into_a_protected_folder_is_denied()
    {
        using var t = new TempDir();
        var root = t.Sub("protected");
        var link = Path.Combine(t.Path, "link");
        if (!TempDir.TryCreateJunction(link, root)) return;
        var svc = Service(root, ProtectedPathRule.ReadOnly);
        Assert.Equal(PathDecisionKind.Deny, svc.Evaluate(Path.Combine(link, "file.txt"), PathOperation.Write).Kind);
    }

    [Fact]
    public void Similar_prefix_folder_is_not_protected()
    {
        using var t = new TempDir();
        var root = t.Sub("proj");
        var sibling = t.Sub("proj2");
        var svc = Service(root, ProtectedPathRule.BlockAllAccess);
        Assert.Equal(PathDecisionKind.Allow, svc.Evaluate(Path.Combine(sibling, "a.txt"), PathOperation.Write).Kind);
    }

    [Fact]
    public void Data_root_rule_binds_agents_and_extensions_but_not_the_app()
    {
        using var t = new TempDir();
        var data = t.Sub("data");
        var svc = new ProtectedPathService([new ProtectedPathEntry(data, ProtectedPathRule.BlockAllAccess, "data", true, AppliesToApplication: false)], null, null);
        Assert.Equal(PathDecisionKind.Deny, svc.Evaluate(Path.Combine(data, "x"), PathOperation.Read, PathActor.Agent).Kind);
        Assert.Equal(PathDecisionKind.Deny, svc.Evaluate(Path.Combine(data, "x"), PathOperation.Read, PathActor.Extension).Kind);
        Assert.Equal(PathDecisionKind.Allow, svc.Evaluate(Path.Combine(data, "x"), PathOperation.Write, PathActor.Application).Kind);
    }

    [Fact]
    public void Default_builtins_close_sensitive_app_folders_but_allow_test_projects_and_agent_profiles()
    {
        using var t = new TempDir();
        var paths = new TestPaths(t.Sub("data"));
        var svc = new ProtectedPathService(ProtectedPathService.DefaultBuiltIns(paths), null, null);
        Assert.Equal(PathDecisionKind.Deny, svc.Evaluate(paths.DataRoot, PathOperation.UseAsWorkingDirectory, PathActor.Agent).Kind);
        foreach (var d in new[] { paths.ConfigDirectory, paths.AuditDirectory, paths.LogDirectory, paths.BackupDirectory, paths.ExtensionsDirectory, paths.StagingDirectory })
            Assert.Equal(PathDecisionKind.Deny, svc.Evaluate(Path.Combine(d, "x"), PathOperation.Read, PathActor.Agent).Kind);
        var sample = Directory.CreateDirectory(Path.Combine(paths.TestProjectsDirectory, "sample")).FullName;
        Assert.Equal(PathDecisionKind.Allow, svc.Evaluate(sample, PathOperation.UseAsWorkingDirectory, PathActor.Agent).Kind);
        Assert.Equal(PathDecisionKind.Allow, svc.Evaluate(Path.Combine(paths.AgentProfilesDirectory, "s1", "config.toml"), PathOperation.Write).Kind);
    }

    [Fact]
    public void No_process_execution_rule_blocks_exe_and_working_dir()
    {
        using var t = new TempDir();
        var root = t.Sub("noexec");
        var svc = Service(root, ProtectedPathRule.NoProcessExecution);
        Assert.Equal(PathDecisionKind.Deny, svc.Evaluate(Path.Combine(root, "tool.exe"), PathOperation.Execute).Kind);
        Assert.Equal(PathDecisionKind.Deny, svc.Evaluate(root, PathOperation.UseAsWorkingDirectory, PathActor.Agent).Kind);
    }

    [Fact]
    public void Drive_root_as_working_directory_needs_approval()
    {
        var svc = new ProtectedPathService([], null, null);
        Assert.Equal(PathDecisionKind.RequiresApproval, svc.Evaluate(@"C:\", PathOperation.UseAsWorkingDirectory, PathActor.Agent).Kind);
    }

    [Fact]
    public void Violations_are_audited()
    {
        using var t = new TempDir();
        var root = t.Sub("p");
        var audit = new MemoryAudit();
        Service(root, ProtectedPathRule.ReadOnly, audit).Evaluate(Path.Combine(root, "x"), PathOperation.Write, PathActor.Agent);
        Assert.Contains(audit.Events, e => e.Action == "ProtectedPathViolation" && e.Result == AuditResult.Blocked);
    }

    [Fact]
    public void Default_user_rules_protect_the_projects_tree_read_only()
    {
        var rule = Assert.Single(ProtectedPathService.DefaultUserRules());
        Assert.EndsWith(@"\Projects", rule.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(rule.Rules.HasFlag(ProtectedPathRule.ReadOnly));
        var svc = new ProtectedPathService([], null, null); // seeds default user rules when no store
        Assert.Equal(PathDecisionKind.Deny, svc.Evaluate(Path.Combine(rule.Path, "anything", "file.cs"), PathOperation.Write, PathActor.Agent).Kind);
    }
}
