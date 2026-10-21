using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DarkCipher.AgentControl.App.ViewModels;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Extensions;
using DarkCipher.AgentControl.Infrastructure.Persistence;
using DarkCipher.AgentControl.Integrations.Ollama;
using Microsoft.Extensions.DependencyInjection;

namespace DarkCipher.AgentControl.App.Shell;

/// <summary>
/// "--selftest &lt;reportDir&gt;" with DCAC_DATA_DIR set to a scratch folder: exercises central security controls in the
/// real composed app, renders every screen OFF-SCREEN to PNG (no window is ever shown; every dialog is refused) and
/// writes report.txt. Exit code 0 = all checks passed. Refuses to run against the real data folder.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(IServiceProvider sp, string reportDir)
    {
        Dialogs.Headless = true;
        // The shell is rendered off-screen from a MainWindow instance that is never shown; it must not become the
        // application's main window (closing it would end the process before the report is written).
        System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var lines = new List<string>();
        var failures = 0;
        void Ok(string s) => lines.Add("ok    " + s);
        void Fail(string s) { lines.Add("FAIL  " + s); failures++; }
        void Check(bool cond, string s) { if (cond) Ok(s); else Fail(s); }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataDirVariable)))
        {
            Directory.CreateDirectory(reportDir);
            await File.WriteAllTextAsync(Path.Combine(reportDir, "report.txt"), "REFUSED: set DCAC_DATA_DIR to a scratch folder; the self-test never touches the real data store.\n");
            return 3;
        }
        Directory.CreateDirectory(reportDir);
        var shots = Path.Combine(reportDir, "shots");
        Directory.CreateDirectory(shots);

        // ---- Security controls in the composed app ----
        var paths = sp.GetRequiredService<IProtectedPathService>();
        var appPaths = sp.GetRequiredService<IAppPaths>();
        var traversal = paths.Evaluate(Path.Combine(appPaths.ConfigDirectory, "..", "audit", "x.txt"), PathOperation.Read, PathActor.Agent);
        Check(traversal.Kind == PathDecisionKind.Deny, "agent access to app data via '..' traversal is denied");
        var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects");
        var write = paths.Evaluate(Path.Combine(projects, "x", "file.txt"), PathOperation.Write, PathActor.Agent);
        Check(write.Kind == PathDecisionKind.Deny, "writes into the protected Projects tree are denied (evaluation only, nothing written)");
        Check(paths.Evaluate(projects.ToUpperInvariant() + "\\.\\X", PathOperation.Write).Kind == PathDecisionKind.Deny, "case/dot-segment variants of a protected path are denied");

        var redactor = sp.GetRequiredService<IRedactor>();
        Check(!redactor.Redact("token hf_abcdefghijklmnopqrstuvwxyz0123 and sk-ant-abcdefghijklmnopqrstuvwxyz").Contains("hf_abc", StringComparison.Ordinal), "redactor masks Hugging Face and Anthropic tokens");

        var exec = sp.GetRequiredService<IProcessExecutor>();
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var batch = await exec.RunAsync(new ProcessSpec { Executable = Path.Combine(appPaths.TestProjectsDirectory, "x.cmd"), Purpose = "selftest" }, CancellationToken.None);
        Check(batch.Outcome == ProcessOutcome.Blocked, "batch files are refused by the process executor");
        var shell = await exec.RunAsync(new ProcessSpec { Executable = Path.Combine(sys, "cmd.exe"), Arguments = ["/c", "echo", "a&b"], Purpose = "selftest" }, CancellationToken.None);
        Check(shell.Outcome == ProcessOutcome.Blocked, "command interpreters (cmd.exe) are refused");
        var whereExe = Path.Combine(sys, "where.exe");
        var inj = await exec.RunAsync(new ProcessSpec { Executable = whereExe, Arguments = ["where"], Environment = new Dictionary<string, string> { ["NODE_OPTIONS"] = "--require x" }, Purpose = "selftest" }, CancellationToken.None);
        Check(inj.Outcome == ProcessOutcome.Blocked, "code-injection environment variables are refused");
        var run = await exec.RunAsync(new ProcessSpec { Executable = whereExe, Arguments = ["where.exe"], Purpose = "selftest", Timeout = TimeSpan.FromSeconds(15) }, CancellationToken.None);
        Check(run.Succeeded && run.StdOut.Contains("where.exe", StringComparison.OrdinalIgnoreCase), $"a direct executable runs with captured output ({run.Outcome})");

        var net = sp.GetRequiredService<INetworkClient>();
        try
        {
            await net.SendAsync(new NetworkRequest(HttpMethod.Get, new Uri("https://example.com/"), NetworkPurpose.Inference), CancellationToken.None);
            Fail("unapproved public endpoint was NOT blocked");
        }
        catch (NetworkBlockedException) { Ok("unapproved public endpoint is blocked before any connection"); }

        var audit = sp.GetRequiredService<IAuditLogger>().Verify();
        Check(audit.Intact, "audit chain verifies: " + audit.Message);

        // ---- Runtime (informational) ----
        var status = await sp.GetRequiredService<OllamaRuntime>().GetStatusAsync(CancellationToken.None);
        lines.Add($"info  Ollama: {status.Health} — {status.Summary}");

        // ---- Extensions start without blocking ----
        await sp.GetRequiredService<ExtensionHost>().StartAsync(CancellationToken.None);
        Ok($"extension host started ({sp.GetRequiredService<ExtensionHost>().Extensions.Count} records)");

        // ---- Quick Setup dry run: real detection, plan and session preview; nothing is downloaded or launched ----
        try
        {
            var hwSvc = sp.GetRequiredService<Application.Hardware.HardwareService>();
            var hw = await hwSvc.GetAsync(CancellationToken.None);
            var ollama = sp.GetRequiredService<OllamaRuntime>();
            var rtStatus = await ollama.GetStatusAsync(CancellationToken.None);
            var installs = new List<Domain.Agents.AgentInstallation>();
            foreach (var a in sp.GetServices<Domain.Agents.IAgentProvider>()) installs.Add(await a.DetectAsync(CancellationToken.None));
            IReadOnlyList<Domain.Models.ModelDescriptor> installed = [];
            IReadOnlyList<Domain.Models.RunningModel> running = [];
            if (rtStatus.Health is ComponentHealth.Healthy or ComponentHealth.Degraded)
            {
                installed = await ollama.ListModelsAsync(CancellationToken.None);
                running = await ollama.ListRunningAsync(CancellationToken.None);
            }
            var recs = sp.GetRequiredService<Application.Recommendations.RecommendationEngine>()
                .Recommend(hw, Application.Recommendations.RecommendationProfile.AgenticCoding, installed, 3, running);
            var plan = Application.Setup.SetupPlanner.Build(Application.Setup.OneClickPlan.Balanced, hw, rtStatus, installs, recs.Count > 0 ? recs[0] : null,
                "claude-code", Composition.Bootstrapper.ModelDriveFreeBytes(), appPaths.AgentProfilesDirectory, appPaths.TestProjectsDirectory);
            Check(plan.Operations.Count > 0 && plan.Operations.All(o => !o.RequiresElevation), $"Quick Setup plan builds ({plan.Operations.Count} steps, none needs elevation)");
            foreach (var o in plan.Operations) lines.Add($"info    plan: {o.Title} [{o.State}]");

            var projectDir = Application.Setup.TestProjectFactory.Create(appPaths.TestProjectsDirectory);
            var added = sp.GetRequiredService<Application.Sessions.ProjectService>().Add(projectDir, "Self-test sample");
            Check(added.Success, "disposable sample project can be added as an agent project: " + added.Message);
            if (added.Success)
            {
                var sessions = sp.GetRequiredService<Application.Sessions.SessionService>();
                var preview = await sessions.PreviewAsync("claude-local-gateway", added.Value!.Id, CancellationToken.None);
                if (installs.Any(i => i.ProviderId == "claude-code" && i.Installed))
                    Check(preview.Success && preview.Value!.CommandPreview.Contains("ANTHROPIC_BASE_URL=http://127.0.0.1:11434", StringComparison.Ordinal)
                          && !preview.Value.CommandPreview.Contains("bypassPermissions", StringComparison.Ordinal),
                        "Claude Code local-gateway session preview is loopback-only and never bypasses permissions" + (preview.Success ? "" : ": " + preview.Message));
                else lines.Add("info  Claude Code not installed: session preview skipped");
                var cloud = await sessions.PreviewAsync("claude-native", added.Value!.Id, CancellationToken.None);
                Check(!cloud.Success, "cloud stack is refused in Fully Local mode");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail("Quick Setup dry run: " + ex.Message); }

        // ---- Icon, design system, appearance (0.1.6) ----
        var nav = sp.GetRequiredService<Navigator>();
        var appearance = sp.GetRequiredService<Application.Appearance.AppearanceSettingsService>();
        var res = System.Windows.Application.Current.Resources;
        try
        {
            var frames = Appearance.ApplicationIconService.FrameSizes();
            Check(new[] { 16, 20, 24, 32, 48, 64, 128, 256 }.All(frames.Contains), "embedded app.ico decodes with frames " + string.Join(",", frames));
            Check(Appearance.ApplicationIconService.WindowIcon is not null && Appearance.ApplicationIconService.LargeImage.Width > 0, "window icon and About artwork load from resources");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail("icon resources: " + ex.Message); }

        Color C(string key) => ((SolidColorBrush)res[key]).Color;
        Check(C("Bg") == (Color)ColorConverter.ConvertFromString("#050909") && C("Accent") == (Color)ColorConverter.ConvertFromString("#36D2CB"),
            "DarkCipher theme tokens applied (Bg #050909, accent #36D2CB — WhisperDesk/ServerManagement palette)");
        Check(res["TextPrimary"] is SolidColorBrush && res["StatusError"] is SolidColorBrush && res["FocusRing"] is SolidColorBrush, "semantic token aliases published");
        Check(res["UiFont"] is FontFamily f && f.Source == "Segoe UI Variable Text, Segoe UI" && (double)res["FsBase"] == 13.5, "typography tokens match ServerManagement (Segoe UI Variable Text, FsBase 13.5)");

        appearance.Update(s => s with { Theme = Application.Appearance.ThemePresetId.DarkCipherGraphite, TextScale = 1.25 });
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(C("Bg") == (Color)ColorConverter.ConvertFromString("#0A0B0C") && (double)res["FsBase"] == 17, "preset and text size switch live (Graphite, 125 %)");
        appearance.Update(s => s with { HiddenPages = ["logs", "security"], PinnedPages = ["models"] });
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(nav.Items.All(i => i.Key != "logs") && nav.Items.Any(i => i.Key == "security") && nav.Items[0] is { Key: "models", Group: "PINNED" },
            "navigation customisation: pinned first, hidden page removed, Security can never be hidden");
        appearance.Reset(null);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(C("Bg") == (Color)ColorConverter.ConvertFromString("#050909") && nav.Items.Any(i => i.Key == "logs"), "appearance reset restores the defaults");

        // ---- Catalogue, templates, deterministic configuration, configuration transactions (0.1.6) ----
        try
        {
            var catalogue = sp.GetRequiredService<Application.Tools.AgentToolCatalogueService>();
            var entries = await catalogue.RefreshAsync(false, CancellationToken.None);
            Check(entries.Count >= 4 && entries.All(e => e.Descriptor.Source is not null || !e.Descriptor.CanInstall),
                $"agent-tool catalogue detects {entries.Count} tools; every installable one has a pinned package source");
            foreach (var e in entries) lines.Add($"info    tool: {e.Descriptor.DisplayName} — {e.Status.State} {e.Status.Version} ({e.Descriptor.Source?.PackageId ?? "built into Ollama"})");
            lines.Add("info  winget: " + (Integrations.Tools.WingetClient.Locate() is { } w ? "available" : "not found"));

            var engine = sp.GetRequiredService<Application.Templates.TemplateEngine>();
            var forCodex = await engine.CompatibleAsync(new Domain.Templates.TemplateSelection { AgentId = "codex" }, CancellationToken.None);
            Check(forCodex.Count >= 10 && forCodex.All(x => x.Template.Agents.Any(a => a.AgentId == "codex")), $"template filtering offers {forCodex.Count} Codex templates, each with its checks");
            var sel = new Domain.Templates.TemplateSelection { AgentId = "codex", Model = "gpt-oss:20b" };
            var g1 = await engine.PreviewAsync("code-review-local", sel, CancellationToken.None);
            var g2 = await engine.PreviewAsync("code-review-local", sel, CancellationToken.None);
            Check(g1.Success && g2.Success && g1.Value!.Fingerprint == g2.Value!.Fingerprint, "configuration generation is deterministic (same fingerprint twice: " + g1.Value?.Fingerprint + ")");

            var tx = sp.GetRequiredService<Application.Configuration.ConfigurationTransactionService>();
            var target = Path.Combine(appPaths.AgentProfilesDirectory, "selftest", "config.toml");
            var prepared = tx.PrepareFromGenerated(target, Domain.Configuration.ConfigFormat.TomlSubset, "# Managed by DarkCipher AgentControl\nmodel = \"x\"\n", "self-test");
            var committed = await tx.CommitAsync(prepared, null, _ => Task.FromResult((false, "self-test forces a rollback")), CancellationToken.None);
            Check(committed.State == Domain.Configuration.TransactionState.RolledBack && !File.Exists(target), "configuration transaction rolls back when its integration test fails (file removed)");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail("catalogue/templates: " + ex.Message); }

        // ---- Catalogue breadth, bundled gallery, grouped right-click menus (0.1.9) ----
        try
        {
            var cached = sp.GetRequiredService<Application.Tools.AgentToolCatalogueService>().Cached;
            int Count(Domain.Tools.ToolKind k) => cached.Count(e => e.Descriptor.Kind == k);
            Check(Count(Domain.Tools.ToolKind.ModelRuntime) >= 5 && Count(Domain.Tools.ToolKind.Gateway) >= 5 && Count(Domain.Tools.ToolKind.AgentTool) >= 7 && Count(Domain.Tools.ToolKind.SupportingTool) >= 8
                  && cached.Where(e => e.Descriptor.Kind == Domain.Tools.ToolKind.Gateway).All(e => e.Descriptor.PartOfToolId is not null),
                $"catalogue offers {Count(Domain.Tools.ToolKind.ModelRuntime)} runtimes, {Count(Domain.Tools.ToolKind.Gateway)} gateways (each installed with its runtime), {Count(Domain.Tools.ToolKind.AgentTool)} agent tools and {Count(Domain.Tools.ToolKind.SupportingTool)} supporting tools");

            var gallery = sp.GetRequiredService<ExtensionGallery>();
            var items = gallery.Items();
            var packed = 0;
            foreach (var item in items)
            {
                var (review, errors) = gallery.Prepare(item);
                if (review is { CanInstall: true, Trust: Extensions.TrustLevel.Verified }) packed++;
                else lines.Add($"info    gallery {item.Id}: {string.Join("; ", errors.Concat(review?.Errors ?? []))}");
                if (review is not null) sp.GetRequiredService<Extensions.ExtensionHost>().DiscardReview(review);
            }
            Check(items.Count >= 7 && packed == items.Count, $"bundled gallery: {packed} of {items.Count} extensions pack and pass the bundled review (nothing installed)");

            var cmd = new Mvvm.RelayCommand(() => { });
            IReadOnlyList<MenuAction> small = [new("A", cmd), new("B", cmd, Group: "Copy"), new("X", cmd, Danger: true)];
            IReadOnlyList<MenuAction> crowded = [.. Enumerable.Range(0, 5).Select(i => new MenuAction("Main " + i, cmd)), .. Enumerable.Range(0, 3).Select(i => new MenuAction("Open " + i, cmd, Group: "Open")),
                .. Enumerable.Range(0, 3).Select(i => new MenuAction("Copy " + i, cmd, Group: "Copy")), new MenuAction("Delete", cmd, Danger: true)];
            var arranged = ContextMenus.Arrange(crowded);
            Check(!ContextMenus.ShouldGroup(small) && ContextMenus.ShouldGroup(crowded) && arranged[^1].Actions.Single().Danger && arranged.Select(g => g.Group).Take(3).SequenceEqual(["", "Open", "Copy"]),
                "context menus stay flat when short and group into submenus when crowded (danger actions last)");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail("catalogue breadth/gallery/menus: " + ex.Message); }

        // ---- Every screen, rendered off-screen ----
        foreach (var item in nav.Items)
        {
            try
            {
                var vm = (PageViewModel)sp.GetRequiredService(item.ViewModelType);
                var logger = sp.GetRequiredService<IAppLogger>();
                var errorsBefore = logger.Recent(5000).Count(e => e.Level >= LogLevel.Error);
                var activation = vm.ActivateSafeAsync();
                var done = await Task.WhenAny(activation, Task.Delay(TimeSpan.FromSeconds(60)));
                if (done != activation) { Fail($"screen {item.Title}: activation timed out"); continue; }
                var host = new Border
                {
                    Width = 1440, Height = 900, Background = (Brush)System.Windows.Application.Current.Resources["Bg"],
                    Child = new ContentControl { Content = vm },
                };
                TextElement.SetForeground(host, (Brush)System.Windows.Application.Current.Resources["Fg"]);
                TextElement.SetFontFamily(host, (FontFamily)System.Windows.Application.Current.Resources["UiFont"]);
                host.Measure(new Size(1440, 900));
                host.Arrange(new Rect(0, 0, 1440, 900));
                host.UpdateLayout();
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                host.UpdateLayout();
                var bmp = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(host);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                await using (var fs = File.Create(Path.Combine(shots, item.Key + ".png"))) enc.Save(fs);
                var newErrors = logger.Recent(5000).Count(e => e.Level >= LogLevel.Error) - errorsBefore;
                Check(newErrors == 0, $"screen {item.Title} loads and renders without errors" + (string.IsNullOrEmpty(vm.Message) ? "" : $" (status: {vm.Message})"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Fail($"screen {item.Title}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---- The shell and every screen in other presets (visual inspection; Light must be fully themed) ----
        foreach (var (preset, folder, change) in new (string, string, Func<Application.Appearance.AppearanceSettings, Application.Appearance.AppearanceSettings>)[]
                 {
                     ("DarkCipher", "shell-dark", s => s),
                     ("DarkCipher Light", "light", s => s with { Theme = Application.Appearance.ThemePresetId.DarkCipherLight }),
                     ("High Contrast + compact + icon-only", "hc-compact", s => s with { Theme = Application.Appearance.ThemePresetId.DarkCipherHighContrast, Density = Application.Appearance.Density.Compact, Navigation = Application.Appearance.NavigationStyle.IconOnly }),
                 })
        {
            try
            {
                appearance.Update(change);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var dir = Directory.CreateDirectory(Path.Combine(shots, folder)).FullName;
                var shellVm = sp.GetRequiredService<MainViewModel>();
                var window = new MainWindow(shellVm);
                var root = (UIElement)window.Content;
                window.Content = null;
                await Render(new Border { Child = root, DataContext = shellVm }, Path.Combine(dir, "shell.png"));
                if (folder != "shell-dark")
                    foreach (var key in new[] { "dashboard", "catalogue", "templates", "stacks", "downloads", "settings", "models", "security" })
                    {
                        var vm = (PageViewModel)sp.GetRequiredService(Navigator.All.First(n => n.Key == key).ViewModelType);
                        await Render(new ContentControl { Content = vm }, Path.Combine(dir, key + ".png"));
                    }
                if (folder != "shell-dark")
                {
                    // A real tool's right-click menu (built by ContextMenus), drawn the way the themed ContextMenu template draws it.
                    var tools = sp.GetRequiredService<Application.Tools.AgentToolCatalogueService>().Cached;
                    if (tools.FirstOrDefault(e => e.Descriptor.Id == "ollama") is { } sample)
                    {
                        var row = new ViewModels.ToolRowViewModel(sample, sp.GetRequiredService<ToolActions>());
                        var panel = new StackPanel { Background = (Brush)System.Windows.Application.Current.Resources["Surface2"] };
                        foreach (var o in ContextMenus.Build(row.MenuActions(inCatalogue: false))) panel.Children.Add((UIElement)o);
                        await Render(new Border { Child = panel, Width = 300, BorderThickness = new Thickness(1), Padding = new Thickness(2),
                            BorderBrush = (Brush)System.Windows.Application.Current.Resources["LineStrong"] }, Path.Combine(dir, "contextmenu.png"));
                    }
                }
                window.Close();
                Ok($"rendered the shell{(folder == "shell-dark" ? "" : " and 8 screens")} in {preset}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Fail($"render in {preset}: {ex.GetType().Name}: {ex.Message}"); }
        }
        appearance.Reset(null);

        Check(Dialogs.RefusedInHeadless == 0, $"no dialog tried to open during the self-test ({Dialogs.RefusedInHeadless} refused)");
        var log = sp.GetRequiredService<IAppLogger>().Recent(5000).Where(e => e.Level >= LogLevel.Error).ToList();
        foreach (var e in log.Take(20)) lines.Add($"log   {e.Level} {e.Component}: {e.Message} {e.Exception}");

        var summary = $"{lines.Count(l => l.StartsWith("ok", StringComparison.Ordinal))} ok, {failures} failed";
        lines.Insert(0, $"DarkCipher AgentControl self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {summary}");
        await File.WriteAllTextAsync(Path.Combine(reportDir, "report.txt"), string.Join(Environment.NewLine, lines) + Environment.NewLine, Encoding.UTF8);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Lays out and renders an element off-screen at 1440 × 900 (never shown).</summary>
    private static async Task Render(FrameworkElement content, string file)
    {
        var res = System.Windows.Application.Current.Resources;
        var host = new Border { Width = 1440, Height = 900, Background = (Brush)res["Bg"], Child = content };
        TextElement.SetForeground(host, (Brush)res["Fg"]);
        TextElement.SetFontFamily(host, (FontFamily)res["UiFont"]);
        if (content is ContentControl { Content: PageViewModel pvm }) await pvm.ActivateSafeAsync();
        host.Measure(new Size(1440, 900));
        host.Arrange(new Rect(0, 0, 1440, 900));
        host.UpdateLayout();
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        await using var fs = File.Create(file);
        enc.Save(fs);
        host.Child = null;
    }
}
