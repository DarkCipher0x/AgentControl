using System.Windows;
using System.Windows.Threading;
using DarkCipher.AgentControl.App.Composition;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.App.ViewModels;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace DarkCipher.AgentControl.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var safeMode = args.Contains("--safe-mode", StringComparer.OrdinalIgnoreCase);
        var selfTestIndex = Array.FindIndex(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));

        try
        {
            _services = Bootstrapper.Build(new BootOptions(safeMode, null, Headless: selfTestIndex >= 0,
                CredentialPrefix: selfTestIndex >= 0 ? "DarkCipher.AgentControl.SelfTest" : "DarkCipher.AgentControl"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (selfTestIndex < 0)
                MessageBox.Show("DarkCipher AgentControl could not start:\n\n" + ex.Message, "DarkCipher AgentControl", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        var log = _services.GetRequiredService<IAppLogger>();
        _services.GetRequiredService<DarkCipher.AgentControl.Application.Downloads.DownloadManager>().SetDispatcher(Ui.OnUi);
        HookErrors(log, interactive: selfTestIndex < 0);
        // Theme, typography, layout and icon before the first window exists (the self-test renders with them too).
        _services.GetRequiredService<Appearance.AppearanceCoordinator>().Install(followSystem: selfTestIndex < 0);

        if (selfTestIndex >= 0)
        {
            var report = selfTestIndex + 1 < args.Length ? args[selfTestIndex + 1] : Path.Combine(Path.GetTempPath(), "dcac-selftest");
            var code = await SelfTest.RunAsync(_services, report);
            Shutdown(code);
            return;
        }

        log.Info("App", $"DarkCipher AgentControl {Bootstrapper.AppVersion} starting{(safeMode ? " in Safe Mode" : "")}.");
        var window = new MainWindow(_services.GetRequiredService<MainViewModel>(), _services.GetRequiredService<Appearance.ThemeService>());
        MainWindow = window;
        window.Show();

        // Extensions load after the window is up; failures are recorded per extension and never block start-up.
        try { await _services.GetRequiredService<ExtensionHost>().StartAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { log.Error("Extensions", "Extension start-up failed.", ex); }
    }

    private void HookErrors(IAppLogger log, bool interactive)
    {
        ErrorReporter.Sink = (context, ex) =>
        {
            var correlation = CorrelationId.New();
            log.Error("UI", $"{context} [ref {correlation}]", ex, correlation);
            if (interactive)
                Ui.OnUi(() => Dialogs.Error(ErrorReport.FromException(context, Navigator.CurrentTitle ?? "Interface", ex, correlation.Value)));
        };
        DispatcherUnhandledException += (_, e) =>
        {
            var correlation = CorrelationId.New();
            log.Log(LogLevel.Critical, "UI", $"Unhandled UI exception [ref {correlation}]", correlation, e.Exception);
            if (interactive)
                Dialogs.Error(ErrorReport.FromException("An unexpected error stopped this action", Navigator.CurrentTitle ?? "Interface", e.Exception, correlation.Value) with
                {
                    UserDataAffected = "Nothing was saved by the failed action; settings and stacks written earlier are unaffected.",
                });
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Log(LogLevel.Error, "Tasks", "Unobserved task exception", exception: e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) log.Log(LogLevel.Critical, "App", "Fatal exception", exception: ex);
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose(); // disposes the process executor (kills captured child processes only)
        base.OnExit(e);
    }
}
