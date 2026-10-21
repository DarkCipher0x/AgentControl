using System.Text.RegularExpressions;
using DarkCipher.AgentControl.Security.Native;

namespace DarkCipher.AgentControl.Security.Paths;

/// <summary>
/// Produces one canonical spelling for a Windows path so that protection rules cannot be bypassed with alternate forms:
/// relative segments and "..", forward slashes, case, 8.3 short names, "\\?\" and "\\?\UNC\" prefixes, symlinks,
/// junctions and other reparse points (resolved through the OS for every existing ancestor). Device paths, alternate
/// data streams and reserved device names are rejected outright.
/// </summary>
public static partial class PathCanonicalizer
{
    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$"];

    [GeneratedRegex(@"^[A-Za-z]:\\", RegexOptions.CultureInvariant)]
    private static partial Regex DriveRooted();

    /// <summary>Canonicalises an absolute path. Relative input is refused: callers must resolve against an explicit base.</summary>
    public static string Canonicalize(string path)
    {
        var p = PreValidate(path);
        if (!Path.IsPathFullyQualified(p))
            throw new UnsafePathException("Relative paths are not accepted here; choose a full folder path.");
        return CanonicalizeQualified(p);
    }

    /// <summary>Canonicalises a possibly-relative path against an explicit, trusted base directory.</summary>
    public static string Canonicalize(string path, string baseDirectory)
    {
        var p = PreValidate(path);
        if (!Path.IsPathFullyQualified(p))
            p = Path.Combine(Canonicalize(baseDirectory), p);
        return CanonicalizeQualified(p);
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> or inside it. Both must be canonical.
    /// Segment-aware: "C:\Projects2" is NOT inside "C:\Projects".</summary>
    public static bool IsSameOrUnder(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return true;
        var r = root.EndsWith('\\') ? root : root + "\\";
        return candidate.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNetworkPath(string canonical)
    {
        if (canonical.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            var root = Path.GetPathRoot(canonical);
            return root is not null && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException) { return false; }
    }

    public static bool IsRemovablePath(string canonical)
    {
        try
        {
            var root = Path.GetPathRoot(canonical);
            if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var t = new DriveInfo(root).DriveType;
            return t is DriveType.Removable or DriveType.CDRom;
        }
        catch (ArgumentException) { return false; }
    }

    public static bool IsDriveRoot(string canonical)
    {
        var root = Path.GetPathRoot(canonical);
        return root is not null && string.Equals(root.TrimEnd('\\'), canonical.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static string PreValidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new UnsafePathException("The path is empty.");
        if (path.Length > 32000) throw new UnsafePathException("The path is too long.");
        if (path.IndexOf('\0') >= 0) throw new UnsafePathException("The path contains a NUL character.");
        foreach (var c in path)
            if (c < 0x20) throw new UnsafePathException("The path contains control characters.");

        var p = path.Trim().Replace('/', '\\');

        // Win32 file namespace prefixes: strip to the ordinary form so they are compared the same way.
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        else if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];

        // Device namespace (\\.\PhysicalDrive0, \\.\pipe\...) and GLOBALROOT are never valid project/data paths.
        if (p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            throw new UnsafePathException("Device paths are not accepted.");
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) // e.g. \\?\Volume{guid}\ after one strip
            throw new UnsafePathException("Volume GUID paths are not accepted.");

        // Alternate data streams: a ':' anywhere except the drive letter position.
        var colon = p.IndexOf(':', DriveRooted().IsMatch(p) ? 2 : 0);
        if (colon >= 0) throw new UnsafePathException("Alternate data streams or extra ':' characters are not accepted.");

        foreach (var segment in p.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment.TrimEnd(' ', '.');
            var stem = name.Split('.')[0];
            if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
                throw new UnsafePathException($"'{segment}' is a reserved Windows device name.");
            if (segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
                throw new UnsafePathException("The path contains characters Windows does not allow in names.");
        }
        return p;
    }

    private static string CanonicalizeQualified(string p)
    {
        string full;
        try { full = Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new UnsafePathException("The path could not be resolved: " + ex.Message); }

        // Windows silently drops trailing dots/spaces from names; normalise them so "Projects." == "Projects".
        var parts = full.Split('\\');
        for (var i = 1; i < parts.Length; i++) parts[i] = parts[i].TrimEnd(' ', '.');
        full = string.Join('\\', parts);

        // Walk up to the deepest existing ancestor, resolve it through the OS, then re-append the missing tail.
        var tail = new Stack<string>();
        var current = full.TrimEnd('\\');
        if (current.Length == 2 && current[1] == ':') current += "\\";
        while (!Directory.Exists(current) && !File.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;
            tail.Push(Path.GetFileName(current));
            current = parent;
        }

        var resolved = ResolveExisting(current) ?? current;
        foreach (var t in tail) resolved = Path.Combine(resolved, t);
        if (resolved.Length > 3) resolved = resolved.TrimEnd('\\');
        return resolved;
    }

    /// <summary>Opens the existing file/directory and asks the OS for its final, normalised name (follows every reparse
    /// point and expands 8.3 names). Returns null when the handle cannot be opened (e.g. access denied).</summary>
    private static string? ResolveExisting(string existing)
    {
        using var handle = NativeMethods.CreateFileW(existing, NativeMethods.FILE_READ_ATTRIBUTES, NativeMethods.FILE_SHARE_ALL,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid) return LongName(existing);

        var buffer = new char[1024];
        var len = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, NativeMethods.FILE_NAME_NORMALIZED | NativeMethods.VOLUME_NAME_DOS);
        if (len > buffer.Length)
        {
            buffer = new char[len + 1];
            len = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, NativeMethods.FILE_NAME_NORMALIZED | NativeMethods.VOLUME_NAME_DOS);
        }
        if (len == 0) return LongName(existing);

        var final = new string(buffer, 0, (int)len);
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) final = @"\\" + final[8..];
        else if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
        return final;
    }

    private static string? LongName(string path)
    {
        var buffer = new char[1024];
        var len = NativeMethods.GetLongPathNameW(path, buffer, (uint)buffer.Length);
        return len == 0 || len > buffer.Length ? null : new string(buffer, 0, (int)len);
    }
}

public sealed class UnsafePathException(string message) : Exception(message);
