using System.Text;
using Lyricod.Core;

namespace Lyricod.Core.Romanization;

/// <summary>
/// Romaji by word (experimental, Settings > Lyrics): Japanese romaji spaced by word rather than by
/// syllable. A source's romaji ("hi ka re a u no wa na ze", as QQ Music times it) is spaced by the
/// words Windows' analyser finds ("hikare au no wa naze"), and a kanji's kana ending the analyser
/// gives apart stays with it ("naku", not "na ku"). Where the source timed its romaji (QQ Music),
/// each syllable goes with the word being sung when it starts; otherwise the two romaji are lined
/// up by letters. Off, Romanizer.cs works as before.
/// </summary>
public sealed partial class Romanizer
{
    public bool GroupJapaneseByWord { get; set; }

    /// <summary>A source's romaji for <paramref name="line"/> spaced by the words worked out here; null when there's no reading here or the two don't line up.</summary>
    Pronunciation? ByWord(Pronunciation given, LyricLine line)
    {
        if (Reader is { } reader && given.Words is { } syllables && line.Words is { } sung
            && WordSpans(line.Text, reader) is { } spans && BySungWord(syllables, line, sung, spans) is { } timed)
            return timed;
        if (Read(line.Text, LyricLanguage.Japanese) is not { } ours || Regroup(given.Text, ours.Text) is not { } grouped) return null;
        // Word for word with ours, it's sung when ours is: timed from the line's own words.
        var starts = WordStarts(grouped);
        bool same = starts.Count == ours.Words.Count && WordStarts(ours.Text).SequenceEqual(ours.Words.Select(w => w.At));
        return new Pronunciation(grouped, LyricLanguage.Japanese, PronunciationOrigin.SourceByWord)
        {
            Words = same ? Sung(grouped, starts, ours.Words, line) : null,
        };
    }

    /// <summary>How far a syllable may start before the sung word it belongs to: the two timings differ by a millisecond.</summary>
    const double Leeway = 0.003;

    /// <summary>
    /// The source's timed syllables spaced by the line's words (<paramref name="spans"/>: where
    /// each is in the line's text): a syllable belongs to the word whose character is being sung
    /// when it starts (<paramref name="sung"/>, the line word by word). A doubled consonant written
    /// as a syllable of its own ("'t" before "ta") is just a letter inside the word: "yokatta".
    /// Null when a syllable isn't in the line.
    /// </summary>
    internal static Pronunciation? BySungWord(IReadOnlyList<LyricWord> syllables, LyricLine line, IReadOnlyList<LyricWord> sung,
        List<(int From, int To)> spans)
    {
        if (sung.Count == 0 || line.Text.Length == 0) return null;
        // Where each sung word starts in the line's text (the text is the words, trimmed).
        string all = string.Concat(sung.Select(w => w.Text));
        if (all.Trim() != line.Text) return null;
        var starts = new int[sung.Count];
        for (int i = 0, at = all.TrimStart().Length - all.Length; i < sung.Count; at += sung[i++].Text.Length)
            starts[i] = at + (sung[i].Text.Length - sung[i].Text.TrimStart().Length);

        var words = new List<LyricWord>(syllables.Count);
        // The word the syllable before was in (a place between words counts as its own), and whether
        // the next syllable goes straight after an opening bracket.
        int last = int.MinValue, k = 0;
        bool glued = false;
        foreach (var syllable in syllables)
        {
            while (k + 1 < sung.Count && sung[k + 1].Start <= syllable.Start + Leeway) k++;
            if (syllable.Start + Leeway < sung[k].Start || syllable.Start > Math.Max(line.End, sung[k].End) + Leeway) return null;
            int c = Math.Clamp(starts[k], 0, line.Text.Length - 1);
            int word = spans.FindIndex(s => c >= s.From && c < s.To);
            int group = word >= 0 ? word : -1 - c;
            string text = syllable.Text.Trim();
            if (text.Length == 0) continue;
            bool mark = !text.Any(char.IsLetterOrDigit), opens = mark && "([{“‘\"".Contains(text[0]);
            // A space between words; none before a closing mark, or after an opening one.
            bool space = words.Count > 0 && !glued && (mark ? opens : group != last);
            if (!mark && group == last && text.Length == 2 && text[0] == '\'' && char.IsLetter(text[1])) text = text[1..];
            words.Add(syllable with { Text = space ? " " + text : text });
            glued = opens;
            last = group;
        }
        return words.Count == 0 ? null
            : new Pronunciation(string.Concat(words.Select(w => w.Text)), LyricLanguage.Japanese, PronunciationOrigin.SourceByWord) { Words = words };
    }

