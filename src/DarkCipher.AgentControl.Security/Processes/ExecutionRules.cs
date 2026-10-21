using System.Text;
using System.Text.RegularExpressions;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Security.Paths;

namespace DarkCipher.AgentControl.Security.Processes;

/// <summary>Executable location rules. Only real PE images (.exe/.com) at absolute, canonical, non-temporary,
/// non-network locations may be started. Batch files are refused: cmd.exe re-parses their arguments, which makes safe
/// quoting impossible (the "BatBadBut" class of injection).</summary>
public static class ExecutableRules
{
    private static readonly string[] AllowedExtensions = [".exe", ".com"];

    /// <summary>Command interpreters and script hosts. They re-parse their arguments (so list-based quoting no longer
    /// protects against injection) and can run arbitrary code; they are refused unless application code explicitly
    /// opts in (never available to extensions or agents).</summary>
    public static readonly string[] ShellInterpreters =
    [
        "cmd.exe", "powershell.exe", "powershell_ise.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe",
        "bash.exe", "sh.exe", "wsl.exe", "wslhost.exe", "rundll32.exe", "regsvr32.exe", "msbuild.exe", "installutil.exe", "forfiles.exe",
    ];

    public static string Validate(string executable, IProtectedPathService paths, PathActor actor, bool allowShellInterpreter = false)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw new UnsafeProcessException("No program was specified.");
        if (!Path.IsPathFullyQualified(executable.Replace('/', '\\')))
            throw new UnsafeProcessException("Programs must be given as a full path; bare names are not searched for on PATH at launch time.");

        string canonical;
        try { canonical = PathCanonicalizer.Canonicalize(executable); }
        catch (UnsafePathException ex) { throw new UnsafeProcessException(ex.Message); }

        var ext = Path.GetExtension(canonical);
        if (!AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new UnsafeProcessException($"Only .exe programs can be started ('{ext}' refused). Scripts and batch files are never run directly.");
        if (!File.Exists(canonical)) throw new UnsafeProcessException("The program does not exist: " + canonical);
        if (ShellInterpreters.Contains(Path.GetFileName(canonical), StringComparer.OrdinalIgnoreCase) && (!allowShellInterpreter || actor != PathActor.Application))
            throw new UnsafeProcessException($"'{Path.GetFileName(canonical)}' is a command interpreter; programs are always started directly, never through a shell.");

        var temp = SafeCanon(Path.GetTempPath());
        if (temp is not null && PathCanonicalizer.IsSameOrUnder(canonical, temp))
            throw new UnsafeProcessException("Programs in the temporary folder are never started.");
        var downloads = SafeCanon(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        if (downloads is not null && PathCanonicalizer.IsSameOrUnder(canonical, downloads))
            throw new UnsafeProcessException("Programs in the Downloads folder are never started. Install them first.");

        var decision = paths.Evaluate(canonical, PathOperation.Execute, actor);
        if (decision.Kind != PathDecisionKind.Allow) throw new UnsafeProcessException(decision.Reason);
        return canonical;
    }

    private static string? SafeCanon(string p)
    {
        try { return PathCanonicalizer.Canonicalize(p); } catch (UnsafePathException) { return null; }
    }
}

/// <summary>Environment variable rules for child processes.</summary>
public static partial class EnvironmentRules
{
    /// <summary>Variables that inject code into a child process (loader hooks, profilers, interpreter start-up files).
    /// They are stripped from the inherited block and may never be set by a launch request.</summary>
    public static readonly string[] CodeInjectionVariables =
    [
        "NODE_OPTIONS", "NODE_REPL_EXTERNAL_MODULE", "DOTNET_STARTUP_HOOKS", "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH",
        "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH", "PYTHONSTARTUP", "PYTHONINSPECT", "PERL5OPT",
        "RUBYOPT", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "LD_PRELOAD", "DYLD_INSERT_LIBRARIES",
    ];

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static void ValidateName(string name)
    {
        if (!NamePattern().IsMatch(name)) throw new UnsafeProcessException($"Environment variable name '{name}' is not allowed.");
        if (CodeInjectionVariables.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new UnsafeProcessException($"Environment variable '{name}' can inject code into the program and is never set.");
        if (string.Equals(name, "PATH", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "PATHEXT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ComSpec", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "SystemRoot", StringComparison.OrdinalIgnoreCase))
            throw new UnsafeProcessException($"Changing '{name}' is not allowed (program substitution risk).");
    }

    public static void ValidateValue(string name, string value)
    {
        if (value.IndexOf('\0') >= 0) throw new UnsafeProcessException($"The value of '{name}' contains a NUL character.");
        if (value.Length > 8192) throw new UnsafeProcessException($"The value of '{name}' is too long.");
    }
}

/// <summary>Windows command-line rendering for DISPLAY ONLY (the actual launch uses ProcessStartInfo.ArgumentList,
/// which applies the same CommandLineToArgvW-compatible quoting).</summary>
public static class CommandLine
{
    public static string Quote(string arg) => Domain.Processes.ProcessPreview.Quote(arg);

    public static string Render(string exe, IEnumerable<string> args) => Domain.Processes.ProcessPreview.Render(exe, args);
}

public sealed class UnsafeProcessException(string message) : Exception(message);
