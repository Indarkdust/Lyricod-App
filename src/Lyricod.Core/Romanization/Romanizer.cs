using System.Text;
using Lyricod.Core;
using ToolGood.Words;

namespace Lyricod.Core.Romanization;

/// <summary>A word of a Japanese line with its reading in kana; <c>Reading</c> null for what's between words.</summary>
public readonly record struct WordReading(string Surface, string? Reading);

/// <summary>Splits Japanese text into words with their kana readings (Windows: JapanesePhoneticAnalyzer).</summary>
public interface IJapaneseReader
{
    IReadOnlyList<WordReading> Read(string text);
}

/// <summary>
/// Works out pronunciation lines. Chinese: pinyin by phrase (ToolGood.Words). Japanese:
/// readings from the platform's analyser, then kana → romaji. Korean: the Revised
/// Romanization rules. A port of the macOS app's Romanizer; the output style matches it,
/// except that pinyin is spaced by syllable.
/// </summary>
public sealed class Romanizer(IJapaneseReader? japanese = null)
{
    /// <summary>
    /// Fills in every sung line's pronunciation. A source's own romaji is kept for Japanese
    /// (people typed it, so kanji readings are right); Korean and Chinese are always worked
    /// out here, since sources spell those inconsistently.
    /// </summary>
    public void Fill(Lyrics lyrics)
    {
        var song = SongLanguage(lyrics);
        for (int i = 0; i < lyrics.Lines.Count; i++)
        {
            var line = lyrics.Lines[i];
            if (line.IsCredit || LineLanguage(line.Text, song) is not { } language)
            {
                lyrics.Lines[i] = line with { Pronunciation = null };
                continue;
            }
            Pronunciation? p = language == LyricLanguage.Japanese && line.Pronunciation?.Text is { Length: > 0 } given
                ? new Pronunciation(given, language)
                : Romanize(line.Text, language) is { } text ? new Pronunciation(text, language) : null;
            lyrics.Lines[i] = line with { Pronunciation = p };
        }
    }

    public string? Romanize(string text, LyricLanguage language)
    {
        string? output = language switch
        {
            LyricLanguage.Chinese => Chinese(text),
            LyricLanguage.Japanese => japanese == null ? null : Japanese(text, japanese),
            _ => Korean(text),
        };
        if (output == null) return null;
        var tidy = Tidy(output);
        // Nothing to add if it reads the same (the line was already in Latin letters).
        return tidy.Length == 0 || tidy == Tidy(text) ? null : tidy;
    }

