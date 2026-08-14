using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ZomboidManager;

public sealed class HardwareSnapshot
{
    public string CpuName { get; init; } = "Unknown CPU";
    public double CpuUsage { get; init; }
    public ulong RamTotalBytes { get; init; }
    public ulong RamUsedBytes { get; init; }
    public ulong RamAvailableBytes { get; init; }
    public double RamTotalGb { get; init; }
    public double RamUsedGb { get; init; }
    public string DiskRoot { get; init; } = "";
    public long DiskTotalBytes { get; init; }
    public long DiskUsedBytes { get; init; }
    public long DiskFreeBytes { get; init; }
    public double DiskTotalGb { get; init; }
    public double DiskUsedGb { get; init; }
    public double DiskFreeGb { get; init; }

    public double RamUsagePercent =>
        RamTotalBytes > 0 ? Math.Round(RamUsedBytes * 100.0 / RamTotalBytes, 1) : 0;
}

/// <summary>
/// Samples host hardware. Static fields (CPU name, total RAM/disk) are refreshed
/// explicitly; dynamic fields (usage %, free disk) update on a live tick and are
/// shared via <see cref="LatestSnapshot"/> so StatsCollector does not re-query.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    /// <summary>How often DriveInfo is refreshed relative to dynamic ticks (every Nth sample).</summary>
    public const int DiskRefreshEveryNTicks = 3;

    /// <summary>Reuse a live sample for stats if younger than this.</summary>
    public static readonly TimeSpan StatsCacheMaxAge = TimeSpan.FromSeconds(8);

    private readonly object _gate = new();
    private PerformanceCounter? _cpuCounter;
    private bool _cpuPrimed;
    private string? _cpuName;
    private ulong _totalRamBytes;

    private string _diskRoot = "";
    private long _diskTotalBytes;
    private long _diskFreeBytes;
    private long _diskUsedBytes;
    private bool _staticDiskReady;
    private string? _staticDiskServerPath;

    private int _dynamicTickCount;
    private HardwareSnapshot? _latest;
    private DateTime _latestUtc;

    public HardwareMonitor()
    {
        // PerformanceCounter create+prime is deferred until EnsureCpuCounter()
        // (first Server-tab open) so startup does not pay that cost.
        _cpuName = ReadCpuName();
        _totalRamBytes = ReadTotalPhysicalMemory();
    }

    /// <summary>
    /// Create and prime the CPU PerformanceCounter. Call when the Server tab
    /// first starts live hardware monitoring — not during app construction.
    /// </summary>
    public void EnsureCpuCounter()
    {
        lock (_gate)
        {
            if (_cpuCounter is not null || _cpuPrimed)
                return;

            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _ = _cpuCounter.NextValue(); // prime (first NextValue is always ~0)
                _cpuPrimed = true;
            }
            catch
            {
                _cpuCounter = null;
                _cpuPrimed = true; // do not retry every sample
            }
        }
    }

    /// <summary>Most recent dynamic sample, if any.</summary>
    public HardwareSnapshot? LatestSnapshot
    {
        get
        {
            lock (_gate)
                return _latest;
        }
    }

    /// <summary>
    /// Refresh disk root and totals (static). Call when the Server tab opens,
    /// server folder path changes, or after an explicit static cache refresh.
    /// </summary>
    public void RefreshStatic(string? serverPath)
    {
        lock (_gate)
        {
            RefreshStaticDiskUnlocked(serverPath, force: true);
        }
    }

    /// <summary>
    /// Live sample: CPU + RAM every call; disk free every
    /// <see cref="DiskRefreshEveryNTicks"/> ticks (or when forced / static not ready).
    /// Updates <see cref="LatestSnapshot"/>.
    /// </summary>
    public HardwareSnapshot SampleDynamic(string? serverPath, bool forceDisk = false)
    {
        lock (_gate)
        {
            float cpu = ReadCpuUsageUnlocked();
            ulong avail = ReadAvailablePhysicalMemory();
            ulong total = _totalRamBytes;
            ulong used = total > avail ? total - avail : 0;

            EnsureStaticDiskUnlocked(serverPath);

            _dynamicTickCount++;
            bool refreshDisk = forceDisk
                || !_staticDiskReady
                || (_dynamicTickCount % DiskRefreshEveryNTicks) == 1;

            if (refreshDisk)
                RefreshDiskFreeUnlocked();

            HardwareSnapshot snap = BuildSnapshotUnlocked(cpu, total, used, avail);
            _latest = snap;
            _latestUtc = DateTime.UtcNow;
            return snap;
        }
    }

    /// <summary>
    /// Prefer a recent cached sample (avoids duplicate PerformanceCounter/DriveInfo
    /// when the Server-tab timer is already polling). Otherwise takes a fresh sample.
    /// </summary>
    public HardwareSnapshot GetCachedOrSample(string? serverPath)
    {
        lock (_gate)
        {
            if (_latest is not null && DateTime.UtcNow - _latestUtc <= StatsCacheMaxAge)
                return _latest;
        }

        return SampleDynamic(serverPath);
    }

    public void Dispose()
    {
        _cpuCounter?.Dispose();
        _cpuCounter = null;
    }

    private HardwareSnapshot BuildSnapshotUnlocked(float cpu, ulong total, ulong used, ulong avail)
    {
        return new HardwareSnapshot
        {
            CpuName = _cpuName ?? "Unknown CPU",
            CpuUsage = Math.Round(cpu, 1),
            RamTotalBytes = total,
            RamUsedBytes = used,
            RamAvailableBytes = avail,
            RamTotalGb = Math.Round(total / (1024.0 * 1024 * 1024), 2),
            RamUsedGb = Math.Round(used / (1024.0 * 1024 * 1024), 2),
            DiskRoot = _diskRoot,
            DiskTotalBytes = _diskTotalBytes,
            DiskUsedBytes = _diskUsedBytes,
            DiskFreeBytes = _diskFreeBytes,
            DiskTotalGb = Math.Round(_diskTotalBytes / (1024.0 * 1024 * 1024), 1),
            DiskUsedGb = Math.Round(_diskUsedBytes / (1024.0 * 1024 * 1024), 1),
            DiskFreeGb = Math.Round(_diskFreeBytes / (1024.0 * 1024 * 1024), 1)
        };
    }

    private float ReadCpuUsageUnlocked()
    {
        try
        {
            if (_cpuCounter is not null)
            {
                float cpu = _cpuCounter.NextValue();
                if (!_cpuPrimed)
                    _cpuPrimed = true;
                return cpu;
            }
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    private void EnsureStaticDiskUnlocked(string? serverPath)
    {
        string normalized = serverPath ?? string.Empty;
        if (_staticDiskReady
            && string.Equals(_staticDiskServerPath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RefreshStaticDiskUnlocked(serverPath, force: true);
    }

    private void RefreshStaticDiskUnlocked(string? serverPath, bool force)
    {
        string normalized = serverPath ?? string.Empty;
        if (!force
            && _staticDiskReady
            && string.Equals(_staticDiskServerPath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string driveRoot = ResolveDriveRoot(serverPath);
        long diskTotal = 0;
        long diskFree = 0;
        long diskUsed = 0;
        try
        {
            var drive = new DriveInfo(driveRoot);
            if (drive.IsReady)
            {
                diskTotal = drive.TotalSize;
                diskFree = drive.AvailableFreeSpace;
                diskUsed = diskTotal - diskFree;
            }
        }
        catch
        {
            // keep zeros
        }

        _diskRoot = driveRoot;
        _diskTotalBytes = diskTotal;
        _diskFreeBytes = diskFree;
        _diskUsedBytes = diskUsed;
        _staticDiskReady = true;
        _staticDiskServerPath = normalized;
        _dynamicTickCount = 0;
    }

    private void RefreshDiskFreeUnlocked()
    {
        if (string.IsNullOrWhiteSpace(_diskRoot))
            return;

        try
        {
            var drive = new DriveInfo(_diskRoot);
            if (!drive.IsReady)
                return;

            // Totals can change only in rare remount cases; keep them in sync cheaply.
            _diskTotalBytes = drive.TotalSize;
            _diskFreeBytes = drive.AvailableFreeSpace;
            _diskUsedBytes = _diskTotalBytes - _diskFreeBytes;
        }
        catch
        {
            // keep last known free
        }
    }

    private static string ResolveDriveRoot(string? serverPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(serverPath))
            {
                string full = Path.GetFullPath(serverPath);
                string? root = Path.GetPathRoot(full);
                if (!string.IsNullOrWhiteSpace(root))
                    return root;
            }
        }
        catch
        {
            // fall through
        }

        return Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    }

    private static string? ReadCpuName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string;
        }
        catch
        {
            return null;
        }
    }

    private static ulong ReadTotalPhysicalMemory()
    {
        try
        {
            var status = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(ref status))
                return status.ullTotalPhys;
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    private static ulong ReadAvailablePhysicalMemory()
    {
        try
        {
            var status = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(ref status))
                return status.ullAvailPhys;
        }
        catch
        {
            // ignore
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            dwMemoryLoad = 0;
            ullTotalPhys = 0;
            ullAvailPhys = 0;
            ullTotalPageFile = 0;
            ullAvailPageFile = 0;
            ullTotalVirtual = 0;
            ullAvailVirtual = 0;
            ullAvailExtendedVirtual = 0;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
