using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lyricod.Core;

/// <summary>Turns each source's lyric text into <see cref="Lyrics"/>.</summary>
public static partial class LyricParser
{
    const double MaxTime = 24 * 60 * 60;

    static bool ValidTime(double value) => double.IsFinite(value) && value >= 0 && value <= MaxTime;
    static bool ValidSpan(double start, double duration) => ValidTime(start) && ValidTime(duration) && ValidTime(start + duration);

    static readonly string[] NewLines = ["\r\n", "\n", "\r"];

    static string[] SplitLines(string s) => s.Split(NewLines, StringSplitOptions.None);

    // ---- LRC ----

    /// <summary>
    /// Parses LRC into (time, text) pairs, sorted. Also understands NetEase's JSON
    /// credit lines: {"t":1000,"c":[{"tx":"作曲: "},{"tx":"周杰伦"}]}.
    /// </summary>
    public static List<(double Time, string Text)> ParseLrcEntries(string lrc)
    {
        var entries = new List<(double, string)>();
        foreach (var raw in SplitLines(lrc))
        {
            var line = raw.Trim();
            if (line.StartsWith('{'))
            {
                if (ParseJsonLine(line) is { } credit) entries.Add(credit);
                continue;
            }
            var rest = line.AsSpan();
            var times = new List<double>();
            while (rest.StartsWith("[") && rest.IndexOf(']') is var close and > 0)
            {
                if (ParseTimestamp(rest[1..close]) is { } t) times.Add(t);
                rest = rest[(close + 1)..];
            }
            var text = rest.ToString().Trim();
            foreach (var t in times) entries.Add((t, text));
        }
        return [.. entries.OrderBy(e => e.Item1)]; // stable, like the Mac version
    }

    /// <summary>"mm:ss.xx", "mm:ss.xxx" or "mm:ss".</summary>
    public static double? ParseTimestamp(ReadOnlySpan<char> tag)
    {
        int colon = tag.IndexOf(':');
        if (colon < 0 || tag[(colon + 1)..].Contains(':')) return null;
        var sec = tag[(colon + 1)..].ToString().Replace(',', '.');
        if (!double.TryParse(tag[..colon], NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return null;
        if (!double.TryParse(sec, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return null;
        double time = m * 60 + s;
        return ValidTime(m) && ValidTime(s) && ValidTime(time) ? time : null;
    }

    static (double, string)? ParseJsonLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number) return null;
            if (!root.TryGetProperty("c", out var c) || c.ValueKind != JsonValueKind.Array) return null;
            var text = string.Concat(c.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("tx", out var tx) && tx.ValueKind == JsonValueKind.String
                    ? tx.GetString() : null)
                .Where(s => s != null));
            double time = t.GetDouble() / 1000;
            return ValidTime(time) ? (time, text) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Builds display lines from LRC. Empty entries act as "line ends here" markers.</summary>
    public static Lyrics? ParseLrc(string lrc, string? translation, string source)
    {
        var entries = ParseLrcEntries(lrc);
        if (entries.Count == 0) return PlainText(string.Join("\n", SplitLines(lrc).Where(l => !l.TrimStart().StartsWith('['))), source);
        var lines = new List<LyricLine>();
        for (int i = 0; i < entries.Count; i++)
        {
            var (start, entryText) = entries[i];
            if (entryText.Length == 0) continue;
            double end = i + 1 < entries.Count ? entries[i + 1].Time : Math.Min(start + 5, MaxTime);
            // Enhanced LRC carries word timings inline: "<00:05.49>词<00:06.50>曲".
            var words = ParseEnhancedWords(entryText, start, end);
            var text = words != null ? string.Concat(words.Select(w => w.Text)).Trim() : EnhancedTag().Replace(entryText, "").Trim();
            if (text.Length == 0) continue;
            lines.Add(new LyricLine { Id = lines.Count, Start = start, End = end, Text = text, Words = words, IsCredit = IsCreditLine(text) });
        }
        if (lines.Count == 0) return null;
        var lyrics = new Lyrics(lines, source);
        if (translation != null) AttachTranslation(translation, lyrics);
        return lyrics;
    }

    [GeneratedRegex(@"<([^<>]*:[^<>]*)>")]
    private static partial Regex EnhancedTag();

    /// <summary>Splits an enhanced-LRC line into timed words, or null if it has no word tags.</summary>
    public static List<LyricWord>? ParseEnhancedWords(string text, double lineStart, double lineEnd)
    {
        var tags = EnhancedTag().Matches(text);
        if (tags.Count == 0 || !ValidTime(lineStart) || !ValidTime(lineEnd) || lineEnd < lineStart) return null;
        double previous = lineStart;
        foreach (Match tag in tags)
        {
            if (ParseTimestamp(tag.Groups[1].Value) is not { } time || time < previous) return null;
            previous = time;
        }
        var words = new List<LyricWord>();
        // Text before the first tag ("Hello <00:02.00>world") starts with the line; the first tag ends it.
        var prefix = text[..tags[0].Index];
        if (prefix.Trim().Length > 0) words.Add(new LyricWord(lineStart, Math.Max(lineEnd - lineStart, 0), prefix));
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            if (ParseTimestamp(tag.Groups[1].Value) is not { } start) continue;
            int textStart = tag.Index + tag.Length;
            int textEnd = i + 1 < tags.Count ? tags[i + 1].Index : text.Length;
            var word = text[textStart..textEnd];
            // Close the previous word at this tag; the last tag often just marks where the final word ends.
            if (words.Count > 0) words[^1] = words[^1] with { Duration = Math.Max(start - words[^1].Start, 0) };
            if (word.Length == 0) continue;
            words.Add(new LyricWord(start, Math.Max(lineEnd - start, 0), word));
        }
        return words.Count == 0 ? null : words;
    }

    /// <summary>Unsynced lyrics: shown as a static list.</summary>
    public static Lyrics? PlainText(string text, string source)
    {
        var rows = SplitLines(text).Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('{')) // stray NetEase JSON metadata
            .ToList();
        if (rows.Count == 0) return null;
        var lines = rows.Select((t, i) => new LyricLine { Id = i, Text = t, IsCredit = IsCreditLine(t) }).ToList();
        return new Lyrics(lines, source, isSynced: false);
    }

