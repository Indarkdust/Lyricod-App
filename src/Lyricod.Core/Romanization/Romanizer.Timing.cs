using Lyricod.Core;

namespace Lyricod.Core.Romanization;

/// <summary>
/// When a pronunciation is sung, where the source didn't say: from the line's own word timing.
/// Each word of a pronunciation worked out here reads some characters of the line (a pinyin
/// syllable its character, a romaji word its kanji and kana, a romanised Korean word its Hangul),
/// so it's sung from when the first of them starts until the last has been. A source's romaji
/// spaced by the words found here (romaji by word) is timed the same way, word for word.
/// </summary>
public sealed partial class Romanizer
{
    /// <summary>A word of a pronunciation: where it starts in the pronunciation's text, and the
    /// characters of the line it reads (<c>From</c> up to <c>To</c>; -1 when not known).</summary>
    record struct Placed(int At, int From, int To);

    /// <summary>
    /// <paramref name="text"/> (a pronunciation of <paramref name="line"/>) as words with their
    /// timing, to be lit as it's sung: one for each of <paramref name="starts"/> (where a word
    /// starts in the text), with the space before it and any marks after it (the first also what's
    /// before it), sung when <paramref name="reads"/> says its characters are. Their texts make up
    /// the text.
    /// Null when the line isn't timed by word, or a word's characters aren't known.
    /// </summary>
    static List<LyricWord>? Sung(string text, List<int> starts, List<Placed> reads, LyricLine line)
    {
        if (starts.Count == 0 || starts.Count != reads.Count || SungClock.For(line) is not { } clock) return null;
        var words = new List<LyricWord>(starts.Count);
        double last = double.NegativeInfinity;
        // Where a word's piece of the text begins: at the space before it, as timed romaji has it.
        int Begin(int k)
        {
            if (k == 0) return 0;
            if (k >= starts.Count) return text.Length;
            int at = Math.Clamp(starts[k], 0, text.Length);
            while (at > 0 && char.IsWhiteSpace(text[at - 1])) at--;
            return at;
        }
        for (int k = 0; k < starts.Count; k++)
        {
            var (_, from, to) = reads[k];
            int begin = Begin(k), end = Begin(k + 1);
            if (from < 0 || to <= from || to > line.Text.Length || begin >= end) return null;
            // Never before the word before it, whatever the line's timing says.
            double start = Math.Max(clock.StartOf(from), last);
            words.Add(new LyricWord(start, Math.Max(clock.EndOf(to) - start, 0), text[begin..end]));
            last = start;
        }
        return words;
    }

    /// <summary>Where each word of <paramref name="text"/> starts: what spaces set apart, with a letter or digit in it.</summary>
    static List<int> WordStarts(string text)
    {
        var starts = new List<int>();
        for (int i = 0; i < text.Length;)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int from = i;
            bool word = false;
            for (; i < text.Length && !char.IsWhiteSpace(text[i]); i++) word |= char.IsLetterOrDigit(text[i]);
            if (word) starts.Add(from);
        }
        return starts;
    }

    /// <summary>When each character of a line is sung, from the line's words as the source timed them.</summary>
    sealed class SungClock
    {
        /// <summary>Each timed word with the characters it is in the line's text (<c>From</c> up to <c>To</c>).</summary>
        readonly List<(LyricWord Word, int From, int To)> _words = [];
        readonly double _end;

        SungClock(double end) => _end = end;

        /// <summary>Null when the line has no word timing, or its words don't make up its text.</summary>
        public static SungClock? For(LyricLine line)
        {
            if (line.Words is not { Count: > 0 } sung || line.Text.Length == 0) return null;
            string all = string.Concat(sung.Select(w => w.Text));
            if (all.Trim() != line.Text) return null;
            var clock = new SungClock(Math.Max(line.End, sung[^1].End));
            // The text is the words, trimmed.
            int at = all.TrimStart().Length - all.Length;
            foreach (var word in sung)
            {
                int from = at + (word.Text.Length - word.Text.TrimStart().Length), to = at + word.Text.TrimEnd().Length;
                at += word.Text.Length;
                from = Math.Clamp(from, 0, line.Text.Length);
                to = Math.Clamp(to, 0, line.Text.Length);
                if (to > from) clock._words.Add((word, from, to));
            }
            return clock._words.Count > 0 ? clock : null;
        }

        /// <summary>When the character at <paramref name="index"/> starts to be sung: partway through a
        /// word timed as one, by how far into it the character is; at a space, when the next word starts.</summary>
        public double StartOf(int index)
        {
            foreach (var (word, from, to) in _words)
            {
                if (index < from) return word.Start;
                if (index < to) return word.Start + word.Duration * (index - from) / (to - from);
            }
            return _end;
        }

        /// <summary>When the characters before <paramref name="index"/> have been sung.</summary>
        public double EndOf(int index)
        {
            double end = _words[0].Word.Start;
            foreach (var (word, from, to) in _words)
            {
                if (index <= from) break;
                end = index >= to ? word.End : word.Start + word.Duration * (index - from) / (to - from);
            }
            return end;
        }
    }
}
