using System.Text.RegularExpressions;

namespace BSpeak.Domain;

/// <summary>Helpers for comparing spoken words as a transcript renders them: case and punctuation ignored.</summary>
public static partial class Words
{
    [GeneratedRegex(@"[^\p{L}\p{N}']+")]
    private static partial Regex NonWord();

    /// <summary>Lowercases, turns punctuation into spaces and collapses whitespace: "Atlas, run!" becomes "atlas run".</summary>
    public static string Normalize(string text) => NonWord().Replace(text.ToLowerInvariant(), " ").Trim();

    public static string[] Split(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// True when two names are close enough that speech to text could confuse them: one edit apart,
    /// or two for names of six letters or more.
    /// </summary>
    public static bool SoundsAlike(string a, string b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x == y) return true;
        var tolerance = Math.Min(x.Length, y.Length) >= 6 ? 2 : 1;
        return Distance(x, y) <= tolerance;
    }

    /// <summary>Levenshtein edit distance.</summary>
    public static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// True when <paramref name="text"/> starts with the words of <paramref name="prefix"/>.
    /// <paramref name="rest"/> is the original text after those words, with leading punctuation removed.
    /// </summary>
    public static bool StartsWith(string text, string prefix, out string rest)
    {
        rest = text;
        var want = Split(prefix);
        var have = Split(text);
        if (want.Length == 0 || have.Length < want.Length) return false;
        for (var i = 0; i < want.Length; i++)
            if (have[i] != want[i]) return false;
        rest = DropLeadingWords(text, want.Length);
        return true;
    }

    /// <summary>Removes the first <paramref name="count"/> words and any punctuation around them.</summary>
    public static string DropLeadingWords(string text, int count)
    {
        var m = Regex.Match(text, @"^\W*(?:[\p{L}\p{N}']+[^\p{L}\p{N}']*){" + count + "}");
        return m.Success ? text[m.Length..].Trim() : text.Trim();
    }
}
