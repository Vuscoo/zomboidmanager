using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ZomboidManager;

public sealed class HardwareMonitor : IDisposable
{
    private PerformanceCounter? _cpuCounter;
    private bool _cpuPrimed;
    private string? _cpuName;
    private ulong _totalRamBytes;

    public HardwareMonitor()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _ = _cpuCounter.NextValue(); // prime
            _cpuPrimed = true;
        }
        catch
        {
            _cpuCounter = null;
        }

        _cpuName = ReadCpuName();
        _totalRamBytes = ReadTotalPhysicalMemory();
    }

    public object Snapshot(string? serverPath)
    {
        float cpu = 0;
        try
        {
            if (_cpuCounter is not null)
            {
                cpu = _cpuCounter.NextValue();
                if (!_cpuPrimed)
                    _cpuPrimed = true;
            }
        }
        catch
        {
            cpu = 0;
        }

        ulong avail = ReadAvailablePhysicalMemory();
        ulong total = _totalRamBytes;
        ulong used = total > avail ? total - avail : 0;

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
            // ignore
        }

        return new
        {
            cpuName = _cpuName ?? "Unknown CPU",
            cpuUsage = Math.Round(cpu, 1),
            ramTotalBytes = total,
            ramUsedBytes = used,
            ramAvailableBytes = avail,
            ramTotalGb = Math.Round(total / (1024.0 * 1024 * 1024), 2),
            ramUsedGb = Math.Round(used / (1024.0 * 1024 * 1024), 2),
            diskRoot = driveRoot,
            diskTotalBytes = diskTotal,
            diskUsedBytes = diskUsed,
            diskFreeBytes = diskFree,
            diskTotalGb = Math.Round(diskTotal / (1024.0 * 1024 * 1024), 1),
            diskUsedGb = Math.Round(diskUsed / (1024.0 * 1024 * 1024), 1),
            diskFreeGb = Math.Round(diskFree / (1024.0 * 1024 * 1024), 1)
        };
    }

    public void Dispose()
    {
        _cpuCounter?.Dispose();
        _cpuCounter = null;
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
