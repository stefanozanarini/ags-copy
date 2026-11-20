using System.Runtime.InteropServices;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.Service;

/// <summary>Free CPU and RAM right now, for <see cref="AutoTune"/>.</summary>
public static class ResourceProbe
{
    /// <summary>Samples the CPU load over half a second.</summary>
    public static async Task<Resources> MeasureAsync(CancellationToken ct)
    {
        var load = 0.0;
        if (GetSystemTimes(out var idle1, out var kernel1, out var user1))
        {
            await Task.Delay(500, ct);
            if (GetSystemTimes(out var idle2, out var kernel2, out var user2))
            {
                // Kernel time includes idle time.
                var total = (kernel2 - kernel1) + (user2 - user1);
                if (total > 0) load = 1 - (double)(idle2 - idle1) / total;
            }
        }
        return new Resources(Environment.ProcessorCount, Math.Clamp(load, 0, 1), AvailableRam());
    }

    private static long AvailableRam()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.ullAvailPhys : 2L * 1024 * 1024 * 1024;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
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
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
