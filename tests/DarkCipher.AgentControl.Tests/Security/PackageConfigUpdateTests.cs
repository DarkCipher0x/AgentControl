using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Security.Configuration;
using DarkCipher.AgentControl.Security.Packages;
using DarkCipher.AgentControl.Security.Paths;
using DarkCipher.AgentControl.Security.Updates;

namespace DarkCipher.AgentControl.Tests.Security;

public class PackageVerificationTests
{
    private readonly PackageVerificationService _v = new(new MemoryAudit());

    private static string Zip(TempDir t, params (string Name, byte[] Data)[] entries)
    {
        var path = Path.Combine(t.Path, Guid.NewGuid().ToString("N")[..6] + ".zip");
        using var fs = File.Create(path);
        using var z = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            var e = z.CreateEntry(name, CompressionLevel.Optimal);
            using var s = e.Open();
            s.Write(data);
        }
        return path;
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData("C:/x.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("dir/CON.txt")]
    public void Rejects_escaping_and_dangerous_entry_names(string name)
    {
        using var t = new TempDir();
        var zip = Zip(t, (name, B("x")));
        var dest = Path.Combine(t.Path, "out");
        var r = _v.ExtractZipSafely(zip, dest, new ExtractionLimits());
        Assert.False(r.Success);
        Assert.False(File.Exists(Path.Combine(t.Path, "evil.txt")));
        Assert.False(Directory.Exists(dest)); // cleaned up
    }

    [Fact]
    public void Rejects_symlink_entries()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "link.zip");
        using (var fs = File.Create(path))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("link");
            e.ExternalAttributes = unchecked((int)(0xA1FFu << 16)); // S_IFLNK | 0777
            using var s = e.Open();
            s.Write(B("/etc/passwd"));
        }
        Assert.False(_v.ExtractZipSafely(path, Path.Combine(t.Path, "out"), new ExtractionLimits()).Success);
    }

    [Fact]
    public void Rejects_zip_bombs_by_actual_bytes_and_ratio()
    {
        using var t = new TempDir();
        var zip = Zip(t, ("big.bin", new byte[30 * 1024 * 1024]));
        var r = _v.ExtractZipSafely(zip, Path.Combine(t.Path, "out"), new ExtractionLimits(MaxTotalBytes: 200L * 1024 * 1024, MaxCompressionRatio: 50));
        Assert.False(r.Success);
        Assert.Contains("ratio", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_too_many_entries_and_oversize_totals()
    {
        using var t = new TempDir();
        var many = Zip(t, Enumerable.Range(0, 20).Select(i => ($"f{i}.txt", B("x"))).ToArray());
        Assert.False(_v.ExtractZipSafely(many, Path.Combine(t.Path, "o1"), new ExtractionLimits(MaxEntries: 10)).Success);
        var big = Zip(t, ("a.txt", B(new string('a', 5000))));
        Assert.False(_v.ExtractZipSafely(big, Path.Combine(t.Path, "o2"), new ExtractionLimits(MaxTotalBytes: 1000)).Success);
    }

    [Fact]
    public void Refuses_non_empty_destination()
    {
        using var t = new TempDir();
        var dest = t.Sub("dest");
        File.WriteAllText(Path.Combine(dest, "existing.txt"), "x");
        Assert.False(_v.ExtractZipSafely(Zip(t, ("a.txt", B("x"))), dest, new ExtractionLimits()).Success);
        Assert.True(File.Exists(Path.Combine(dest, "existing.txt")));
    }

    [Fact]
    public void Extracts_a_normal_archive()
    {
        using var t = new TempDir();
        var r = _v.ExtractZipSafely(Zip(t, ("a.txt", B("x")), ("dir/b.json", B("{}"))), Path.Combine(t.Path, "ok"), new ExtractionLimits());
        Assert.True(r.Success, r.Message);
        Assert.Equal(2, r.Files.Count);
    }

    [Fact]
    public void Sha256_verification()
    {
        using var t = new TempDir();
        var f = Path.Combine(t.Path, "x.bin");
        File.WriteAllText(f, "hello");
        var good = Convert.ToHexString(SHA256.HashData(B("hello")));
        Assert.True(_v.VerifySha256(f, good));
        Assert.False(_v.VerifySha256(f, new string('0', 64)));
        Assert.False(_v.VerifySha256(f, "short"));
    }

    [Fact]
    public void Authenticode_reports_unsigned_and_signed_files()
    {
        using var t = new TempDir();
        var unsigned = Path.Combine(t.Path, "u.exe");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0, 0]);
        Assert.NotEqual(SignatureStatus.Valid, _v.CheckAuthenticode(unsigned));
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (File.Exists(notepad)) Assert.NotEqual(SignatureStatus.Invalid, _v.CheckAuthenticode(notepad)); // catalog-signed system files
    }
}

