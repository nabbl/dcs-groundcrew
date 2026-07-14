using System.Diagnostics;
using System.Runtime.InteropServices;
using DcsDashboard.Api.Models;

namespace DcsDashboard.Api.Services;

public sealed class HostMetricsService : IDisposable
{
    private readonly PerformanceCounter? _cpuCounter;
    private readonly object _sampleLock = new();
    private TimeSpan _lastProcessCpu;
    private DateTimeOffset _lastProcessSample = DateTimeOffset.UtcNow;

    public HostMetricsService()
    {
        if (OperatingSystem.IsWindows())
        {
            try { _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _cpuCounter.NextValue(); }
            catch { _cpuCounter = null; }
        }
    }

    public IReadOnlyList<Metric> Read(Process? dcs, string storagePath)
    {
        var hostCpu = OperatingSystem.IsWindows() ? _cpuCounter?.NextValue() ?? 0 : 0;
        var totalMemoryGb = 0d;
        var usedMemoryGb = 0d;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (GlobalMemoryStatusEx(ref memory))
                {
                    totalMemoryGb = memory.TotalPhysical / 1073741824d;
                    usedMemoryGb = (memory.TotalPhysical - memory.AvailablePhysical) / 1073741824d;
                }
            }
            catch { }
        }

        var processMemoryGb = dcs is null ? 0 : dcs.WorkingSet64 / 1073741824d;
        var processCpu = ReadProcessCpu(dcs);
        var freeDiskGb = 0d;
        var totalDiskGb = 1d;
        try
        {
            var root = string.IsNullOrWhiteSpace(storagePath) ? Path.GetPathRoot(Environment.CurrentDirectory) : Path.GetPathRoot(storagePath);
            if (root is not null)
            {
                var drive = new DriveInfo(root);
                freeDiskGb = drive.AvailableFreeSpace / 1073741824d;
                totalDiskGb = drive.TotalSize / 1073741824d;
            }
        }
        catch { }

        return new List<Metric>
        {
            new("CPU", Math.Round(hostCpu, 1), "%", 100),
            new("Memory", Math.Round(usedMemoryGb, 1), "GB", Math.Max(1, Math.Round(totalMemoryGb, 1))),
            new("DCS process", Math.Round(processMemoryGb, 1), "GB", Math.Max(1, Math.Round(totalMemoryGb, 1))),
            new("Disk", Math.Round(freeDiskGb, 1), "GB free", Math.Max(1, Math.Round(totalDiskGb, 1))),
            new("DCS CPU", Math.Round(processCpu, 1), "%", 100)
        };
    }

    private double ReadProcessCpu(Process? process)
    {
        lock (_sampleLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (process is null) { _lastProcessCpu = TimeSpan.Zero; _lastProcessSample = now; return 0; }
            try
            {
                var current = process.TotalProcessorTime;
                var elapsed = now - _lastProcessSample;
                var cpu = elapsed.TotalMilliseconds <= 0 ? 0 : (current - _lastProcessCpu).TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
                _lastProcessCpu = current;
                _lastProcessSample = now;
                return Math.Clamp(cpu, 0, 100);
            }
            catch { return 0; }
        }
    }

    public void Dispose() => _cpuCounter?.Dispose();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
