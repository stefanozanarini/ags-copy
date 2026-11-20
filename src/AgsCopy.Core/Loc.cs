using System.Globalization;

namespace AgsCopy.Core;

/// <summary>
/// Interface texts in Italian (default) and English. Missing English texts fall back to Italian,
/// missing keys show as [key].
/// </summary>
public static partial class Loc
{
    public static readonly string[] Languages = ["it", "en"];

    public static string Language { get; set; } = "it";

    public static CultureInfo Culture => CultureOf(Language);

    public static CultureInfo CultureOf(string? lang) => CultureInfo.GetCultureInfo(lang == "en" ? "en-GB" : "it-IT");

    public static string T(string key) => Tl(Language, key);

    public static string F(string key, params object?[] args) => Fl(Language, key, args);

    public static string Tl(string? lang, string key)
    {
        var table = lang == "en" ? En : It;
        if (table.TryGetValue(key, out var s)) return s;
        return It.TryGetValue(key, out var f) ? f : "[" + key + "]";
    }

    public static string Fl(string? lang, string key, params object?[] args) =>
        string.Format(CultureOf(lang), Tl(lang, key), args);

    internal static IReadOnlyDictionary<string, string> Table(string lang) => lang == "en" ? En : It;
}