    static string Tidy(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ---- Which language ----

    record struct Scripts(int Han, int Kana, int Hangul);

    static Scripts Count(string text)
    {
        int han = 0, kana = 0, hangul = 0;
        foreach (var r in text.EnumerateRunes())
        {
            switch (r.Value)
            {
                case (>= 0x3040 and <= 0x309F) or (>= 0x30A0 and <= 0x30FA) or (>= 0x30FC and <= 0x30FF) or (>= 0x31F0 and <= 0x31FF) or (>= 0xFF66 and <= 0xFF9F): kana++; break;
                case (>= 0xAC00 and <= 0xD7A3) or (>= 0x1100 and <= 0x11FF) or (>= 0x3130 and <= 0x318F): hangul++; break;
                case (>= 0x4E00 and <= 0x9FFF) or (>= 0x3400 and <= 0x4DBF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x2FFFF): han++; break;
            }
        }
        return new Scripts(han, kana, hangul);
    }

    /// <summary>The song's language from all its lines: any real amount of kana means Japanese (Chinese lyrics have none), Hangul means Korean.</summary>
    public static LyricLanguage? SongLanguage(Lyrics lyrics)
    {
        int han = 0, kana = 0, hangul = 0;
        foreach (var line in lyrics.Lines.Where(l => !l.IsCredit))
        {
            var s = Count(line.Text);
            han += s.Han; kana += s.Kana; hangul += s.Hangul;
        }
        if (kana >= 3) return LyricLanguage.Japanese;
        if (hangul > han) return LyricLanguage.Korean;
        if (han > 0) return LyricLanguage.Chinese;
        return hangul > 0 ? LyricLanguage.Korean : null;
    }

    /// <summary>One line's language: its own script when it has kana or Hangul; Chinese characters alone are read as the song's language (kanji in a Japanese song).</summary>
    public static LyricLanguage? LineLanguage(string text, LyricLanguage? song)
    {
        var s = Count(text);
        if (s.Hangul > 0 && s.Hangul >= s.Kana) return LyricLanguage.Korean;
        if (s.Kana > 0) return LyricLanguage.Japanese;
        if (s.Han > 0) return song == LyricLanguage.Japanese ? LyricLanguage.Japanese : LyricLanguage.Chinese;
        return null;
    }

    // ---- Joining ----

    /// <summary>One piece of a romanised line: a word's reading, or what was between words.</summary>
    record struct Part(string Text, bool IsWord, bool WasLatin = false);

    /// <summary>
    /// Joins the pieces into a line. Words that came from Chinese, Japanese or Korean get
    /// spaces between them (those scripts don't use any); words that were Latin keep their own
    /// spacing ("Soi.Co.Ltd"); punctuation sits against the word before.
    /// </summary>
    static string Join(IEnumerable<Part> parts)
    {
        var sb = new StringBuilder();
        bool lastWasLatin = false, spaceSinceWord = true;
        char? Last() => sb.Length > 0 ? sb[^1] : null;
        foreach (var part in parts)
        {
            if (part.IsWord)
            {
                if (Last() is { } last && !char.IsWhiteSpace(last) && !"(“‘".Contains(last) && !(lastWasLatin && part.WasLatin && !spaceSinceWord))
                    sb.Append(' ');
                sb.Append(part.Text);
                lastWasLatin = part.WasLatin;
                spaceSinceWord = false;
                continue;
            }
            var gap = Punctuation(part.Text);
            var core = gap.Trim(' ', '\t');
            if (gap.Any(char.IsWhiteSpace)) spaceSinceWord = true;
            if (core.Length == 0) { sb.Append(' '); continue; }
            char first = core[0];
            if (")]}”’,.!?:;…~".Contains(first))
            {
                while (Last() is { } l && char.IsWhiteSpace(l)) sb.Length--;
            }
            else if (char.IsWhiteSpace(gap[0]) || ("(“‘".Contains(first) && Last() is { } l2 && !char.IsWhiteSpace(l2)))
            {
                sb.Append(' ');
            }
            sb.Append(core);
            if (char.IsWhiteSpace(gap[^1])) sb.Append(' ');
        }
        return sb.ToString();
    }

    static readonly Dictionary<char, string> PunctuationMap = new()
    {
        ['，'] = ",", ['。'] = ".", ['、'] = ",", ['！'] = "!", ['？'] = "?", ['：'] = ":", ['；'] = ";",
        ['～'] = "~", ['〜'] = "~", ['（'] = "(", ['）'] = ")", ['「'] = "“", ['」'] = "”", ['『'] = "“",
        ['』'] = "”", ['《'] = "“", ['》'] = "”", ['・'] = " ", ['　'] = " ",
    };

    /// <summary>CJK punctuation as its Latin counterpart.</summary>
    static string Punctuation(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(PunctuationMap.TryGetValue(c, out var r) ? r : c.ToString());
        return sb.ToString();
    }

    // ---- Chinese ----

    static bool IsHan(Rune r) => r.Value is (>= 0x4E00 and <= 0x9FFF) or (>= 0x3400 and <= 0x4DBF) or (>= 0xF900 and <= 0xFAFF);
    static bool IsLatinWord(Rune r) => Rune.IsLetterOrDigit(r) && !IsHan(r) || r.Value == '\'';

    /// <summary>还 is huán only in "give back"; as "still / also" before these it's hái.</summary>
    static readonly HashSet<char> HaiFollowers = [.. "要是有在会没能想不很可记得好差算真行再从已也"];

    public static string Chinese(string text)
    {
        // Simplified first: the pinyin dictionary has phrases there.
        var simplified = WordsHelper.ToSimplifiedChinese(text);
        var parts = new List<Part>();
        var runes = simplified.EnumerateRunes().ToList();
        int i = 0;
        while (i < runes.Count)
        {
            int start = i;
            if (IsHan(runes[i]))
            {
                while (i < runes.Count && IsHan(runes[i])) i++;
                var run = string.Concat(runes.Skip(start).Take(i - start).Select(r => r.ToString()));
                var syllables = WordsHelper.GetPinyin(run, " ", true).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.ToLowerInvariant()).ToArray();
                if (syllables.Length == run.Length)
                    for (int k = 0; k + 1 < run.Length; k++)
                        if (run[k] == '还' && syllables[k] == "huán" && HaiFollowers.Contains(run[k + 1])) syllables[k] = "hái";
                parts.AddRange(syllables.Select(s => new Part(s, true)));
            }
            else if (IsLatinWord(runes[i]))
            {
                while (i < runes.Count && IsLatinWord(runes[i])) i++;
                parts.Add(new Part(string.Concat(runes.Skip(start).Take(i - start).Select(r => r.ToString())), true, WasLatin: true));
            }
            else
            {
                while (i < runes.Count && !IsHan(runes[i]) && !IsLatinWord(runes[i])) i++;
                parts.Add(new Part(string.Concat(runes.Skip(start).Take(i - start).Select(r => r.ToString())), false));
            }
        }
        return Join(parts);
    }

