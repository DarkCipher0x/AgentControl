using System.Runtime.InteropServices;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Security.Paths;
using DarkCipher.AgentControl.Security.Processes;
using DarkCipher.AgentControl.Security.Redaction;

namespace DarkCipher.AgentControl.Tests.Security;

public class ProcessExecutionTests : IDisposable
{
    private static readonly string Sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string Where = Path.Combine(Sys, "where.exe");
    private static readonly string Ping = Path.Combine(Sys, "PING.EXE");
    private readonly TempDir _tmp = new();
    private readonly MemoryAudit _audit = new();
    private readonly SecureProcessExecutionService _exec;
    private readonly ProtectedPathService _paths;

    public ProcessExecutionTests()
    {
        _paths = new ProtectedPathService([new ProtectedPathEntry(_tmp.Sub("noexec"), ProtectedPathRule.NoProcessExecution, "noexec", true)], null, _audit);
        _exec = new SecureProcessExecutionService(_paths, new Redactor(), _audit, null, new MemoryLog());
    }

    public void Dispose()
    {
        _exec.Dispose();
        _tmp.Dispose();
    }

    private Task<ProcessResult> Run(ProcessSpec s) => _exec.RunAsync(s, CancellationToken.None);

    [Fact]
    public async Task Bare_names_are_not_resolved_from_PATH()
    {
        var r = await Run(new ProcessSpec { Executable = "where.exe" });
        Assert.Equal(ProcessOutcome.Blocked, r.Outcome);
    }

