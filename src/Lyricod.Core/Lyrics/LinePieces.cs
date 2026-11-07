using System.Globalization;
using System.Text;

namespace Lyricod.Core;

/// <summary>
/// Splits a lyric line that's too wide for its space: into rows (the lyrics window) or into pieces
/// shown one after another (the desktop strip and the island), each starting when its first word
/// is sung.
/// </summary>
public static class LinePieces
{
    /// <summary>
    /// <paramref name="measure"/> gives a string's drawn width. Lines that fit come back whole.
    /// Otherwise the line is broken into as few pieces as fit, as even as they can be, where a
    /// reader would break: at spaces, after punctuation, between words rather than inside them.
    /// A piece never starts with a closing mark ("」", "、") or ends with an opening one ("「").
    /// Word timing is kept (a word split between pieces shares its time by length); a line without
    /// it gets times spread over its characters.
    /// </summary>
    public static List<LyricLine> Split(LyricLine line, double maxWidth, Func<string, double> measure)
    {
        if (maxWidth <= 0 || measure(line.Text) <= maxWidth) return [line];
        bool timed = line.Words is { Count: > 0 };
        var words = timed ? line.Words!.ToList() : EstimatedWords(line);
        string text = string.Concat(words.Select(w => w.Text));
        var starts = RowStarts(text, maxWidth, measure);
        if (starts.Count <= 1) return [line];

        // Each row runs from its start to the next row's start; the space between them is trimmed.
        var rows = new List<List<LyricWord>>();
        for (int r = 0; r < starts.Count; r++) rows.Add([]);
        int at = 0, row = 0;
        foreach (var word in words)
        {
            int end = at + word.Text.Length;
            for (int r = row; r < starts.Count; r++)
            {
                int from = Math.Max(at, starts[r]), to = Math.Min(end, r + 1 < starts.Count ? starts[r + 1] : text.Length);
                if (from >= to) continue;
                rows[r].Add(from == at && to == end ? word : Part(word, from - at, to - at));
                if (to == end) break;
            }
            at = end;
            while (row + 1 < starts.Count && starts[row + 1] <= at) row++;
        }

        var pieces = new List<LyricLine>();
        for (int r = 0; r < rows.Count; r++)
        {
            var piece = rows[r];
            while (piece.Count > 0 && piece[0].Text.TrimStart() is var head && head.Length < piece[0].Text.Length)
            {
                if (head.Length > 0) { piece[0] = piece[0] with { Text = head }; break; }
                piece.RemoveAt(0);
            }
            while (piece.Count > 0 && piece[^1].Text.TrimEnd() is var tail && tail.Length < piece[^1].Text.Length)
            {
                if (tail.Length > 0) { piece[^1] = piece[^1] with { Text = tail }; break; }
                piece.RemoveAt(piece.Count - 1);
            }
            if (piece.Count == 0) continue;
            pieces.Add(new LyricLine
            {
                Id = line.Id, Start = pieces.Count == 0 ? line.Start : piece[0].Start, End = line.End,
                Text = string.Concat(piece.Select(w => w.Text)), Words = timed ? piece : null,
                Translation = line.Translation, IsCredit = line.IsCredit,
            });
        }
        for (int i = 0; i + 1 < pieces.Count; i++) pieces[i] = pieces[i] with { End = pieces[i + 1].Start };
        return pieces.Count > 1 ? pieces : [line];
    }

    /// <summary>Characters <paramref name="from"/> to <paramref name="to"/> of a word, with its share of the word's time.</summary>
    static LyricWord Part(LyricWord word, int from, int to)
    {
        double length = Math.Max(word.Text.Length, 1);
        return new LyricWord(word.Start + word.Duration * from / length, word.Duration * (to - from) / length, word.Text[from..to]);
    }

    /// <summary>The piece being sung at <paramref name="t"/> (the first one before the line starts).</summary>
    public static LyricLine? Current(IReadOnlyList<LyricLine> pieces, double t) =>
        pieces.LastOrDefault(p => p.Start <= t) ?? pieces.FirstOrDefault();

    // ---- Where to break ----

