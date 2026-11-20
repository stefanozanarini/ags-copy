using System.Diagnostics;
using System.Globalization;
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
