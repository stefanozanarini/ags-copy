using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AgsCopy.Core.Robocopy;

public static class RobocopyProcess
{
    static RobocopyProcess() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string ExePath => Path.Combine(Environment.SystemDirectory, "robocopy.exe");

    /// <summary>Robocopy writes to a pipe in the OEM code page (850 on Italian Windows).</summary>
    public static Encoding ConsoleEncoding
    {
        get
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch (ArgumentException) { return Encoding.Latin1; }
        }
    }

    /// <summary>
    /// The same string after a trip through the console encoding, so it can be compared with
    /// paths read from robocopy's output (characters the code page lacks become '?' in both).
    /// </summary>
    public static string AsConsoleText(string s)
    {
        var e = ConsoleEncoding;
        return e.GetString(e.GetBytes(s));
    }

    /// <summary>Runs robocopy and returns its exit code. Cancelling kills it.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
        p.Start();
        ChildProcesses.Adopt(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        p.WaitForExit(); // drains the output events
        return p.ExitCode;
    }
}

/// <summary>
/// A Windows job object that ends with this process: robocopy processes put in it die with the service,
/// even when it crashes or is killed, instead of copying on unattended next to the run that replaces them.
/// </summary>
public static class ChildProcesses
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private static readonly Lazy<IntPtr> Job = new(Create);

    public static void Adopt(Process p)
    {
        if (!OperatingSystem.IsWindows() || Job.Value == IntPtr.Zero) return;
        try
        {
            AssignProcessToJobObject(Job.Value, p.Handle);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimit>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            SetInformationJobObject(job, ExtendedLimitInformation, buffer, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return job; // never closed: the system closes it when this process ends
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
