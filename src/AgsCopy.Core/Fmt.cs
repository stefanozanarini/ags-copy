using System.Globalization;

namespace AgsCopy.Core;

public static class Fmt
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes, CultureInfo? culture = null)
    {
        culture ??= Loc.Culture;
        double v = bytes;
        var u = 0;
        while (Math.Abs(v) >= 1024 && u < Units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0
            ? bytes.ToString("N0", culture) + " B"
            : v.ToString(v < 10 ? "0.##" : v < 100 ? "0.#" : "0", culture) + " " + Units[u];
    }

    public static string Count(long n, CultureInfo? culture = null) => n.ToString("N0", culture ?? Loc.Culture);

    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{Math.Max(0, t.Seconds)}s";
    }

    public static string DateTime(DateTime local, CultureInfo? culture = null) =>
        local.ToString("g", culture ?? Loc.Culture);
}
