using System.Text.RegularExpressions;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Processes;

namespace DarkCipher.AgentControl.Integrations.Tools;

/// <summary>
/// The Windows Package Manager, used as the ONLY installation mechanism for built-in tools. Every call names the exact
/// package id (--exact) and the reviewed community source (--source winget), runs non-interactively, and goes through
/// the secure process executor (full path, argument list, no shell). winget downloads the installer named in the
/// reviewed manifest and refuses it if its SHA-256 does not match the manifest.
/// winget has no structured output for "show", so the "Version:" line is read and validated against a strict pattern.
/// </summary>
public sealed partial class WingetClient
{
    private readonly IProcessExecutor _processes;
    private readonly Func<string?> _locate;

    public WingetClient(IProcessExecutor processes, Func<string?>? locate = null)
    {
        _processes = processes;
        _locate = locate ?? Locate;
    }

    /// <summary>winget's app execution alias (installed with App Installer), or winget.exe on PATH.</summary>
    public static string? Locate()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : Common.ExecutableLocator.FindOnPath("winget.exe");
    }

    public string? ExecutablePath => _locate();

    public static IReadOnlyList<string> CommonFlags { get; } = ["--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity"];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{1,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdPattern();

    // Optional leading "v" or "b" for packages that tag releases that way (GitHub.Copilot v1.0.100, ggml.llamacpp b11723).
    [GeneratedRegex(@"^[vb]?[0-9][0-9A-Za-z.\-+]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static bool IsValidPackageId(string? id) => id is not null && !id.Contains("..", StringComparison.Ordinal) && PackageIdPattern().IsMatch(id);
    public static bool IsValidVersion(string? v) => v is not null && VersionPattern().IsMatch(v);

    public static IReadOnlyList<string> ShowArguments(string id) => ["show", "--id", Require(id), .. CommonFlags];

    public static IReadOnlyList<string> InstallArguments(string id, string? version, bool force = false)
    {
        var args = new List<string> { "install", "--id", Require(id) };
        if (version is not null) { if (!IsValidVersion(version)) throw new ArgumentException("Invalid version."); args.Add("--version"); args.Add(version); }
        args.AddRange(CommonFlags);
        args.AddRange(["--scope", "user", "--silent", "--accept-package-agreements"]);
        if (force) args.Add("--force");
        return args;
    }

    public static IReadOnlyList<string> UpgradeArguments(string id) =>
        ["upgrade", "--id", Require(id), .. CommonFlags, "--silent", "--accept-package-agreements"];

    public static IReadOnlyList<string> UninstallArguments(string id) => ["uninstall", "--id", Require(id), "--exact", "--source", "winget", "--silent", "--disable-interactivity"];

    private static string Require(string id) => IsValidPackageId(id) ? id : throw new ArgumentException($"'{id}' is not a valid winget package id.");

    /// <summary>The newest version the source offers, or null (winget missing, offline, unknown package).</summary>
    public async Task<string?> AvailableVersionAsync(string packageId, CancellationToken ct)
    {
        var r = await RunAsync(ShowArguments(packageId), TimeSpan.FromMinutes(2), $"Look up {packageId} with winget", ct).ConfigureAwait(false);
        return r.Succeeded ? ParseShowVersion(r.StdOut) : null;
    }

    /// <summary>Installed version according to winget's own list (used for tools whose executable name is unknown).</summary>
    public async Task<string?> InstalledVersionAsync(string packageId, CancellationToken ct)
    {
        var r = await RunAsync(["list", "--id", Require(packageId), .. CommonFlags], TimeSpan.FromMinutes(2), $"Check {packageId} with winget", ct).ConfigureAwait(false);
        return r.Succeeded ? ParseListVersion(r.StdOut, packageId) : null;
    }

    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private (DateTimeOffset At, string Output)? _snapshot;
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Installed version from ONE shared "winget list --source winget" snapshot (cached for a minute), so a catalogue with
    /// many winget entries costs one winget call instead of one per tool. Null when the id is not in the list or winget is
    /// unavailable. Installs, updates and uninstalls invalidate the snapshot.
    /// </summary>
    public async Task<string?> InstalledVersionFromSnapshotAsync(string packageId, CancellationToken ct)
    {
        Require(packageId);
        await _snapshotGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_snapshot is not { } s || DateTimeOffset.UtcNow - s.At > SnapshotLifetime)
            {
                var r = await RunAsync(["list", "--source", "winget", "--accept-source-agreements", "--disable-interactivity"], TimeSpan.FromMinutes(2),
                    "List packages installed from the winget source", ct).ConfigureAwait(false);
                // winget list exits non-zero when nothing matches; the table is still the answer.
                _snapshot = (DateTimeOffset.UtcNow, r.Outcome is ProcessOutcome.Completed or ProcessOutcome.Failed ? r.StdOut : "");
            }
            return ParseListVersion(_snapshot.Value.Output, packageId);
        }
        finally { _snapshotGate.Release(); }
    }

    public void InvalidateSnapshot() => _snapshot = null;

    /// <summary>winget list prints a table (Name, Id, Version, Available, Source); the row whose Id matches is read.</summary>
    public static string? ParseListVersion(string output, string packageId)
    {
        foreach (var raw in output.Split('\n'))
        {
            var cells = Regex.Split(raw.Trim(), @"\s{2,}", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            var i = Array.FindIndex(cells, c => string.Equals(c, packageId, StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && i + 1 < cells.Length && IsValidVersion(cells[i + 1])) return cells[i + 1];
        }
        return null;
    }

    public static string? ParseShowVersion(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)) continue;
            var v = line["Version:".Length..].Trim();
            return IsValidVersion(v) ? v : null;
        }
        return null;
    }

    public async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, string purpose, CancellationToken ct)
    {
        var exe = _locate();
        if (exe is null)
            return new ProcessResult(ProcessOutcome.StartFailed, null, "", "", TimeSpan.Zero, "winget (App Installer) is not available on this PC.");
        return await _processes.RunAsync(new ProcessSpec
        {
            Executable = exe,
            Arguments = arguments,
            Timeout = timeout,
            Purpose = purpose,
            MaxOutputChars = 512 * 1024,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>A short, user-safe explanation of a winget result (known HRESULTs, else the last output lines).</summary>
    public static string Explain(ProcessResult r)
    {
        if (r.Succeeded) return "winget finished successfully.";
        if (r.Outcome is ProcessOutcome.Blocked or ProcessOutcome.StartFailed or ProcessOutcome.TimedOut or ProcessOutcome.Cancelled) return r.Message;
        var known = unchecked((uint)(r.ExitCode ?? 0)) switch
        {
            0x8A150014 => "No package with that id was found in the winget source.",
            0x8A15002B => "No newer version is available.",
            0x8A150061 => "The package is already installed.",
            0x8A150011 => "The installer's hash did not match the manifest; winget refused to run it.",
            0x8A150010 => "The installer download failed.",
            0x8A150056 => "The installer was blocked by policy.",
            0x8A15010B => "The source agreement was not accepted.",
            _ => null,
        };
        var tail = string.Join(" ", (r.StdOut + "\n" + r.StdErr).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 2 && !l.All(c => c is '-' or '\\' or '|' or '/' or '█' or '▒' or ' ')).TakeLast(3));
        return (known ?? $"winget exited with code 0x{unchecked((uint)(r.ExitCode ?? 0)):X8}.") + (tail.Length > 0 ? " " + tail : "");
    }
}
