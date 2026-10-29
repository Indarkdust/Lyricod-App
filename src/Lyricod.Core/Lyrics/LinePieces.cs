using System.Globalization;
using System.Text;

namespace Lyricod.Core;

/// <summary>
/// Splits a lyric line that's too wide for a narrow display (the island) into pieces shown
/// one after another, each starting when its first word is sung.
/// </summary>
public static class LinePieces
{
    /// <summary>
    /// <paramref name="measure"/> gives a string's drawn width. Lines that fit come back whole.
    /// Pieces are balanced (two halves, not "everything but one word"), break between words,
    /// and keep word timing when the line has it; otherwise timing is estimated by spreading
    /// the line's duration over its characters.
    /// </summary>
    public static List<LyricLine> Split(LyricLine line, double maxWidth, Func<string, double> measure)
    {
        double total = measure(line.Text);
        if (total <= maxWidth || maxWidth <= 0) return [line];
        bool timed = line.Words != null;
        var tokens = line.Words?.ToList() ?? EstimatedWords(line);
        if (tokens.Count <= 1) return [line];

        int count = (int)Math.Ceiling(total / maxWidth);
        double target = Math.Min(total / count + 8, maxWidth);
        var groups = new List<List<LyricWord>> { new() };
        foreach (var token in tokens)
        {
            var current = groups[^1];
            var candidate = string.Concat(current.Select(w => w.Text)) + token.Text;
            if (current.Count > 0 && measure(candidate.Trim(' ')) > target) groups.Add([]);
            groups[^1].Add(token);
        }

        var pieces = new List<LyricLine>();
        for (int i = 0; i < groups.Count; i++)
        {
            var words = groups[i].ToList();
            words[0] = words[0] with { Text = words[0].Text.TrimStart(' ') };
            words[^1] = words[^1] with { Text = words[^1].Text.TrimEnd(' ') };
            double end = i + 1 < groups.Count ? groups[i + 1][0].Start : line.End;
            pieces.Add(new LyricLine
            {
                Id = line.Id, Start = i == 0 ? line.Start : words[0].Start, End = end,
                Text = string.Concat(words.Select(w => w.Text)), Words = timed ? words : null,
                Translation = line.Translation, IsCredit = line.IsCredit,
            });
        }
        return pieces;
    }

    /// <summary>The piece being sung at <paramref name="t"/> (the first one before the line starts).</summary>
    public static LyricLine? Current(IReadOnlyList<LyricLine> pieces, double t) =>
        pieces.LastOrDefault(p => p.Start <= t) ?? pieces.FirstOrDefault();

    /// <summary>
    /// Word-like tokens for a line without word timing: each CJK character, or a run of other
    /// characters up to and including a space; times spread evenly.
    /// </summary>
    public static List<LyricWord> EstimatedWords(LyricLine line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(line.Text);
        while (e.MoveNext())
        {
            var ch = e.GetTextElement();
            if (IsCjk(ch))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                tokens.Add(ch);
            }
            else if (ch == " ")
            {
                if (current.Length == 0 && tokens.Count > 0) tokens[^1] += ch; else current.Append(ch);
                tokens.Add(current.ToString()); current.Clear();
            }
            else if (IsPunctuation(ch) && current.Length == 0 && tokens.Count > 0)
            {
                tokens[^1] += ch; // keep "，" with the word before it
            }
            else current.Append(ch);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        tokens = tokens.Where(t => t.Length > 0).ToList();

        static int Len(string s) => new StringInfo(s).LengthInTextElements;
        double duration = line.End > line.Start ? line.End - line.Start : Len(line.Text) * 0.3;
        double totalChars = Math.Max(tokens.Sum(Len), 1);
        int before = 0;
        var words = new List<LyricWord>();
        foreach (var token in tokens)
        {
            words.Add(new LyricWord(line.Start + duration * before / totalChars, duration * Len(token) / totalChars, token));
            before += Len(token);
        }
        return words;
    }

    static bool IsPunctuation(string ch) => Rune.TryGetRuneAt(ch, 0, out var r) && Rune.GetUnicodeCategory(r) is
        UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
        or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
        or UnicodeCategory.OtherPunctuation;

    static bool IsCjk(string ch) => ch.EnumerateRunes().Any(r => r.Value is (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x9FFF)
        or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF));
}
