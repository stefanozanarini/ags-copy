using System.Text;

namespace AgsCopy.Core.Robocopy;

public static class CommandLine
{
    /// <summary>Splits on whitespace, keeping "quoted parts" together (quotes removed).</summary>
    public static List<string> Split(string text)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (any) result.Add(cur.ToString());
                cur.Clear();
                any = false;
            }
            else
            {
                cur.Append(ch);
                any = true;
            }
        }
        if (any) result.Add(cur.ToString());
        return result;
    }

    /// <summary>Quotes an argument for display, the way it would be typed in cmd.</summary>
    public static string Quote(string arg) =>
        arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? "\"" + arg + "\"" : arg;

    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));
}