public class ConfigurationProtectionTests
{
    private static (ConfigurationProtectionService Svc, TempDir T, string Target) Make(ProtectedPathRule? protect = null)
    {
        var t = new TempDir();
        var cfgDir = t.Sub("agent");
        var entries = protect is { } r ? new[] { new ProtectedPathEntry(cfgDir, r, "protected", true) } : [];
        var svc = new ConfigurationProtectionService(new ProtectedPathService(entries, null, null), new MemoryAudit(), t.Sub("backups"));
        return (svc, t, Path.Combine(cfgDir, "config.toml"));
    }

    [Fact]
    public void Preview_apply_backup_and_rollback()
    {
        var (svc, t, target) = Make();
        using var _ = t;
        File.WriteAllText(target, "model = \"a\"\nkeep = 1\n");
        var preview = svc.Preview(target, "model = \"b\"\nkeep = 1\n");
        Assert.Contains("- model = \"a\"", preview.DiffLines);
        Assert.Contains("+ model = \"b\"", preview.DiffLines);
        Assert.Contains("  keep = 1", preview.DiffLines);

        var applied = svc.Apply(preview, CorrelationId.New());
        Assert.True(applied.Success, applied.Message);
        Assert.Contains("\"b\"", File.ReadAllText(target), StringComparison.Ordinal);

        Assert.True(svc.Rollback(applied.Value!, CorrelationId.New()).Success);
        Assert.Contains("\"a\"", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.NotEmpty(svc.Backups(target));
    }

    [Fact]
    public void Stale_preview_is_refused()
    {
        var (svc, t, target) = Make();
        using var _ = t;
        File.WriteAllText(target, "v1");
        var preview = svc.Preview(target, "v2");
        File.WriteAllText(target, "changed by someone else");
        var r = svc.Apply(preview, CorrelationId.New());
        Assert.False(r.Success);
        Assert.Equal("changed by someone else", File.ReadAllText(target));
    }

    [Fact]
    public void Protected_targets_are_never_written()
    {
        var (svc, t, target) = Make(ProtectedPathRule.ReadOnly);
        using var _ = t;
        var r = svc.Apply(svc.Preview(target, "x"), CorrelationId.New());
        Assert.False(r.Success);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Atomic_write_leaves_no_temp_files()
    {
        using var t = new TempDir();
        var f = Path.Combine(t.Path, "a.json");
        ConfigurationProtectionService.AtomicWrite(f, "1");
        ConfigurationProtectionService.AtomicWrite(f, "2");
        Assert.Equal("2", File.ReadAllText(f));
        Assert.Single(Directory.GetFiles(t.Path));
    }
}

public class UpdateSecurityTests
{
    private static (UpdateSecurityService Svc, ECDsa Key) Make()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (new UpdateSecurityService(new MemoryAudit(), key.ExportSubjectPublicKeyInfoPem()), key);
    }

    private static string Manifest(ECDsa key, string version = "0.2.0", string url = "https://darkciphersystems.com/downloads/agentcontrol/x.msi", Action<UpdateManifest>? tamper = null)
    {
        var m = new UpdateManifest(UpdateSecurityService.ProductName, version, url, new string('a', 64), DateTimeOffset.Parse("2026-10-01T00:00:00Z"), "");
        var sig = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(UpdateSecurityService.CanonicalPayload(m)), HashAlgorithmName.SHA256));
        m = m with { Signature = sig };
        return System.Text.Json.JsonSerializer.Serialize(m);
    }

    [Fact]
    public void Refuses_everything_without_a_trusted_key()
    {
        var svc = new UpdateSecurityService(new MemoryAudit());
        Assert.False(svc.UpdatesEnabled);
        Assert.False(svc.Verify("{}", new Version(0, 1, 0)).Accepted);
    }

    [Fact]
    public void Accepts_a_correctly_signed_newer_version()
    {
        var (svc, key) = Make();
        Assert.True(svc.Verify(Manifest(key), new Version(0, 1, 0)).Accepted);
    }

    [Fact]
    public void Refuses_downgrades_and_same_version()
    {
        var (svc, key) = Make();
        Assert.False(svc.Verify(Manifest(key, "0.1.0"), new Version(0, 1, 0)).Accepted);
        Assert.False(svc.Verify(Manifest(key, "0.0.9"), new Version(0, 1, 0)).Accepted);
    }

    [Fact]
    public void Refuses_tampered_manifests_and_foreign_hosts()
    {
        var (svc, key) = Make();
        var json = Manifest(key).Replace("0.2.0", "9.9.9", StringComparison.Ordinal);
        Assert.False(svc.Verify(json, new Version(0, 1, 0)).Accepted);
        Assert.False(svc.Verify(Manifest(key, url: "https://evil.example/x.msi"), new Version(0, 1, 0)).Accepted);
        Assert.False(svc.Verify(Manifest(key, url: "http://darkciphersystems.com/x.msi"), new Version(0, 1, 0)).Accepted);
        var (_, otherKey) = Make();
        Assert.False(svc.Verify(Manifest(otherKey), new Version(0, 1, 0)).Accepted);
    }
}
