using System.Globalization;
using System.Text.RegularExpressions;

namespace AgsCopy.Core.Robocopy;

public sealed record CopyError(string Path, int Code, string Action, string Message);

/// <summary>
/// Collects robocopy's errors from its log. An error is a timestamped line with a code
/// ("2026/11/20 10:15:32 ERROR 32 (0x00000020) Copying File C:\a.pst"; the words are
/// translated, the shape is not), followed by the Windows message on the next line. Retries
/// repeat the same error: one entry per path is kept, with its last code.
/// </summary>
public sealed partial class ErrorCollector
{
    public const int MaxErrors = 5000;

    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CopyError> _errors = [];
    private CopyError? _pending;

    public IReadOnlyList<CopyError> Errors => _errors;

    /// <summary>Distinct paths in error, including those beyond <see cref="MaxErrors"/>.</summary>
    public int Count { get; private set; }

    [GeneratedRegex(@"^\s*\S+\s+\d{1,2}:\d{2}:\d{2}\s+\S+\s+(\d+)\s+\(0x[0-9A-Fa-f]{8}\)\s+(.*)$")]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"[A-Za-z]:\\|\\\\")]
    private static partial Regex PathStart();

    public void Add(string? line)
    {
        if (line == null) return;
        var m = ErrorLine().Match(line);
        if (m.Success)
        {
            Flush();
            var rest = m.Groups[2].Value.Trim();
            var p = PathStart().Match(rest);
            var action = p.Success ? rest[..p.Index].Trim() : rest;
            var path = p.Success ? rest[p.Index..].Trim() : "";
            _pending = new CopyError(path, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), action, "");
            return;
        }

        // The Windows message is the first non-empty line after the error.
        if (_pending != null && !string.IsNullOrWhiteSpace(line))
        {
            _pending = _pending with { Message = line.Trim() };
            Flush();
        }
    }

    /// <summary>Call after the last line.</summary>
    public void Complete() => Flush();

    private void Flush()
    {
        if (_pending is not { } e) return;
        _pending = null;
        if (_index.TryGetValue(e.Path, out var i))
        {
            _errors[i] = e.Message.Length > 0 || _errors[i].Message.Length == 0 ? e : e with { Message = _errors[i].Message };
            return;
        }
        Count++;
        if (_errors.Count >= MaxErrors) return;
        _index[e.Path] = _errors.Count;
        _errors.Add(e);
    }
}