    // ---- Credits ----

    [GeneratedRegex(@"^\s*(作词|作詞|作曲|编曲|編曲|制作人|监制|混音|录音|和声|吉他|贝斯|鼓|弦乐|母带|出品|发行|企划|词|曲|OP|SP|Lyricist|Lyrics|Composer|Producer|Arranger|Written by)[^:：]{0,16}[:：]",
        RegexOptions.IgnoreCase)]
    private static partial Regex CreditPattern();

    /// <summary>
    /// A short Chinese role ending like an instrument or a job, then a colon:
    /// "小号：古嘉䜣", "长笛：宫崎千佳", "录音师：…". End-of-song musician credits name more
    /// roles than the list above covers.
    /// </summary>
    [GeneratedRegex(@"^\s*[㐀-鿿豈-﫿]{0,5}(号|笛|琴|鼓|箫|萧|吉他|贝斯|键盘|合成器|和音|配唱|统筹|协力|师|工程|助理|设计|摄影|版权|演奏|室|乐队|乐团|指挥|监制|策划|编写|出品人)\s*[:：]\s*\S.{0,48}$")]
    private static partial Regex RoleCreditPattern();

    /// <summary>English job titles before a colon: "Guitar：…", "Recording &amp; Mix：…", "Composed by：…". Japanese releases list the whole crew this way; QQ Music's English songs open with it.</summary>
    [GeneratedRegex(@"^\s*(?:[A-Za-z]+\s+){0,2}(?:Produc|Record|Mix|Master|Guitar|Bass|Drum|Piano|Keyboard|Synth|String|Violin|Viola|Cello|Chorus|Vocal|Programm|Engineer|Direct|Arrang|Compos|Lyric|Words|Music|Illustrat|Movie|Video|Design|Label|Publish|Writ)[A-Za-z]*(?:\s+by)?(?:\s*(?:&|and|/)\s*[A-Za-z]+)*\s*[:：]\s*\S",
        RegexOptions.IgnoreCase)]
    private static partial Regex EnglishRolePattern();

    public static bool IsCreditLine(string text) =>
        CreditPattern().IsMatch(text) || RoleCreditPattern().IsMatch(text) || EnglishRolePattern().IsMatch(text);

    // ---- YRC (NetEase word-by-word) ----

    [GeneratedRegex(@"^\[([^,\]]+),([^,\]]+)\]")]
    private static partial Regex TimedLine();