    [Fact]
    public async Task Batch_and_script_files_are_refused()
    {
        var bat = Path.Combine(_tmp.Path, "x.bat");
        File.WriteAllText(bat, "@echo off");
        Assert.Equal(ProcessOutcome.Blocked, (await Run(new ProcessSpec { Executable = bat })).Outcome);
        var ps1 = Path.Combine(_tmp.Path, "x.ps1");
        File.WriteAllText(ps1, "");
        Assert.Equal(ProcessOutcome.Blocked, (await Run(new ProcessSpec { Executable = ps1 })).Outcome);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData(@"WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData("wscript.exe")]
    public async Task Shell_interpreters_are_refused(string relative)
    {
        var exe = Path.Combine(Sys, relative);
        if (!File.Exists(exe)) return;
        Assert.Equal(ProcessOutcome.Blocked, (await Run(new ProcessSpec { Executable = exe, Arguments = ["/c", "echo hi"] })).Outcome);
    }

    [Fact]
    public async Task Agents_and_extensions_cannot_opt_into_shells()
    {
        var r = await Run(new ProcessSpec { Executable = Path.Combine(Sys, "cmd.exe"), AllowShellInterpreter = true, Actor = PathActor.Extension });
        Assert.Equal(ProcessOutcome.Blocked, r.Outcome);
    }

    [Fact]
    public async Task Executables_in_temp_are_refused()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dcac-test-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(temp);
        try
        {
            var copy = Path.Combine(temp, "where.exe");
            File.Copy(Where, copy);
            Assert.Equal(ProcessOutcome.Blocked, (await Run(new ProcessSpec { Executable = copy })).Outcome);
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task Code_injection_and_substitution_variables_are_refused()
    {
        foreach (var name in new[] { "NODE_OPTIONS", "DOTNET_STARTUP_HOOKS", "PATH", "ComSpec", "bad name" })
        {
            var r = await Run(new ProcessSpec { Executable = Where, Arguments = ["where"], Environment = new Dictionary<string, string> { [name] = "x" } });
            Assert.Equal(ProcessOutcome.Blocked, r.Outcome);
        }
    }

    [Fact]
    public async Task Nul_in_arguments_is_refused()
    {
        var r = await Run(new ProcessSpec { Executable = Where, Arguments = ["a\0b"] });
        Assert.Equal(ProcessOutcome.Blocked, r.Outcome);
    }

    [Fact]
    public async Task Protected_working_directory_is_refused()
    {
        var r = await Run(new ProcessSpec { Executable = Where, Arguments = ["where"], WorkingDirectory = Path.Combine(_tmp.Path, "noexec") });
        Assert.Equal(ProcessOutcome.Blocked, r.Outcome);
        Assert.Contains(_audit.Events, e => e.Action == "ProcessBlocked");
    }

    [Fact]
    public async Task Runs_directly_and_captures_output()
    {
        var r = await Run(new ProcessSpec { Executable = Where, Arguments = ["where.exe"], Timeout = TimeSpan.FromSeconds(15) });
        Assert.True(r.Succeeded, r.Message + r.StdErr);
        Assert.Contains("where.exe", r.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Shell_metacharacters_are_passed_as_data_not_executed()
    {
        // If "&" were interpreted, 'where' would also try to run "calc"; with direct invocation it is just an odd pattern.
        var r = await Run(new ProcessSpec { Executable = Where, Arguments = ["nonexistent&calc"], Timeout = TimeSpan.FromSeconds(15) });
        Assert.Equal(ProcessOutcome.Failed, r.Outcome);
        Assert.DoesNotContain("calc.exe", r.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Timeout_terminates_the_process_tree()
    {
        var r = await Run(new ProcessSpec { Executable = Ping, Arguments = ["-n", "30", "127.0.0.1"], Timeout = TimeSpan.FromSeconds(1) });
        Assert.Equal(ProcessOutcome.TimedOut, r.Outcome);
        Assert.True(r.Duration < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_terminates_the_process()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var r = await _exec.RunAsync(new ProcessSpec { Executable = Ping, Arguments = ["-n", "30", "127.0.0.1"], Timeout = TimeSpan.FromSeconds(60) }, cts.Token);
        Assert.Equal(ProcessOutcome.Cancelled, r.Outcome);
    }

    [Fact]
    public void Duplicate_launches_are_refused_and_stop_only_touches_owned_processes()
    {
        var spec = new ProcessSpec { Executable = Ping, Arguments = ["-n", "30", "127.0.0.1"], DuplicateKey = "dup-test" };
        var first = _exec.Start(spec);
        Assert.True(first.Success, first.Message);
        var second = _exec.Start(spec);
        Assert.False(second.Success);
        Assert.False(_exec.Stop("not-ours"));
        Assert.True(_exec.Stop(first.Process!.Id));
    }

    [Fact]
    public void Background_helpers_survive_the_app_and_are_refused_for_agents()
    {
        Assert.False(_exec.Start(new ProcessSpec { Executable = Ping, Arguments = ["-n", "2", "127.0.0.1"], Mode = ProcessMode.Background, Actor = PathActor.Agent }).Success);
        Assert.Contains(_audit.Events, e => e.Action == "ProcessBlocked");

        // A separate executor stands in for the app: disposing it must not kill a background helper (Ollama keeps running).
        var app = new SecureProcessExecutionService(_paths, new Redactor(), _audit, null, new MemoryLog());
        var bg = app.Start(new ProcessSpec { Executable = Ping, Arguments = ["-n", "30", "127.0.0.1"], Mode = ProcessMode.Background, Purpose = "test helper" });
        Assert.True(bg.Success, bg.Message);
        var captured = app.Start(new ProcessSpec { Executable = Ping, Arguments = ["-n", "30", "127.0.0.1"] });
        Assert.True(captured.Success, captured.Message);
        app.Dispose();
        using var helper = System.Diagnostics.Process.GetProcessById(bg.Process!.ProcessId);
        try
        {
            Thread.Sleep(500);
            Assert.False(helper.HasExited);
            Assert.True(ProcessGone(captured.Process!.ProcessId));
        }
        finally { helper.Kill(); }
    }

    private static bool ProcessGone(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.WaitForExit(3000); }
        catch (ArgumentException) { return true; }
    }

    [Fact]
    public async Task Output_is_bounded()
    {
        var r = await Run(new ProcessSpec { Executable = Ping, Arguments = ["-n", "3", "127.0.0.1"], MaxOutputChars = 40, Timeout = TimeSpan.FromSeconds(20) });
        Assert.True(r.StdOut.Length <= 40);
        Assert.True(r.OutputTruncated);
    }

    [Fact]
    public void Preview_never_shows_secret_environment_values()
    {
        var p = _exec.Preview(new ProcessSpec
        {
            Executable = Where,
            Arguments = ["a b", "c\"d"],
            SecretEnvironment = new Dictionary<string, string> { ["HF_TOKEN"] = "cred123" },
            Environment = new Dictionary<string, string> { ["X"] = "token=hf_abcdefghijklmnopqrstuvwxyz0123" },
        });
        Assert.Contains("HF_TOKEN=<credential>", p, StringComparison.Ordinal);
        Assert.DoesNotContain("hf_abcdefghij", p, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("with space")]
    [InlineData("quote\"inside")]
    [InlineData("trailing\\")]
    [InlineData("back\\\\slashes \\\"mixed\\")]
    [InlineData("")]
    [InlineData("a&b|c<d>e^f")]
    public void Windows_quoting_round_trips_through_CommandLineToArgvW(string arg)
    {
        var line = CommandLine.Render(@"C:\x\prog.exe", [arg]);
        var argv = Parse(line);
        Assert.Equal(2, argv.Length);
        Assert.Equal(arg, argv[1]);
    }

    private static string[] Parse(string commandLine)
    {
        var ptr = CommandLineToArgvW(commandLine, out var count);
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(ptr); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
