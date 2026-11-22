using AgsCopy.Core.Models;

namespace AgsCopy.Core.Robocopy;

public sealed class PreviewItem
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public bool IsDirectory { get; set; }

    /// <summary>For a folder to delete: files inside it, when counted.</summary>
    public long? Files { get; set; }
}

/// <summary>What a run would do, from a robocopy /L pass.</summary>
public sealed class PreviewResult
{
    public const int MaxCopyItems = 500;
    public const int MaxDeleteItems = 5000;

    public CopyMode Mode { get; set; }
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";

    public long FilesToCopy { get; set; }
    public long BytesToCopy { get; set; }

    /// <summary>Files and folders present only in the destination.</summary>
    public long ExtraFiles { get; set; }
    public long ExtraBytes { get; set; }
    public long ExtraDirs { get; set; }

    public long FilesUnchanged { get; set; }
    public long BytesUnchanged { get; set; }

    public List<PreviewItem> CopyItems { get; set; } = [];
    public List<PreviewItem> ExtraItems { get; set; } = [];
    public bool CopyItemsTruncated { get; set; }
    public bool ExtraItemsTruncated { get; set; }

    public bool DestinationExists { get; set; }
    public int ExitCode { get; set; }
    public string? Error { get; set; }
    public TimeSpan Elapsed { get; set; }

    /// <summary>The robocopy command of the real run, for the curious.</summary>
    public string? CommandLine { get; set; }

    /// <summary>Extras and overwritten files go to the versions folder first.</summary>
    public bool KeepVersions { get; set; }

    /// <summary>Extras are deleted only in Mirror mode.</summary>
    public bool ExtrasDeleted => Mode == CopyMode.Mirror;

    /// <summary>
    /// Adds the preview of another part of the same job (another source, a DFS link) to this one.
    /// </summary>
    public void Merge(PreviewResult other)
    {
        FilesToCopy += other.FilesToCopy;
        BytesToCopy += other.BytesToCopy;
        ExtraFiles += other.ExtraFiles;
        ExtraBytes += other.ExtraBytes;
        ExtraDirs += other.ExtraDirs;
        FilesUnchanged += other.FilesUnchanged;
        BytesUnchanged += other.BytesUnchanged;
        CopyItemsTruncated |= other.CopyItemsTruncated || CopyItems.Count + other.CopyItems.Count > MaxCopyItems;
        ExtraItemsTruncated |= other.ExtraItemsTruncated || ExtraItems.Count + other.ExtraItems.Count > MaxDeleteItems;
        CopyItems.AddRange(other.CopyItems.Take(Math.Max(0, MaxCopyItems - CopyItems.Count)));
        ExtraItems.AddRange(other.ExtraItems.Take(Math.Max(0, MaxDeleteItems - ExtraItems.Count)));
        DestinationExists &= other.DestinationExists;
        ExitCode |= other.ExitCode;
        if (other.Error != null) Error = Error == null ? other.Error : Error + Environment.NewLine + other.Error;
        if (other.CommandLine != null) CommandLine = CommandLine == null ? other.CommandLine : CommandLine + Environment.NewLine + other.CommandLine;
        if (Source != other.Source) Source += Environment.NewLine + other.Source;
        KeepVersions |= other.KeepVersions;
    }
}

/// <summary>Builds a <see cref="PreviewResult"/> from the lines of a robocopy /L run.</summary>
public sealed class PreviewAggregator
{
    private readonly string _source;
    private readonly string _destination;
    private readonly bool _destinationInsideSource;
    private readonly SummaryCollector _summary = new();

    public PreviewResult Result { get; }

    public PreviewAggregator(string source, string destination, CopyMode mode)
    {
        _source = PathUtil.WithSlash(source);
        _destination = PathUtil.WithSlash(destination);
        _destinationInsideSource = PathUtil.IsUnder(_destination, _source);
        Result = new PreviewResult { Mode = mode, Source = PathUtil.NormalizeDir(source), Destination = PathUtil.NormalizeDir(destination) };
    }

    public void Add(string? line)
    {
        _summary.Add(line);
        if (RobocopyOutput.ParseLine(line) is not { } l) return;

        // Check the longer root first, in case one contains the other.
        bool inDest, inSource;
        if (_destinationInsideSource)
        {
            inDest = PathUtil.IsUnder(l.Path, _destination);
            inSource = !inDest && PathUtil.IsUnder(l.Path, _source);
        }
        else
        {
            inSource = PathUtil.IsUnder(l.Path, _source);
            inDest = !inSource && PathUtil.IsUnder(l.Path, _destination);
        }

        var r = Result;
        if (inSource && !l.IsDirectory)
        {
            r.FilesToCopy++;
            r.BytesToCopy += Math.Max(0, l.Size);
            if (r.CopyItems.Count < PreviewResult.MaxCopyItems)
                r.CopyItems.Add(new PreviewItem { Path = l.Path, Size = l.Size });
            else
                r.CopyItemsTruncated = true;
        }
        else if (inDest)
        {
            if (l.IsDirectory) r.ExtraDirs++;
            else
            {
                r.ExtraFiles++;
                r.ExtraBytes += Math.Max(0, l.Size);
            }
            if (r.ExtraItems.Count < PreviewResult.MaxDeleteItems)
                r.ExtraItems.Add(new PreviewItem { Path = l.Path, Size = l.IsDirectory ? 0 : l.Size, IsDirectory = l.IsDirectory });
            else
                r.ExtraItemsTruncated = true;
        }
    }

    /// <summary>Completes the totals with robocopy's own table, when it was printed.</summary>
    public PreviewResult Complete(int exitCode)
    {
        var r = Result;
        r.ExitCode = exitCode;
        if (_summary.Summary is { } s)
        {
            r.FilesUnchanged = s.Files.Skipped;
            r.BytesUnchanged = s.Bytes.Skipped;
            // The table is authoritative for totals; the lines may be fewer with /MT.
            r.FilesToCopy = Math.Max(r.FilesToCopy, s.Files.Copied);
            r.BytesToCopy = Math.Max(r.BytesToCopy, s.Bytes.Copied);
        }
        return r;
    }
}

/// <summary>Counts what a running robocopy has copied, from its /TEE output.</summary>
public sealed class ProgressCounter
{
    private readonly string _source;
    private long _files;
    private long _bytes;

    public ProgressCounter(string source) => _source = PathUtil.WithSlash(source);

    public long Files => Interlocked.Read(ref _files);
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>A file the copy engine wrote (robocopy runs count from its output instead).</summary>
    public void AddFile(long bytes)
    {
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _bytes, bytes);
    }

    public void Add(string? line)
    {
        if (RobocopyOutput.ParseLine(line) is { IsDirectory: false } l && l.Size >= 0 && PathUtil.IsUnder(l.Path, _source))
        {
            Interlocked.Increment(ref _files);
            Interlocked.Add(ref _bytes, l.Size);
        }
    }
}
