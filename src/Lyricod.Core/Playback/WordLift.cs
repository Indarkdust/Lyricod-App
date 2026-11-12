using System.Text;

namespace Lyricod.Core;

/// <summary>A stretch of a row that rises as one: characters <c>From</c> up to <c>To</c> of its text, sung from <c>Start</c> to <c>End</c>.</summary>
public readonly record struct LiftWord(int From, int To, double Start, double End);

/// <summary>Where a <see cref="LiftWord"/> is drawn: the row's width it takes (to halfway through the
/// space on either side) and the middle of its letters, in the row's pixels.</summary>
public readonly record struct LiftSpan(double Left, double Right, double Centre);

/// <summary>
/// The lift: each word of the line being sung rises a little and grows as it's sung, then settles
/// back. This says which stretches of a row move as one, where they are, and how far up each is at
/// any moment; the lyrics window draws it.
/// </summary>
public static class WordLift
{
    /// <summary>How long a word takes to come back down (seconds).</summary>
    public const double Settle = 0.5;

    /// <summary>
    /// The row's words that rise, covering its whole text between them. A source may time a word a
    /// syllable at a time ("beau", "ti", "ful"): those rise together, as the word they spell. Chinese,
    /// Japanese and Korean characters each rise alone. Marks and spaces timed by themselves go with
    /// the word before them (the first word's go with it). Empty without word timing.
    /// </summary>
    public static List<LiftWord> Words(LyricLine piece)
    {
        var words = new List<LiftWord>();
        if (piece.Words is not { Count: > 0 } timed) return words;
        int at = 0;
        // Whether the last word could go on into the next: it didn't end in a space or a character that stands alone.
        bool open = false;
        foreach (var word in timed)
        {
            int end = at + word.Text.Length;
            string text = word.Text;
            if (!text.Any(char.IsLetterOrDigit))
            {
                if (words.Count > 0) words[^1] = words[^1] with { To = end };
                if (text.Any(char.IsWhiteSpace)) open = false;
            }
            else
            {
                string sung = text.Trim();
                bool joins = open && words.Count > 0 && !char.IsWhiteSpace(text[0]) && !StandsAlone(sung, 0);
                if (joins) words[^1] = words[^1] with { To = end, End = Math.Max(words[^1].End, word.End) };
                else words.Add(new LiftWord(words.Count == 0 ? 0 : at, end, word.Start, word.End));
                open = !char.IsWhiteSpace(text[^1]) && !StandsAlone(sung, LastRune(sung));
            }
            at = end;
        }
        return words;
    }

    /// <summary>
    /// Where each of <paramref name="words"/> is in the row. <paramref name="measure"/> gives a
    /// string's drawn width (it leaves out spaces at the end, so those are measured against a letter).
    /// </summary>
    public static List<LiftSpan> Spans(IReadOnlyList<LiftWord> words, string text, Func<string, double> measure)
    {
        double X(int index)
        {
            string head = text[..Math.Clamp(index, 0, text.Length)];
            return head.Length > 0 && char.IsWhiteSpace(head[^1]) ? Math.Max(measure(head + "x") - measure("x"), 0) : measure(head);
        }
        // Each word's letters, without the spaces around them.
        var letters = new List<(double From, double To)>();
        foreach (var word in words)
        {
            int from = Math.Clamp(word.From, 0, text.Length), to = Math.Clamp(word.To, from, text.Length);
            while (from < to && char.IsWhiteSpace(text[from])) from++;
            while (to > from && char.IsWhiteSpace(text[to - 1])) to--;
            letters.Add((X(from), X(to)));
        }
        var spans = new List<LiftSpan>();
        for (int k = 0; k < words.Count; k++)
        {
            double left = k == 0 ? 0 : (letters[k - 1].To + letters[k].From) / 2;
            double right = k + 1 < words.Count ? (letters[k].To + letters[k + 1].From) / 2 : X(text.Length);
            spans.Add(new LiftSpan(left, Math.Max(right, left), (letters[k].From + letters[k].To) / 2));
        }
        return spans;
    }

    /// <summary>
    /// How far up <paramref name="word"/> is at <paramref name="t"/>, from 0 (at rest) to 1 (a long
    /// word at its top). It rises as it's sung, quickly at first; stays up while it's held; then
    /// eases back down. A quick syllable rises less, so fast passages ripple rather than jump.
    /// </summary>
    public static double At(LiftWord word, double t)
    {
        double rise = Rise(word), top = Top(word);
        if (t <= word.Start || t >= top + Settle) return 0;
        double height = Height(word);
        if (t < word.Start + rise)
        {
            double x = 1 - (t - word.Start) / rise;
            return height * (1 - x * x * x);
        }
        if (t <= top) return height;
        double y = (t - top) / Settle;
        return height * (1 - (y < 0.5 ? 4 * y * y * y : 1 - Math.Pow(-2 * y + 2, 3) / 2));
    }

    /// <summary>
    /// The rest of <paramref name="word"/>'s lift from <paramref name="position"/>, as keyframes:
    /// seconds from now and how far up it is at each (linear between them is close enough). The
    /// first is now. Just that one when the word is already back down.
    /// </summary>
    public static (List<double> Times, List<double> Values) Plan(LiftWord word, double position)
    {
        var times = new List<double> { 0 };
        var values = new List<double> { At(word, position) };
        double rise = Rise(word), top = Top(word);
        void Add(double t)
        {
            if (t - position <= times[^1] + 0.001) return;
            times.Add(t - position);
            values.Add(At(word, t));
        }
        Add(word.Start);
        for (int k = 1; k <= RiseSteps; k++) Add(word.Start + rise * k / RiseSteps);
        Add(top);
        for (int k = 1; k <= SettleSteps; k++) Add(top + Settle * k / SettleSteps);
        return (times, values);
    }

    const int RiseSteps = 6, SettleSteps = 10;

    /// <summary>How long the word takes to reach its top: as long as it's sung, within reason.</summary>
    static double Rise(LiftWord word) => Math.Clamp(word.End - word.Start, 0.16, 0.45);

    /// <summary>When the word starts back down: when it's been sung (or has reached its top, if that's later).</summary>
    static double Top(LiftWord word) => Math.Max(word.End, word.Start + Rise(word));

    static double Height(LiftWord word) => 0.55 + 0.45 * Math.Clamp((word.End - word.Start) / 0.4, 0, 1);

    static int LastRune(string s) => s.Length >= 2 && char.IsLowSurrogate(s[^1]) && char.IsHighSurrogate(s[^2]) ? s.Length - 2 : s.Length - 1;

    /// <summary>Whether the character at <paramref name="index"/> is one that's a word (or a syllable block) by itself: Chinese, kana, Hangul.</summary>
    static bool StandsAlone(string s, int index)
    {
        if (index < 0 || index >= s.Length || !Rune.TryGetRuneAt(s, index, out var rune)) return false;
        int v = rune.Value;
        return v is (>= 0x3040 and <= 0x30FF) or (>= 0x31F0 and <= 0x31FF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF)
            or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF) or (>= 0xFF66 and <= 0xFF9F) or (>= 0x20000 and <= 0x2FFFF);
    }
}
