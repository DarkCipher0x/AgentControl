using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Domain.Common;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed class LogsViewModel : PageViewModel
{
    private readonly IAppLogger _log;
    private string _search = "";
    private string _level = "All";

    public LogsViewModel(IAppLogger log)
    {
        _log = log;
        log.Logged += (_, e) => Ui.OnUi(() => { if (Matches(e)) Entries.Insert(0, e); });
        CopyCommand = new RelayCommand(() => { Ui.Copy(Render()); Message = "Copied (already redacted)."; });
        ExportCommand = new RelayCommand(Export);
        RefreshCommand = new RelayCommand(Reload);
    }

    public override string Title => "Logs";
    public override string Subtitle => "Structured application log with correlation IDs. Secrets are redacted before anything is written; source code, prompts and model responses are never logged.";

    public ObservableCollection<LogEntry> Entries { get; } = [];
    public IReadOnlyList<string> Levels { get; } = ["All", "Information", "Warning", "Error"];
    public string Search { get => _search; set { if (Set(ref _search, value)) Reload(); } }
    public string Level { get => _level; set { if (Set(ref _level, value)) Reload(); } }

    public ICommand CopyCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand RefreshCommand { get; }

    protected override Task OnActivateAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    private bool Matches(LogEntry e)
    {
        if (Level != "All" && Enum.TryParse<LogLevel>(Level, out var min) && e.Level < min) return false;
        return Search.Length == 0 || e.Message.Contains(Search, StringComparison.OrdinalIgnoreCase) || e.Component.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || (e.Correlation?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void Reload()
    {
        Entries.Clear();
        foreach (var e in _log.Recent(3000).Where(Matches)) Entries.Add(e);
    }

    private string Render()
    {
        var sb = new StringBuilder();
        foreach (var e in Entries) sb.AppendLine($"{e.Timestamp:O}\t{e.Level}\t{e.Component}\t{e.Correlation}\t{e.Message}\t{e.Exception}");
        return sb.ToString();
    }

    private void Export()
    {
        if (Dialogs.Headless) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"agentcontrol-log-{DateTime.Now:yyyyMMdd-HHmm}.tsv", Filter = "Tab-separated|*.tsv" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, Render());
        Message = "Exported (already redacted).";
    }
}