    // ---- Japanese ----

    static string Japanese(string text, IJapaneseReader reader)
    {
        var parts = new List<Part>();
        string pending = "";
        foreach (var (surface, reading) in reader.Read(text))
        {
            if (reading == null || !surface.Any(char.IsLetterOrDigit))
            {
                if (pending.Length > 0) { parts.Add(new Part(KanaRomaji.Convert(pending), true)); pending = ""; }
                parts.Add(new Part(surface, false)); // the analyser returns brackets as words
                continue;
            }
            string romaji;
            // Particles are written as said.
            if (pending.Length == 0 && surface is "は" or "へ" or "を")
                romaji = surface == "は" ? "wa" : surface == "へ" ? "e" : "o";
            else
            {
                var r = pending + reading;
                // A small っ ending a word ("待っ|て") doubles the next word's first consonant,
                // and the two belong together: "matte".
                if (r.EndsWith('っ') || r.EndsWith('ッ')) { pending = r; continue; }
                pending = "";
                romaji = KanaRomaji.Convert(r);
            }
            bool wasLatin = !reading.Any(KanaRomaji.IsKana) && romaji == surface;
            parts.Add(new Part(romaji, true, wasLatin));
        }
        if (pending.Length > 0) parts.Add(new Part(KanaRomaji.Convert(pending), true));
        return Join(parts.Where(p => !p.IsWord || p.Text.Length > 0));
    }

    // ---- Korean ----
    // Revised Romanization of Korean, with the sound changes that make spelling and speech
    // differ most (linking, nasalisation, ㄹ, aspiration, palatalisation).

    static readonly string[] Initials = ["g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h"];
    static readonly string[] Vowels = ["a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo", "u", "wo", "we",
                                       "wi", "yu", "eu", "ui", "i"];

    // Initial consonant indices.
    const int G = 0, KK = 1, N = 2, D = 3, R = 5, M = 6, B = 7, S = 9, SS = 10, Silent = 11, J = 12, CH = 14, K = 15, T = 16, P = 17, H = 18;

    /// <summary>Final consonant (index 1…27): what's left in place and what moves to a following vowel-initial syllable.</summary>
    static readonly Dictionary<int, (int Stays, int? Moves)> Linking = new()
    {
        [1] = (0, G), [2] = (0, KK), [3] = (1, S), [4] = (0, N), [5] = (4, J), [6] = (0, N), [7] = (0, D), [8] = (0, R),
        [9] = (8, G), [10] = (8, M), [11] = (8, B), [12] = (8, S), [13] = (8, T), [14] = (8, P), [15] = (0, R), [16] = (0, M),
        [17] = (0, B), [18] = (17, S), [19] = (0, S), [20] = (0, SS), [21] = (21, null), [22] = (0, J), [23] = (0, CH),
        [24] = (0, K), [25] = (0, T), [26] = (0, P), [27] = (0, null),
    };

    /// <summary>How a final consonant sounds before another consonant or at a word's end.</summary>
    static string Sound(int final) => final switch
    {
        1 or 2 or 3 or 9 or 24 => "k",
        4 or 5 or 6 => "n",
        7 or 19 or 20 or 22 or 23 or 25 or 27 => "t",
        8 or 11 or 12 or 13 or 15 => "l",
        10 or 16 => "m",
        14 or 17 or 18 or 26 => "p",
        21 => "ng",
        _ => "",
    };

