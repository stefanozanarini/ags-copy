using AgsCopy.Core.Models;

namespace AgsCopy.Core.Robocopy;

/// <summary>What the machine has free when a run starts.</summary>
public sealed record Resources(int LogicalCores, double CpuLoad, long AvailableRamBytes);

public sealed record TuneResult(int Processes, int Threads, Resources Resources)
{
    public int TotalThreads => Processes * Threads;
}

/// <summary>
/// Picks robocopy processes and threads from the free CPU and RAM. Robocopy threads spend most
/// of their time waiting on disks and network, so several per free core pay off, more when
/// one side is on the network; memory caps the total.
/// </summary>
public static class AutoTune
{
    /// <summary>Rough cost of one robocopy process, and of each of its threads.</summary>
    public const long BytesPerProcess = 64L * 1024 * 1024;
    public const long BytesPerThread = 4L * 1024 * 1024;

    /// <summary>Copies may use at most this share of the free RAM.</summary>
    public const double RamShare = 0.25;

    public const int MaxAutoProcesses = 8;
    public const int MinThreads = 4;
    public const int MaxTotalThreads = 256;

    /// <param name="network">The source or the destination is a network path.</param>
    /// <param name="topLevelFolders">Folders directly inside the source: parallel processes split by them.</param>
    public static TuneResult Compute(Resources r, bool network, int topLevelFolders)
    {
        var freeCores = Math.Max(1.0, Math.Max(1, r.LogicalCores) * (1 - Math.Clamp(r.CpuLoad, 0, 1)));

        var wanted = (int)Math.Round(freeCores * (network ? 8 : 4));
        wanted = Math.Clamp(wanted, MinThreads, MaxTotalThreads);

        // Separate processes help only with several folders to share out: about one every two free cores.
        var processes = topLevelFolders >= 2
            ? Math.Clamp((int)(freeCores / 2), 1, Math.Min(MaxAutoProcesses, topLevelFolders + 1))
            : 1;

        var budget = (long)(Math.Max(0, r.AvailableRamBytes) * RamShare);
        // Processes may take at most half of the budget, the rest goes to threads.
        while (processes > 1 && processes * BytesPerProcess > budget / 2) processes--;
        var threadsByRam = (int)Math.Clamp((budget - processes * BytesPerProcess) / BytesPerThread, processes, int.MaxValue);

        var total = Math.Min(wanted, threadsByRam);
        var threads = Math.Clamp(total / processes, 1, CopyOptions.MaxThreads);
        return new TuneResult(processes, threads, r);
    }
}