    /// <summary>
    /// <paramref name="source"/>'s romaji spaced by the words of <paramref name="ours"/>. Their letters
    /// are lined up, which copes with readings that differ here and there (明日 as "asu" or "ashita"),
    /// and spaces go where ours break between words. The source's spelling stays. Null when the two
    /// are too different to line up.
    /// </summary>
    public static string? Regroup(string source, string ours)
    {
        // Letters only, lowercase and without accents; where each of ours starts a word.
        var theirs = new List<(char Letter, int At)>();
        var spacedBefore = new HashSet<int>();
        for (int c = 0; c < source.Length; c++)
        {
            if (!char.IsLetterOrDigit(source[c])) continue;
            if (c > 0 && !char.IsLetterOrDigit(source[c - 1])) spacedBefore.Add(theirs.Count);
            theirs.Add((Plain(source[c]), c));
        }
        var mine = new StringBuilder();
        var starts = new List<int>();
        bool between = false;
        foreach (char c in ours)
        {
            if (!char.IsLetterOrDigit(c)) { between = mine.Length > 0; continue; }
            if (between) starts.Add(mine.Length);
            between = false;
            mine.Append(Plain(c));
        }
        int n = mine.Length, m = theirs.Count;
        if (n == 0 || m == 0) return null;

        // The fewest letters changed, added or left out to turn ours into theirs.
        var cost = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) cost[i, 0] = i;
        for (int j = 0; j <= m; j++) cost[0, j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                cost[i, j] = Math.Min(cost[i - 1, j - 1] + (mine[i - 1] == theirs[j - 1].Letter ? 0 : 1), Math.Min(cost[i - 1, j], cost[i, j - 1]) + 1);
        if (cost[n, m] > Math.Max(2, Math.Max(n, m) * 0.35)) return null;
        var at = new int[n];
        var after = new int[n];
        for (int i = n, j = m; i > 0;)
        {
            if (j > 0 && cost[i, j] == cost[i - 1, j - 1] + (mine[i - 1] == theirs[j - 1].Letter ? 0 : 1)) { i--; j--; at[i] = j; after[i] = j + 1; }
            else if (cost[i, j] == cost[i - 1, j] + 1) { i--; at[i] = after[i] = j; }
            else j--;
        }
        // Letters of theirs that ours lacks at a break between words go with the word their own
        // spacing puts them in ("kyou wa"), or else with the word after ("he" for へ as "e").
        var breaks = new HashSet<int>();
        foreach (int s in starts)
        {
            int from = after[s - 1], to = Math.Max(from, at[s]);
            int k = Enumerable.Range(from, to - from + 1).Where(spacedBefore.Contains).DefaultIfEmpty(from).First();
            if (k > 0 && k < m) breaks.Add(k);
        }

        // Their text again, with a space between letters only where ours breaks; spaces next to punctuation stay.
        var sb = new StringBuilder();
        bool spaced = false;
        int letter = 0;
        foreach (char c in source)
        {
            if (char.IsWhiteSpace(c)) { spaced = true; continue; }
            bool isLetter = char.IsLetterOrDigit(c);
            if (sb.Length > 0)
            {
                bool afterLetter = char.IsLetterOrDigit(sb[^1]);
                if (isLetter && afterLetter ? breaks.Contains(letter) : spaced) sb.Append(' ');
            }
            sb.Append(c);
            spaced = false;
            if (isLetter) letter++;
        }
        return sb.ToString();
    }

    static char Plain(char c) => char.ToLowerInvariant(c.ToString().Normalize(NormalizationForm.FormD)[0]);

    /// <summary>Particles and helpers that stay words of their own after a kanji (音|は "oto wa", 離れ|て "hanare te"), unlike a kanji's own kana ending.</summary>
    static readonly HashSet<string> OwnWords = [
        "は", "が", "を", "に", "へ", "と", "で", "も", "の", "や", "か", "ね", "よ", "な", "ぞ", "さ", "わ", "ば", "て", "た", "だ", "ん",
        "から", "まで", "より", "けど", "けれど", "ので", "のに", "ても", "でも", "だけ", "しか", "ばかり", "など", "なら", "たら", "たり",
        "ながら", "ほど", "くらい", "ぐらい", "さえ", "こそ", "すら", "ずつ", "って", "とか", "ます", "ました", "ません", "ない", "なかっ",
        "たい", "です", "でし", "だろう", "でしょう", "でしょ", "じゃ", "ちゃ", "よう", "そう", "らしい", "みたい"];

    /// <summary>A kanji's kana ending that the analyser gives as a word of its own (泣|く, 憂|き) but belongs with it: one kana, or an adjective's ending.</summary>
    static bool IsEnding(string surface) =>
        !OwnWords.Contains(surface) && surface.All(KanaRomaji.IsKana) && (surface.Length == 1 || surface is "しい" or "しく" or "しき");

    static bool EndsWithKanji(string s) => s.Length > 0 && !KanaRomaji.IsKana(s[^1]) && char.IsLetter(s[^1]);

    /// <summary>
    /// Joins a kanji's kana ending to the word before it (泣|く "naku"); a small っ ending goes on with
    /// the next word instead (待|っ|て "matte"). Returns whether the word was taken.
    /// </summary>
    static bool JoinsEnding(List<Part> parts, ref string pending, ref int pendingFrom, ref string? lastSurface, ref string? lastReading,
        string surface, string reading, bool phraseStart, int to)
    {
        if (phraseStart || pending.Length > 0 || lastSurface is not { } before || lastReading is not { } said
            || !EndsWithKanji(before) || !IsEnding(surface)) return false;
        var joined = said + reading;
        if (joined.EndsWith('っ') || joined.EndsWith('ッ'))
        {
            pendingFrom = parts[^1].From;
            parts.RemoveAt(parts.Count - 1);
            pending = joined;
            lastSurface = null;
            return true;
        }
        (lastSurface, lastReading) = (before + surface, joined);
        parts[^1] = parts[^1] with { Text = KanaRomaji.Convert(joined), To = to };
        return true;
    }
}