    public static string Korean(string text)
    {
        var sb = new StringBuilder();
        var word = new List<(int L, int V, int T)>();
        void Flush() { sb.Append(RomanizeWord(word)); word.Clear(); }
        foreach (var r in text.EnumerateRunes())
        {
            if (r.Value is >= 0xAC00 and <= 0xD7A3)
            {
                int i = r.Value - 0xAC00;
                word.Add((i / 588, i % 588 / 28, i % 28));
            }
            else
            {
                Flush();
                sb.Append(Punctuation(r.ToString()));
            }
        }
        Flush();
        return sb.ToString();
    }

    static string RomanizeWord(List<(int L, int V, int T)> syllables)
    {
        if (syllables.Count == 0) return "";
        var initial = syllables.Select(s => Initials[s.L]).ToArray();
        var final = syllables.Select(s => Sound(s.T)).ToArray();
        for (int i = 0; i + 1 < syllables.Count; i++)
        {
            int t = syllables[i].T;
            var next = syllables[i + 1];
            if (t == 0) continue;
            var s = Sound(t);
            switch (next.L)
            {
                case Silent:
                    // Linking: the final carries over to the vowel after it.
                    var link = Linking[t];
                    final[i] = link.Stays == 21 ? "ng" : Sound(link.Stays);
                    if (link.Stays == 21) break;
                    if (link.Moves is { } moved)
                    {
                        // ㄷ, ㅌ before 이 soften to ㅈ, ㅊ (같이 → gachi).
                        bool palatal = next.V == 20 && (moved == D || moved == T);
                        initial[i + 1] = palatal ? (moved == D ? "j" : "ch") : Initials[moved];
                    }
                    break;
                case N or M:
                    // Stops before a nasal become nasals (합니다 → hamnida).
                    if (s == "k") final[i] = "ng";
                    else if (s == "t") final[i] = "n";
                    else if (s == "p") final[i] = "m";
                    else if (s == "l" && next.L == N) { final[i] = "l"; initial[i + 1] = "l"; } // 설날 → seollal
                    break;
                case R:
                    if (s is "n" or "l") { final[i] = "l"; initial[i + 1] = "l"; } // 신라 → silla
                    else if (s is "m" or "ng") initial[i + 1] = "n";             // 음료 → eumnyo
                    else if (s == "k") { final[i] = "ng"; initial[i + 1] = "n"; } // 독립 → dongnip
                    else if (s == "p") { final[i] = "m"; initial[i + 1] = "n"; }
                    else if (s == "t") { final[i] = "n"; initial[i + 1] = "n"; }
                    break;
                case H:
                    // A stop meeting ㅎ becomes aspirated (축하 → chuka).
                    switch (t)
                    {
                        case 1 or 2: final[i] = ""; initial[i + 1] = "k"; break;
                        case 9: final[i] = "l"; initial[i + 1] = "k"; break;
                        case 7 or 19 or 20 or 25: final[i] = ""; initial[i + 1] = "t"; break;
                        case 22 or 23: final[i] = ""; initial[i + 1] = "ch"; break;
                        case 17 or 26: final[i] = ""; initial[i + 1] = "p"; break;
                        case 11: final[i] = "l"; initial[i + 1] = "p"; break;
                        case 27: final[i] = ""; initial[i + 1] = ""; break;
                    }
                    break;
            }
            // ㅎ, ㄶ, ㅀ before ㄱ, ㄷ, ㅈ aspirate them (좋다 → jota, 많고 → manko).
            if (t is 6 or 15 or 27)
            {
                string stays = t == 6 ? "n" : t == 15 ? "l" : "";
                switch (next.L)
                {
                    case G: final[i] = stays; initial[i + 1] = "k"; break;
                    case D: final[i] = stays; initial[i + 1] = "t"; break;
                    case J: final[i] = stays; initial[i + 1] = "ch"; break;
                    case S: final[i] = stays; initial[i + 1] = "ss"; break;
                    case N when t is 27 or 6: final[i] = "n"; break; // 놓는 → nonneun
                }
            }
        }
        var sb = new StringBuilder();
        for (int i = 0; i < syllables.Count; i++) sb.Append(initial[i]).Append(Vowels[syllables[i].V]).Append(final[i]);
        return sb.ToString();
    }
}
