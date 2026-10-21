using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Security.Native;
using DarkCipher.AgentControl.Security.Paths;

namespace DarkCipher.AgentControl.Security.Packages;

/// <summary>
/// Integrity and archive safety. Extraction refuses: absolute or rooted entry names, drive letters and ':' (alternate
/// data streams), ".." traversal, reserved device names, symbolic-link entries, duplicate entries, any target that
/// canonicalises outside the destination, more entries/bytes than the limits allow, and suspicious compression ratios
/// (zip bombs; actual decompressed bytes are counted, header sizes are not trusted). It extracts only into a new,
/// empty directory and removes everything it wrote if any entry fails.
/// </summary>
public sealed class PackageVerificationService : IPackageVerifier
{
    private readonly IAuditLogger? _audit;

    public PackageVerificationService(IAuditLogger? audit) => _audit = audit;

    public string ComputeSha256(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public bool VerifySha256(string filePath, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex) || expectedHex.Length != 64) return false;
        var actual = ComputeSha256(filePath);
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expectedHex.Trim()));
    }

    public ExtractionResult ExtractZipSafely(string zipPath, string destinationDirectory, ExtractionLimits limits)
    {
        string dest;
        try
        {
            if (Directory.Exists(destinationDirectory) && Directory.EnumerateFileSystemEntries(destinationDirectory).Any())
                return Fail("The destination folder must be new and empty.", zipPath);
            Directory.CreateDirectory(destinationDirectory);
            dest = PathCanonicalizer.Canonicalize(destinationDirectory);
        }
        catch (Exception ex) when (ex is UnsafePathException or IOException or UnauthorizedAccessException)
        {
            return Fail("Invalid destination: " + ex.Message, zipPath);
        }

        var written = new List<string>();
        try
        {
            var compressedSize = new FileInfo(zipPath).Length;
            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > limits.MaxEntries) return Cleanup(written, dest, $"The archive has too many entries ({archive.Entries.Count}).", zipPath);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var problem = CheckEntryName(name);
                if (problem is not null) return Cleanup(written, dest, $"Unsafe entry '{entry.FullName}': {problem}", zipPath);

                // Unix symlink bit in the external attributes (S_IFLNK = 0xA000 in the high word).
                var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixMode == 0xA000) return Cleanup(written, dest, $"Symbolic link entries are not allowed ('{entry.FullName}').", zipPath);

                var target = Path.GetFullPath(Path.Combine(dest, name.Replace('/', '\\')));
                if (!PathCanonicalizer.IsSameOrUnder(target, dest)) return Cleanup(written, dest, $"Entry '{entry.FullName}' escapes the destination.", zipPath);
                if (!seen.Add(target)) return Cleanup(written, dest, $"Duplicate entry '{entry.FullName}'.", zipPath);

                if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                if (entry.Length > limits.MaxEntryBytes) return Cleanup(written, dest, $"Entry '{entry.FullName}' is too large.", zipPath);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // Re-check the parent after creation: a directory junction cannot come from a zip, but be certain.
                if (!PathCanonicalizer.IsSameOrUnder(PathCanonicalizer.Canonicalize(Path.GetDirectoryName(target)!), dest))
                    return Cleanup(written, dest, "A folder resolved outside the destination.", zipPath);

                using (var input = entry.Open())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    written.Add(target);
                    var buffer = new byte[81920];
                    long entryBytes = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        entryBytes += read;
                        total += read;
                        if (entryBytes > limits.MaxEntryBytes || total > limits.MaxTotalBytes)
                            return Cleanup(written, dest, "The archive expands beyond the size limit.", zipPath);
                        if (compressedSize > 0 && total / (double)compressedSize > limits.MaxCompressionRatio && total > 10 * 1024 * 1024)
                            return Cleanup(written, dest, "The archive's compression ratio is suspicious (possible zip bomb).", zipPath);
                        output.Write(buffer, 0, read);
                    }
                }
            }
            return new ExtractionResult(true, $"Extracted {written.Count} files.", written);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or UnsafePathException)
        {
            return Cleanup(written, dest, "The archive could not be read safely: " + ex.Message, zipPath);
        }
    }

    internal static string? CheckEntryName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "empty name";
        if (name.Length > 260) return "name too long";
        if (name.StartsWith('/') || name.StartsWith("//", StringComparison.Ordinal)) return "absolute path";
        if (name.Contains(':')) return "drive letter or alternate data stream";
        if (name.IndexOf('\0') >= 0 || name.Any(c => c < 0x20)) return "control characters";
        foreach (var seg in name.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg is "." or "..") return "relative traversal";
            var trimmed = seg.TrimEnd(' ', '.');
            if (trimmed.Length == 0) return "invalid segment";
            var stem = trimmed.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3])))
                return "reserved device name";
            if (seg.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) return "invalid characters";
        }
        return null;
    }

    private ExtractionResult Cleanup(List<string> written, string dest, string message, string zipPath)
    {
        foreach (var f in written) { try { File.Delete(f); } catch (IOException) { } }
        try { Directory.Delete(dest, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Fail(message, zipPath);
    }

    private ExtractionResult Fail(string message, string zipPath)
    {
        _audit?.Record(new AuditEvent("ArchiveRejected", "PackageVerificationService", AuditResult.Blocked, Path.GetFileName(zipPath), SecurityClassification.High, Detail: message));
        return new ExtractionResult(false, message, []);
    }

    public SignatureStatus CheckAuthenticode(string filePath)
    {
        var fileInfo = new NativeMethods.WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<NativeMethods.WINTRUST_FILE_INFO>(), pcwszFilePath = filePath };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new NativeMethods.WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<NativeMethods.WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE (offline-safe)
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 0,
                dwProvFlags = 0x00000080,  // WTD_CACHE_ONLY_URL_RETRIEVAL: never fetch over the network
            };
            var action = NativeMethods.WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var hr = NativeMethods.WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return unchecked((uint)hr) switch
            {
                0 => SignatureStatus.Valid,
                0x800B0100 => SignatureStatus.Unsigned,           // TRUST_E_NOSIGNATURE
                0x800B0109 or 0x800B0111 or 0x800B0004 => SignatureStatus.Untrusted, // untrusted root / explicit distrust / subject not trusted
                _ => SignatureStatus.Invalid,
            };
        }
        finally
        {
            Marshal.DestroyStructure<NativeMethods.WINTRUST_FILE_INFO>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }
}