    // Keep ordinary parentheses in the lyric, including ones with commas.
    [GeneratedRegex(@"\((?=\s*[+-]?(?:\d|NaN|Infinity)|[^(),]*,\s*[+-]?(?:\d|NaN|Infinity)|[^(),]*,[^(),]*,\s*-?\d+\))([^(),]*),([^(),]*)(?:,([^()]*))?\)", RegexOptions.IgnoreCase)]
    private static partial Regex WordTag();

    static double Ms(Group g) => double.TryParse(g.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms / 1000 : double.NaN;

    static bool ValidWords(List<LyricWord> words)
    {
        double previous = 0;
        foreach (var word in words)
        {
            if (!ValidSpan(word.Start, word.Duration) || word.Start < previous) return false;
            previous = word.Start;
        }
        return true;
    }

    /// <summary>Format: [lineStartMs,lineDurMs](wordStartMs,wordDurMs,0)word(…)word</summary>
    public static Lyrics? ParseYrc(string yrc, string? translation, string source)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in SplitLines(yrc))
        {
            var line = raw.Trim();
            if (line.StartsWith('{'))
            {
                if (ParseJsonLine(line) is (double t, string text) && text.Length > 0)
                    lines.Add(new LyricLine { Start = t, End = Math.Min(t + 1, MaxTime), Text = text, IsCredit = true });
                continue;
            }
            var m = TimedLine().Match(line);
            if (!m.Success) continue;
            double start = Ms(m.Groups[1]), dur = Ms(m.Groups[2]);
            if (!ValidSpan(start, dur)) continue;
            var markers = WordTag().Matches(line, m.Index + m.Length);
            var words = new List<LyricWord>();
            for (int i = 0; i < markers.Count; i++)
            {
                var w = markers[i];
                int textStart = w.Index + w.Length;
                int textEnd = i + 1 < markers.Count ? markers[i + 1].Index : line.Length;
                var text = line[textStart..textEnd];
                if (text.Length == 0) continue;
                words.Add(new LyricWord(Ms(w.Groups[1]), Ms(w.Groups[2]), text));
            }
            RepairMissingSpaces(words);
            bool validWords = markers.Count > 0 && ValidWords(words)
                && line[m.Length..markers[0].Index].Trim().Length == 0
                && markers.All(w => ValidSpan(Ms(w.Groups[1]), Ms(w.Groups[2])) && int.TryParse(w.Groups[3].Value, out _));
            var joined = validWords ? string.Concat(words.Select(w => w.Text)).Trim() : WordTag().Replace(line[m.Length..], "").Trim();
            if (joined.Length == 0) continue;
            lines.Add(new LyricLine { Start = start, End = start + dur, Text = joined, Words = validWords ? words : null, IsCredit = IsCreditLine(joined) });
        }
        return Finish(lines, translation, source);
    }

    static Lyrics? Finish(List<LyricLine> lines, string? translation, string source)
    {
        if (lines.Count == 0) return null;
        var sorted = lines.OrderBy(l => l.Start).Select((l, i) => l with { Id = i }).ToList();
        var lyrics = new Lyrics(sorted, source);
        if (translation != null) AttachTranslation(translation, lyrics);
        return lyrics;
    }

    /// <summary>
    /// NetEase YRC sometimes drops the space after a word ("I'm(…)going "). When a line
    /// is clearly split per word (most boundaries carry a space), restore the missing ones
    /// between two Latin words. Syllable-split and CJK lines are untouched.
    /// </summary>
    public static void RepairMissingSpaces(List<LyricWord> words)
    {
        if (words.Count <= 2) return;
        int boundaries = words.Count - 1;
        int spaced = words.Take(boundaries).Count(w => w.Text.Length == 0 || char.IsWhiteSpace(w.Text[^1]));
        if (spaced * 2 <= boundaries) return;
        for (int i = 0; i < boundaries; i++)
        {
            string cur = words[i].Text, next = words[i + 1].Text;
            if (cur.Length == 0 || next.Length == 0) continue;
            char a = cur[^1], b = next[0];
            if (!char.IsWhiteSpace(a) && !char.IsWhiteSpace(b) && IsLatin(a) && char.IsAscii(b) && char.IsLetterOrDigit(b))
                words[i] = words[i] with { Text = cur + " " };
        }
    }

    static bool IsLatin(char c) => char.IsAscii(c) && (char.IsLetterOrDigit(c) || c is '\'' or ',' or '!' or '?');

    // ---- QRC (QQ Music word-by-word) ----

    /// <summary>
    /// The lyric text inside a decrypted QRC document
    /// (<c>&lt;Lyric_1 LyricType="1" LyricContent="…"/&gt;</c>), or the input if it isn't one.
    /// </summary>
    public static string QrcContent(string doc)
    {
        const string open = "LyricContent=\"";
        int start = doc.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return doc;
        start += open.Length;
        // The content can hold unescaped quotes, so end at the element's close.
        int close = doc.LastIndexOf("\"/>", StringComparison.Ordinal);
        var content = close >= start ? doc[start..close] : doc[start..];
        return UnescapeHtml(content);
    }

    /// <summary>Format: [lineStartMs,lineDurMs]word(wordStartMs,wordDurMs)word(…). Each word's timing follows it.</summary>
    public static Lyrics? ParseQrc(string qrc, string? translation, string source)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in SplitLines(QrcContent(qrc)))
        {
            var line = raw.Trim();
            var m = TimedLine().Match(line);
            if (!m.Success) continue;
            double start = Ms(m.Groups[1]), dur = Ms(m.Groups[2]);
            if (!ValidSpan(start, dur)) continue;
            var words = new List<LyricWord>();
            int textStart = m.Index + m.Length;
            var markers = WordTag().Matches(line, textStart);
            foreach (Match w in markers)
            {
                var text = line[textStart..w.Index];
                textStart = w.Index + w.Length;
                if (text.Length == 0) continue;
                words.Add(new LyricWord(Ms(w.Groups[1]), Ms(w.Groups[2]), text));
            }
            bool validWords = markers.Count > 0 && ValidWords(words)
                && line[textStart..].Trim().Length == 0 && markers.All(w => ValidSpan(Ms(w.Groups[1]), Ms(w.Groups[2])) && !w.Groups[3].Success);
            var joined = validWords ? string.Concat(words.Select(w => w.Text)).Trim() : WordTag().Replace(line[m.Length..], "").Trim();
            if (joined.Length == 0) continue;
            lines.Add(new LyricLine { Start = start, End = start + dur, Text = joined, Words = validWords ? words : null, IsCredit = IsCreditLine(joined) });
        }
        return Finish(lines, translation, source);
    }

    // ---- Translation and romanisation ----

    static int? NearestLine(Lyrics lyrics, double t)
    {
        if (lyrics.Lines.Count == 0) return null;
        int best = 0;
        for (int i = 1; i < lyrics.Lines.Count; i++)
            if (Math.Abs(lyrics.Lines[i].Start - t) < Math.Abs(lyrics.Lines[best].Start - t)) best = i;
        return Math.Abs(lyrics.Lines[best].Start - t) < 1.0 ? best : null;
    }

    /// <summary>
    /// Matches each translated LRC entry to the original line starting closest to it.
    /// YRC and LRC timings differ by a few hundred ms, so this is nearest-match, not exact.
    /// </summary>
    public static void AttachTranslation(string lrc, Lyrics lyrics)
    {
        foreach (var (t, text) in ParseLrcEntries(lrc))
        {
            if (text.Length == 0 || text == "//") continue;
            if (NearestLine(lyrics, t) is int i && lyrics.Lines[i].Translation == null)
                lyrics.Lines[i] = lyrics.Lines[i] with { Translation = text };
        }
    }

    /// <summary>
    /// Attaches a source's romanisation the same way: LRC (NetEase) or word-timed QRC
    /// (QQ Music, one syllable per word), each entry to the nearest line.
    /// </summary>
    public static void AttachPronunciation(string text, Lyrics lyrics)
    {
        IEnumerable<(double, string)> entries = ParseQrc(text, null, "") is { } qrc
            ? qrc.Lines.Select(l => (l.Start, string.Join(' ', (l.Words ?? []).Select(w => w.Text.Trim()).Where(w => w.Length > 0))))
            : ParseLrcEntries(text);
        foreach (var (t, raw) in entries)
        {
            var tidy = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            // Romanisation is Latin letters; QQ heads its own with a Chinese notice.
            if (tidy.Length == 0 || tidy == "//" || tidy.Any(c => c >= '⺀' && c != '　')) continue;
            if (NearestLine(lyrics, t) is int i && lyrics.Lines[i].Pronunciation == null)
                lyrics.Lines[i] = lyrics.Lines[i] with { Pronunciation = new Pronunciation(tidy) };
        }
    }

    /// <summary>QQ's lyric API escapes some characters as HTML entities ("&amp;#58;").</summary>
    public static string UnescapeHtml(string s) => s.Contains('&') ? WebUtility.HtmlDecode(s).Replace('\u00A0', ' ') : s;
}
