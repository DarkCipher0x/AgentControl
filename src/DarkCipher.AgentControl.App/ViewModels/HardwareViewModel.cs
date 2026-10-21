using System.Collections.ObjectModel;
using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.Application.Hardware;
using DarkCipher.AgentControl.Application.Recommendations;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Hardware;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed record FactRow(string Label, string Value, string State);

public sealed class HardwareViewModel : PageViewModel
{
    private readonly HardwareService _hardware;
    private string _headline = "";

    public HardwareViewModel(HardwareService hardware)
    {
        _hardware = hardware;
        RescanCommand = new AsyncCommand(() => Guard(() => LoadAsync(true), "Scan failed"));
    }

    public override string Title => "Hardware";
    public override string Subtitle => "Detected is not the same as supported — each item shows its own state.";

    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public ObservableCollection<string> Reasons { get; } = [];
    public ObservableCollection<FactRow> Facts { get; } = [];
    public ObservableCollection<string> Budget { get; } = [];
    public ICommand RescanCommand { get; }

    protected override Task OnActivateAsync() => LoadAsync(false);

    private async Task LoadAsync(bool rescan)
    {
        var hw = await _hardware.GetAsync(CancellationToken.None, rescan);
        var a = _hardware.Assessment!;
        Headline = a.Headline;
        Reasons.Clear();
        foreach (var r in a.Reasons) Reasons.Add(r);

        Facts.Clear();
        Facts.Add(new("Windows", $"{hw.Os.Caption} ({hw.Os.Version}, build {hw.Os.Build}, {hw.Os.Architecture})", "Detected"));
        Facts.Add(new("Processor", $"{hw.Cpu.Name} — {hw.Cpu.Cores} cores / {hw.Cpu.LogicalProcessors} threads", "Detected"));
        Facts.Add(new("Instruction sets", string.Join(", ", hw.Cpu.InstructionSets), "Detected"));
        Facts.Add(new("Memory", $"{Bytes.FormatGiB(hw.Memory.TotalBytes)} total, {Bytes.FormatGiB(hw.Memory.AvailableBytes)} available", "Detected"));
        foreach (var g in hw.Gpus)
            Facts.Add(new(g.IsVirtual ? "GPU (virtual, ignored)" : "GPU", $"{g.Name} — {Bytes.FormatGiB(g.DedicatedMemoryBytes)} ({g.MemorySource}); driver {g.DriverVersion} {g.DriverDate:yyyy-MM-dd}; backend: {g.Backend ?? "none"}", g.State.ToString()));
        foreach (var n in hw.Npus) Facts.Add(new("NPU", $"{n.Name} ({n.Vendor}) — {n.RuntimeNote}", n.State.ToString()));
        if (hw.Npus.Count == 0) Facts.Add(new("NPU", "None detected", "Unknown"));
        foreach (var d in hw.Disks) Facts.Add(new("Disk " + d.Root, $"{Bytes.Format(d.FreeBytes)} free of {Bytes.Format(d.TotalBytes)} ({d.Format}){(d.IsRemovable ? ", removable" : "")}{(d.IsNetwork ? ", network" : "")}", "Detected"));
        foreach (var w in hw.Warnings) Facts.Add(new("Warning", w, "—"));

        Budget.Clear();
        foreach (var s in RecommendationEngine.Budget(hw).Assumptions) Budget.Add(s);
    }
}
