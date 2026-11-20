using System.Globalization;
using System.Text.RegularExpressions;

namespace AgsCopy.Core.Robocopy;

/// <summary>One file or folder line of robocopy output run with /FP /BYTES.</summary>
public readonly record struct RobocopyLine(string Path, long Size, bool IsDirectory);

/// <summary>A row of robocopy's final table: Total, Copied, Skipped, Mismatch, FAILED, Extras.</summary>
public sealed record SummaryRow(long Total, long Copied, long Skipped, long Mismatch, long Failed, long Extras)
{
    public static readonly SummaryRow Empty = new(0, 0, 0, 0, 0, 0);

    public SummaryRow Add(SummaryRow o) =>
        new(Total + o.Total, Copied + o.Copied, Skipped + o.Skipped, Mismatch + o.Mismatch, Failed + o.Failed, Extras + o.Extras);
}

public sealed record RobocopySummary(SummaryRow Dirs, SummaryRow Files, SummaryRow Bytes)
{
    public static readonly RobocopySummary Empty = new(SummaryRow.Empty, SummaryRow.Empty, SummaryRow.Empty);

    public RobocopySummary Add(RobocopySummary o) => new(Dirs.Add(o.Dirs), Files.Add(o.Files), Bytes.Add(o.Bytes));
}

/// <summary>
/// Robocopy translates its labels ("New File", "Nuovo file", "*EXTRA Dir"...), so the parsing
/// here never looks at them. A line is recognised by its shape: a number, a tab, an absolute
/// path. Whether it is something to copy or an extra item is told by the path: source paths are
/// copied, destination paths are extras.
/// </summary>
public static partial class RobocopyOutput
{
    public static RobocopyLine? ParseLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var tab = line.LastIndexOf('\t');
        if (tab <= 0) return null;

        var path = line[(tab + 1)..].TrimEnd('\r', '\n');
        if (!PathUtil.IsAbsolute(path)) return null;

        var meta = line[..tab].TrimEnd();
        var sp = meta.LastIndexOfAny([' ', '\t']);
        var num = sp >= 0 ? meta[(sp + 1)..] : meta;
        if (!long.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var size)) return null;

        return new RobocopyLine(path, size, path.EndsWith('\\'));
    }

    [GeneratedRegex(@"^\s*[^\d:]+:\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$")]
    private static partial Regex SummaryRowRegex();

    public static SummaryRow? ParseSummaryRow(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = SummaryRowRegex().Match(line);
        if (!m.Success) return null;
        long G(int i) => long.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        return new SummaryRow(G(1), G(2), G(3), G(4), G(5), G(6));
    }
}

/// <summary>Collects the Dirs, Files and Bytes rows of the final table, in that order.</summary>
public sealed class SummaryCollector
{
    private readonly List<SummaryRow> _rows = [];

    public void Add(string? line)
    {
        if (RobocopyOutput.ParseSummaryRow(line) is { } row)
        {
            // A new table (another process' output, or a restart) starts over.
            if (_rows.Count == 3) _rows.Clear();
            _rows.Add(row);
        }
    }

    public RobocopySummary? Summary => _rows.Count >= 3 ? new RobocopySummary(_rows[0], _rows[1], _rows[2]) : null;
}

public static class RobocopyExitCode
{
    public const int FilesCopied = 1;
    public const int ExtraItems = 2;
    public const int Mismatched = 4;
    public const int CopyFailed = 8;
    public const int Fatal = 16;

    public static Models.RunOutcome Outcome(int code) =>
        code < 0 || code >= CopyFailed ? Models.RunOutcome.Failed
        : (code & Mismatched) != 0 ? Models.RunOutcome.Warning
        : Models.RunOutcome.Success;
}
