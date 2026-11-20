using AgsCopy.Core.Models;

namespace AgsCopy.Core.Robocopy;

public enum RobocopyPass
{
    /// <summary>The whole tree in one process.</summary>
    Full,
    /// <summary>
    /// Parallel runs: files in the root folder only. In Mirror mode it also purges destination
    /// folders the source no longer has, while the source's own folders are left to their passes.
    /// </summary>
    Root,
    /// <summary>Parallel runs: one top-level folder with everything below it.</summary>
    Folder,
}

public static class RobocopyArgs
{
    /// <summary>Windows' limit is 32767 characters; stay well below it.</summary>
    public const int MaxCommandLine = 30000;

    /// <param name="rootExcludedDirs">For <see cref="RobocopyPass.Root"/>: names of the source's top-level folders.</param>
    public static List<string> Build(
        CopyJob job,
        string source,
        string destination,
        RobocopyPass pass,
        bool listOnly,
        string? unicodeLog,
        bool tee,
        IReadOnlyCollection<string>? rootExcludedDirs = null)
    {
        var o = job.Options;
        var a = new List<string> { PathUtil.NormalizeDir(source), PathUtil.NormalizeDir(destination) };

        switch (job.Mode, pass)
        {
            case (CopyMode.Copy, RobocopyPass.Root):
                break; // root files only: no /E
            case (CopyMode.Copy, _):
                a.Add("/E");
                break;
            case (CopyMode.Mirror, _):
                a.Add("/MIR");
                break;
        }

        a.Add(o.FileProperties switch
        {
            FileProperties.WithSecurity => "/COPY:DATS",
            FileProperties.All => "/COPY:DATSOU",
            _ => "/COPY:DAT",
        });
        a.Add("/DCOPY:DAT");
        a.Add($"/MT:{Math.Clamp(o.Threads, 1, CopyOptions.MaxThreads)}");
        a.Add($"/R:{Math.Max(0, o.Retries)}");
        a.Add($"/W:{Math.Max(0, o.RetryWaitSeconds)}");
        if (o.UnbufferedIO) a.Add("/J");
        if (o.BackupMode) a.Add("/ZB");
        if (o.ExcludeOlder) a.Add("/XO");
        if (o.SkipJunctions) a.Add("/XJ");
        if (o.FatFileTimes) a.Add("/FFT");

        var xf = o.ExcludeFiles.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (xf.Count > 0)
        {
            a.Add("/XF");
            a.AddRange(xf);
        }

        var xd = o.ExcludeDirectories.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        // Always, even with versions turned off: a mirror must never purge the kept versions.
        xd.Add(Versions.FolderName);
        if (pass == RobocopyPass.Root && rootExcludedDirs != null) xd.AddRange(rootExcludedDirs);
        if (xd.Count > 0)
        {
            a.Add("/XD");
            a.AddRange(xd.Select(PathUtil.NormalizeDir));
        }

        // Output: full paths and exact sizes are what the parsers rely on.
        a.Add("/FP");
        a.Add("/BYTES");
        a.Add("/NP");
        if (listOnly) a.Add("/L");
        if (unicodeLog != null) a.Add("/UNILOG:" + unicodeLog);
        if (tee) a.Add("/TEE");

        if (!string.IsNullOrWhiteSpace(o.ExtraArguments))
        {
            var forbidden = SourceWritingSwitches(o.ExtraArguments);
            if (forbidden.Count > 0)
                throw new InvalidOperationException("Switches that modify the source are not allowed: " + string.Join(' ', forbidden));
            a.AddRange(CommandLine.Split(o.ExtraArguments));
        }

        return a;
    }

    /// <summary>
    /// Robocopy switches that write to the source: /MOV and /MOVE delete it after copying,
    /// /M clears its archive attribute. AGS-Copy only ever reads the source.
    /// </summary>
    private static readonly string[] SourceWriting = ["/MOV", "/MOVE", "/M"];

    public static List<string> SourceWritingSwitches(string? extraArguments) =>
        string.IsNullOrWhiteSpace(extraArguments)
            ? []
            : CommandLine.Split(extraArguments)
                .Where(x => SourceWriting.Contains(x.Trim(), StringComparer.OrdinalIgnoreCase))
                .ToList();

    /// <summary>Approximate command line length once quoted.</summary>
    public static int Length(IEnumerable<string> args) =>
        "robocopy.exe".Length + args.Sum(x => x.Length + 3);
}