    /// <summary>
    /// A stretch of text that's never broken inside: a run of Latin letters, Hangul, digits and the
    /// like (those break at spaces), or one Chinese or Japanese character. <c>Gap</c> is where the
    /// spaces before it start.
    /// </summary>
    readonly record struct Token(int Start, int Length, int Gap);

    /// <summary>
    /// Where each row of <paramref name="text"/> starts (character indexes, the first 0): the fewest
    /// rows no wider than <paramref name="maxWidth"/>, then the most even ones at the best breaks.
    /// </summary>
    public static List<int> RowStarts(string text, double maxWidth, Func<string, double> measure)
    {
        var tokens = Tokens(text, maxWidth, measure);
        int n = tokens.Count;
        if (n <= 1) return [0];

        // Widths: each token's, and the spaces before it (dropped where a row starts).
        var spaces = new Dictionary<string, double>();
        double Space(string s) => spaces.TryGetValue(s, out var w) ? w : spaces[s] = Math.Max(measure("x" + s + "x") - measure("xx"), 0);
        var sum = new double[n + 1];
        var gap = new double[n];
        for (int k = 0; k < n; k++)
        {
            var t = tokens[k];
            gap[k] = k == 0 ? 0 : Space(text[t.Gap..t.Start]);
            sum[k + 1] = sum[k] + gap[k] + measure(text.Substring(t.Start, t.Length));
        }
        double Width(int first, int last) => sum[last + 1] - sum[first] - gap[first];
        bool Fits(int first, int last) => first == last || Width(first, last) <= maxWidth;

        var cost = new double[n];
        for (int k = 1; k < n; k++) cost[k] = BreakCost(text, tokens[k - 1], tokens[k]);

        // The fewest rows: rows[j] for the first j tokens.
        var fewest = new int[n + 1];
        Array.Fill(fewest, int.MaxValue);
        fewest[0] = 0;
        for (int j = 1; j <= n; j++)
            for (int i = 0; i < j; i++)
                if (fewest[i] != int.MaxValue && (i == 0 || !double.IsPositiveInfinity(cost[i])) && Fits(i, j - 1))
                    fewest[j] = Math.Min(fewest[j], fewest[i] + 1);
        int count = fewest[n];
        if (count == int.MaxValue || count <= 1) return [0];

        // That many rows, as even as they can be (a lone character on a row costs a lot), at the
        // cheapest breaks.
        double even = Width(0, n - 1) / count;
        double RowCost(int first, int last) => 100 * Math.Pow((Width(first, last) - even) / maxWidth, 2);
        var best = new double[count + 1, n + 1];
        var from = new int[count + 1, n + 1];
        for (int r = 0; r <= count; r++)
            for (int j = 0; j <= n; j++) best[r, j] = double.PositiveInfinity;
        best[0, 0] = 0;
        for (int r = 1; r <= count; r++)
            for (int j = 1; j <= n; j++)
                for (int i = r - 1; i < j; i++)
                {
                    if (double.IsPositiveInfinity(best[r - 1, i]) || !Fits(i, j - 1)) continue;
                    double c = best[r - 1, i] + RowCost(i, j - 1) + (i > 0 ? cost[i] : 0);
                    if (c < best[r, j]) { best[r, j] = c; from[r, j] = i; }
                }
        if (double.IsPositiveInfinity(best[count, n])) return [0];
        var starts = new List<int>();
        for (int r = count, j = n; r > 0; j = from[r, j], r--) starts.Add(tokens[from[r, j]].Start);
        starts.Reverse();
        starts[0] = 0;
        return starts;
    }

    /// <summary>The text's tokens. A run too wide for a row on its own is split into its characters, as a last resort.</summary>
    static List<Token> Tokens(string text, double maxWidth, Func<string, double> measure)
    {
        var tokens = new List<Token>();
        int end = 0, run = -1, runGap = 0;
        void CloseRun(int at)
        {
            if (run < 0) return;
            tokens.Add(new Token(run, at - run, runGap));
            end = at;
            run = -1;
        }
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            string ch = e.GetTextElement();
            int at = e.ElementIndex;
            if (char.IsWhiteSpace(ch[0])) { CloseRun(at); continue; }
            if (Kind(ch) != Script.Other)
            {
                CloseRun(at);
                tokens.Add(new Token(at, ch.Length, end));
                end = at + ch.Length;
                continue;
            }
            if (run < 0) { run = at; runGap = end; }
        }
        CloseRun(text.Length);

