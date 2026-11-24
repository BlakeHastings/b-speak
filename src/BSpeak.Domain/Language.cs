using System.Text.RegularExpressions;

namespace BSpeak.Domain;

public enum ControlAction { Pause, Resume }

/// <summary>The phrases that pause and resume listening. Only a whole utterance that is just the phrase counts.</summary>
public sealed record ControlPhrases(IReadOnlyList<string> Pause, IReadOnlyList<string> Resume)
{
    public static ControlPhrases Default { get; } = new(["stop listening", "pause listening"], ["start listening", "resume listening"]);

    private static readonly HashSet<string> LeadingFiller = ["ok", "okay", "hey", "please", "now", "so"];
    private static readonly HashSet<string> TrailingFiller = ["please", "now", "thanks"];

    /// <summary>Every word used in a control phrase. Participant names may not be one of them.</summary>
    public IEnumerable<string> AllWords => Pause.Concat(Resume).SelectMany(Words.Split).Distinct();

    public ControlAction? Match(string utterance)
    {
        var w = Words.Split(utterance).ToList();
        while (w.Count > 0 && LeadingFiller.Contains(w[0])) w.RemoveAt(0);
        if (w.Count >= 2 && w[^2] == "thank" && w[^1] == "you") w.RemoveRange(w.Count - 2, 2);
        while (w.Count > 0 && TrailingFiller.Contains(w[^1])) w.RemoveAt(w.Count - 1);
        var said = string.Join(' ', w);
        if (said.Length == 0) return null;
        if (Pause.Any(p => Words.Normalize(p) == said)) return ControlAction.Pause;
        if (Resume.Any(p => Words.Normalize(p) == said)) return ControlAction.Resume;
        return null;
    }
}

/// <summary>Works out who an utterance is addressed to from how it starts: "Atlas, run the tests".</summary>
public static class AddressResolver
{
    public const string Everyone = "everyone";

    private static readonly HashSet<string> Filler = ["hey", "hi", "ok", "okay", "so", "and", "um", "uh", "oh", "yo"];
    private static readonly HashSet<string> EveryoneWords = ["everyone", "everybody"];

    /// <summary>
    /// Returns the participant's name, <see cref="Everyone"/>, or null when the utterance names nobody.
    /// An exact match wins; otherwise a single close match (a likely transcription error) is accepted.
    /// </summary>
    public static string? Resolve(string utterance, IEnumerable<string> participants)
    {
        var words = Words.Split(utterance).ToList();
        while (words.Count > 0 && Filler.Contains(words[0])) words.RemoveAt(0);
        if (words.Count == 0) return null;
        if (EveryoneWords.Contains(words[0])) return Everyone;

        var close = new List<string>();
        foreach (var name in participants)
        {
            var nameWords = Words.Split(name);
            if (nameWords.Length == 0 || words.Count < nameWords.Length) continue;
            var said = string.Join(' ', words.Take(nameWords.Length));
            if (said == string.Join(' ', nameWords)) return name;
            if (Words.SoundsAlike(said, name)) close.Add(name);
        }
        return close.Count == 1 ? close[0] : null;
    }
}

/// <summary>Rules for a participant's name: short, speakable, and not confusable with control words.</summary>
public static partial class ParticipantNames
{
    public const int MaxLength = 24;

    private static readonly HashSet<string> Reserved = ["everyone", "everybody", "all", "hey", "ok", "okay", "you", "me", "host"];

    [GeneratedRegex(@"^\p{L}[\p{L}' -]{0,23}$")]
    private static partial Regex Speakable();

    public static string Require(string name, ControlPhrases phrases)
    {
        name = (name ?? "").Trim();
        if (!Speakable().IsMatch(name))
            throw new DomainViolation("name-format", $"'{name}' is not a speakable name. Use 1 to {MaxLength} letters, for example Atlas.");
        var reserved = Reserved.Concat(phrases.AllWords).ToHashSet();
        var clash = Words.Split(name).FirstOrDefault(reserved.Contains);
        if (clash is not null)
            throw new DomainViolation("name-reserved", $"'{name}' uses the reserved word '{clash}'. Pick another name.");
        return name;
    }
}

/// <summary>Checks that text reads as speech, and splits it into sentences.</summary>
public static partial class SpokenText
{
    private static readonly (Regex Pattern, string Found)[] Markdown =
    [
        (new Regex("```"), "a code fence"),
        (new Regex("`[^`\n]+`"), "inline code"),
        (new Regex(@"^\s*#{1,6}\s", RegexOptions.Multiline), "a heading"),
        (new Regex(@"^\s*([-*+•]|\d+[.)])\s+", RegexOptions.Multiline), "a list item"),
        (new Regex(@"^\s*\|.*\|\s*$", RegexOptions.Multiline), "a table row"),
        (new Regex(@"\*\*[^*]+\*\*|__[^_]+__"), "bold markup"),
        (new Regex(@"\[[^\]]+\]\([^)]+\)"), "a markdown link"),
        (new Regex(@"https?://|www\.", RegexOptions.IgnoreCase), "a raw URL"),
    ];

    [GeneratedRegex(@"(?<=[.!?…])\s+|\n+")]
    private static partial Regex SentenceBreak();

    /// <summary>Describes the first Markdown construct found ("a list item"), or null for plain speech.</summary>
    public static string? FindMarkdown(string text) =>
        Markdown.FirstOrDefault(m => m.Pattern.IsMatch(text)).Found;

    public static IReadOnlyList<string> Sentences(string text) =>
        SentenceBreak().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
}
