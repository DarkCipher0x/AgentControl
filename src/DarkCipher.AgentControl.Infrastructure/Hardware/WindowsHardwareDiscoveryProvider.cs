using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Hardware;
using Microsoft.Win32;

namespace DarkCipher.AgentControl.Infrastructure.Hardware;

/// <summary>
/// Read-only Windows hardware discovery via WMI, the display-class registry and kernel memory status. Every probe is
/// isolated: a failing probe adds a warning and the scan continues. Nothing is written and no driver tools are run.
/// </summary>
public sealed class WindowsHardwareDiscoveryProvider : IHardwareDiscoveryProvider
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private readonly IAppLogger? _log;

    public WindowsHardwareDiscoveryProvider(IAppLogger? log = null) => _log = log;

    public Task<HardwareProfile> ScanAsync(CancellationToken ct) => Task.Run(() => Scan(ct), ct);

    private HardwareProfile Scan(CancellationToken ct)
    {
        var warnings = new List<string>();
        T Probe<T>(string what, Func<T> f, T fallback)
        {
            ct.ThrowIfCancellationRequested();
            try { return f(); }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
            {
                warnings.Add($"{what} could not be read: {ex.Message}");
                _log?.Warn("Hardware", $"{what} probe failed: {ex.Message}");
                return fallback;
            }
        }

        var os = Probe("Operating system", ReadOs, new OsInfo(RuntimeInformation.OSDescription, Environment.OSVersion.Version.ToString(), "", RuntimeInformation.OSArchitecture.ToString()));
        var cpu = Probe("Processor", ReadCpu, new CpuInfo("Unknown", 0, Environment.ProcessorCount, InstructionSets()));
        var mem = Probe("Memory", ReadMemory, new MemoryInfo(0, 0));
        var gpus = Probe("Graphics", ReadGpus, []);
        var disks = Probe("Disks", ReadDisks, []);
        var npus = Probe("NPUs", ReadNpus, []);

        if (gpus.Count(g => !g.IsVirtual) == 0) warnings.Add("No physical GPU was found; local models will run on the CPU.");
        return new HardwareProfile(os, cpu, mem, gpus, disks, npus, DateTimeOffset.UtcNow, warnings);
    }

    private static OsInfo ReadOs()
    {
        using var s = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem");
        foreach (var o in s.Get())
            return new OsInfo(o["Caption"]?.ToString()?.Trim() ?? "Windows", o["Version"]?.ToString() ?? "", o["BuildNumber"]?.ToString() ?? "", o["OSArchitecture"]?.ToString() ?? "");
        throw new InvalidOperationException("Win32_OperatingSystem returned nothing.");
    }

    private static CpuInfo ReadCpu()
    {
        using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        string name = "Unknown"; int cores = 0, threads = 0;
        foreach (var o in s.Get())
        {
            name = o["Name"]?.ToString()?.Trim() ?? name;
            cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
            threads += Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
        }
        return new CpuInfo(name, cores, threads == 0 ? Environment.ProcessorCount : threads, InstructionSets());
    }

    private static List<string> InstructionSets()
    {
        var list = new List<string>();
        if (Sse42.IsSupported) list.Add("SSE4.2");
        if (Avx.IsSupported) list.Add("AVX");
        if (Avx2.IsSupported) list.Add("AVX2");
        if (Fma.IsSupported) list.Add("FMA");
        if (Avx512F.IsSupported) list.Add("AVX-512F");
        if (Avx512BW.IsSupported) list.Add("AVX-512BW");
        if (Avx512Vbmi.IsSupported) list.Add("AVX-512VBMI");
        if (System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported) list.Add("NEON");
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    private static MemoryInfo ReadMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) throw new InvalidOperationException("GlobalMemoryStatusEx failed.");
        return new MemoryInfo((long)m.ullTotalPhys, (long)m.ullAvailPhys);
    }

    private sealed record RegistryAdapter(string Desc, long? Memory, string? Version, DateTimeOffset? Date, string? MatchingId);

    private static List<RegistryAdapter> ReadDisplayRegistry()
    {
        var list = new List<RegistryAdapter>();
        using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (cls is null) return list;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            try
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string desc) continue;
                var mem = GpuClassifier.ParseRegistryMemory(k.GetValue("HardwareInformation.qwMemorySize"))
                          ?? GpuClassifier.ParseRegistryMemory(k.GetValue("HardwareInformation.MemorySize"));
                // Drivers store DriverDate as "M-d-yyyy" regardless of locale.
                DateTimeOffset? date = DateTime.TryParseExact(k.GetValue("DriverDate") as string, ["M-d-yyyy", "MM-dd-yyyy"],
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
                list.Add(new RegistryAdapter(desc, mem, k.GetValue("DriverVersion") as string, date, k.GetValue("MatchingDeviceId") as string));
            }
            catch (System.Security.SecurityException) { /* some subkeys are ACL-protected */ }
        }
        return list;
    }

    private static List<GpuInfo> ReadGpus()
    {
        var registry = ReadDisplayRegistry();
        var result = new List<GpuInfo>();
        using var s = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion, PNPDeviceID FROM Win32_VideoController");
        foreach (var o in s.Get())
        {
            var name = o["Name"]?.ToString()?.Trim() ?? "Unknown adapter";
            var pnp = o["PNPDeviceID"]?.ToString();
            var isVirtual = GpuClassifier.IsVirtual(name, pnp);
            var vendor = isVirtual ? GpuVendor.Virtual : GpuClassifier.VendorFrom(name, pnp);
            var reg = registry.FirstOrDefault(r => string.Equals(r.Desc, name, StringComparison.OrdinalIgnoreCase)
                                                   && (r.MatchingId is null || pnp is null || pnp.StartsWith(r.MatchingId, StringComparison.OrdinalIgnoreCase)))
                      ?? registry.FirstOrDefault(r => string.Equals(r.Desc, name, StringComparison.OrdinalIgnoreCase));

            long memory; string source;
            if (reg?.Memory is { } regMem) { memory = regMem; source = "driver registry (qwMemorySize)"; }
            else
            {
                var wmi = o["AdapterRAM"] is { } ar ? Convert.ToInt64(Convert.ToUInt32(ar)) : 0;
                memory = wmi;
                source = wmi >= 4L * 1024 * 1024 * 1024 - 1024 * 1024 ? "WMI AdapterRAM (capped at 4 GB; real value may be higher)" : "WMI AdapterRAM";
            }

            result.Add(new GpuInfo(name, vendor, memory, source, o["DriverVersion"]?.ToString() ?? reg?.Version, reg?.Date, isVirtual,
                isVirtual ? DetectionState.Unsupported : DetectionState.Detected, isVirtual ? null : GpuClassifier.LikelyBackend(vendor)));
        }
        return result;
    }

    private static List<DiskInfo> ReadDisks() =>
        DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
            .Select(d => new DiskInfo(d.RootDirectory.FullName, string.IsNullOrEmpty(d.VolumeLabel) ? null : d.VolumeLabel, d.DriveFormat,
                d.TotalSize, d.AvailableFreeSpace, d.DriveType == DriveType.Removable, d.DriveType == DriveType.Network)).ToList();

    private static List<NpuInfo> ReadNpus()
    {
        var list = new List<NpuInfo>();
        // Broad WMI pre-filter, then a strict classifier ('%NPU%' alone would also match "INPUT" devices).
        using var s = new ManagementObjectSearcher("SELECT Name, PNPClass, Manufacturer FROM Win32_PnPEntity WHERE PNPClass='ComputeAccelerator' OR Name LIKE '%NPU%' OR Name LIKE '%AI Boost%' OR Name LIKE '%Hexagon%' OR Name LIKE '%Neural%'");
        foreach (var o in s.Get())
        {
            var name = o["Name"]?.ToString() ?? "NPU";
            if (!NpuClassifier.IsNpu(name, o["PNPClass"]?.ToString())) continue;
            var vendor = o["Manufacturer"]?.ToString() ?? "Unknown";
            list.Add(new NpuInfo(name, vendor, DetectionState.Detected,
                "Detected only. Ollama does not use NPUs; NPU inference needs a vendor runtime (e.g. Foundry Local / ONNX Runtime) that this app does not manage yet."));
        }
        return list;
    }
}