        var split = new List<Token>();
        foreach (var t in tokens)
        {
            if (t.Length == 1 || measure(text.Substring(t.Start, t.Length)) <= maxWidth) { split.Add(t); continue; }
            var chars = StringInfo.GetTextElementEnumerator(text.Substring(t.Start, t.Length));
            while (chars.MoveNext())
            {
                int at = t.Start + chars.ElementIndex;
                split.Add(new Token(at, chars.GetTextElement().Length, at == t.Start ? t.Gap : at));
            }
        }
        return split;
    }

    enum Script { Hiragana, Katakana, Han, Mark, Other }

    /// <summary>
    /// What breaking between two tokens costs (lower is better, infinity is never): nothing at a
    /// space; little after punctuation or before an opening mark; more inside a run of Chinese or
    /// Japanese characters, most where a word likely goes on (a kanji and its kana ending, the
    /// middle of a katakana word); a lot inside a word too wide for a row.
    /// </summary>
    static double BreakCost(string text, Token before, Token after)
    {
        if (after.Gap < after.Start) return 0;
        string last = LastElement(text.Substring(before.Start, before.Length)), first = FirstElement(text.Substring(after.Start, after.Length));
        if (NoBreakAfter(last) || NoBreakBefore(first)) return double.PositiveInfinity;
        if (IsPunctuation(last) || Opens(first)) return 1;
        Script a = Kind(last), b = Kind(first);
        if (a == Script.Other && b == Script.Other) return 20; // inside a word too wide for a row
        if (a == Script.Other || b == Script.Other) return 3;
        return (a, b) switch
        {
            (Script.Hiragana, Script.Han or Script.Katakana) => 3,
            (Script.Katakana, Script.Han) => 3,
            (Script.Han, Script.Katakana) => 4,
            (Script.Han, Script.Han) => 5,
            (Script.Hiragana, Script.Hiragana) => 6,
            (Script.Katakana, Script.Hiragana) => 7,
            (Script.Han, Script.Hiragana) => 8,
            (Script.Katakana, Script.Katakana) => 9,
            _ => 6,
        };
    }

    static string FirstElement(string s) => StringInfo.GetNextTextElement(s, 0);

    static string LastElement(string s)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        string last = s;
        while (e.MoveNext()) last = e.GetTextElement();
        return last;
    }

    static Script Kind(string ch)
    {
        if (!Rune.TryGetRuneAt(ch, 0, out var r)) return Script.Other;
        int v = r.Value;
        if (v is (>= 0x3041 and <= 0x309F)) return Script.Hiragana;
        if (v is (>= 0x30A0 and <= 0x30FF) or (>= 0x31F0 and <= 0x31FF) or (>= 0xFF66 and <= 0xFF9F)) return Script.Katakana;
        if (v is (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x2FFFF) or 0x3005) return Script.Han;
        return v is (>= 0x3000 and <= 0x303F) or (>= 0xFF00 and <= 0xFF65) ? Script.Mark : Script.Other;
    }

    /// <summary>Marks a row mustn't start with: closing brackets and quotes, commas and stops, small kana, "ー".</summary>
    static bool NoBreakBefore(string ch)
    {
        if (ch.Length == 1 && "ぁぃぅぇぉっゃゅょゎゕゖァィゥェォッャュョヮヵヶㇰㇱㇲㇳㇴㇵㇶㇷㇸㇹㇺㇻㇼㇽㇾㇿｧｨｩｪｫｯｬｭｮｰー々〻ゝゞヽヾ〜～‐゠–…‥".Contains(ch[0])) return true;
        return Rune.TryGetRuneAt(ch, 0, out var r) && Rune.GetUnicodeCategory(r) is
            UnicodeCategory.ClosePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    }

    /// <summary>Marks a row mustn't end with: opening brackets and quotes.</summary>
    static bool NoBreakAfter(string ch) => Opens(ch);

    static bool Opens(string ch) => Rune.TryGetRuneAt(ch, 0, out var r) && Rune.GetUnicodeCategory(r) is
        UnicodeCategory.OpenPunctuation or UnicodeCategory.InitialQuotePunctuation;

    // ---- Timing without word timing ----

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
