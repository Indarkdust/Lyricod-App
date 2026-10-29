using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lyricod.Core.Romanization;
using ToolGood.Words;

namespace Lyricod.Core;

/// <summary>Scores how well a search result matches the playing track.</summary>
public static partial class TrackMatcher
{
    [GeneratedRegex(@"[\(（\[【].*?[\)）\]】]")]
    private static partial Regex BracketedPart();

    [GeneratedRegex(@"[\(（\[【](.+?)[\)）\]】]")]
    private static partial Regex BracketContent();

    /// <summary>Traditional → Simplified Chinese (幹 = 干), lower case, no bracketed suffixes, letters and digits only.</summary>
    public static string Normalize(string s)
    {
        s = WordsHelper.ToSimplifiedChinese(s).ToLowerInvariant();
        // Drop bracketed suffixes: "(Live)", "（伴奏）", "[Remastered]", "- 2011 Remaster".
        s = BracketedPart().Replace(s, "");
        int dash = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0) s = s[..dash];
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
            if (IsAlphanumeric(r)) sb.Append(r.ToString());
        return sb.ToString();
    }

    /// <summary>Letters, marks and numbers, like Foundation's CharacterSet.alphanumerics.</summary>
    internal static bool IsAlphanumeric(Rune r) => Rune.GetUnicodeCategory(r) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };

    /// <summary>"几分之几 (You Complete Me)" → ["You Complete Me"].</summary>
    public static IEnumerable<string> Bracketed(string s) => BracketContent().Matches(s).Select(m => m.Groups[1].Value);

    // Another language's version: "English ver.", "(Japanese Version)", "English edition", "英文版", "粤语版".
    [GeneratedRegex(@"(english|japanese|chinese|mandarin|cantonese|korean|thai)\s*(ver(sion)?|edition)\b|[\(（\[【]\s*(english|japanese|chinese|mandarin|cantonese|korean|thai)\s*[\)）\]】]|(英文|日文|中文|韩文|英语|日语|日本语|韩语|粤语|国语|普通话|华语|闽南语|台语|泰语|英|日|韩|粤)\s*(版|ver)")]
    private static partial Regex LanguageVersion();

    // No vocals, so no lyrics to show.
    [GeneratedRegex(@"instrumental|\binst\b|伴奏|纯音乐|off\s*vocal|karaoke|カラオケ|オフボーカル|backing\s*track|消音|no\s*vocals?")]
    private static partial Regex NoVocals();

    // Another take of the same song. These words are common in titles ("Live Forever"), so
    // they only count in a bracketed part or after " - ".
    [GeneratedRegex(@"\blive\b|现场|演唱会|remix|acoustic|piano|钢琴|\bcover\b|翻自|翻唱|sped\s*up|slowed|加速|\bdj\b")]
    private static partial Regex Variant();

    /// <summary>
    /// What a title says it is besides the song: "lang:en" (an English version), "novocals"
    /// (an instrumental), "variant" (live, remix, cover…).
    /// </summary>
    public static HashSet<string> Versions(string title)
    {
        var s = WordsHelper.ToSimplifiedChinese(title).ToLowerInvariant();
        var kinds = new HashSet<string>();
        foreach (Match m in LanguageVersion().Matches(s)) kinds.Add("lang:" + LanguageOf(m.Value));
        if (NoVocals().IsMatch(s)) kinds.Add("novocals");
        int dash = s.IndexOf(" - ", StringComparison.Ordinal);
        var suffixes = string.Join(' ', Bracketed(s)) + (dash >= 0 ? " " + s[dash..] : "");
        if (Variant().IsMatch(suffixes)) kinds.Add("variant");
        return kinds;
    }

    static string LanguageOf(string marker) =>
        marker.Contains("english") || marker.Contains('英') ? "en"
        : marker.Contains("japanese") || marker.Contains('日') ? "ja"
        : marker.Contains("korean") || marker.Contains('韩') ? "ko"
        : marker.Contains("cantonese") || marker.Contains('粤') ? "yue"
        : marker.Contains("thai") || marker.Contains('泰') ? "th"
        : marker.Contains("闽南") || marker.Contains("台语") ? "nan"
        : "zh";

    /// <summary>
    /// A Latin title for one written in kana, either way round ("BETELGEUSE" for ベテルギウス,
    /// "Sayonara, Mata Itsuka!" for さよーならまたいつか！), as YouTube Music and Spotify often show them.
    /// Compared by consonants, the way katakana spells English: r for l, b for v, vowels in between.
    /// </summary>
    public static bool SoundsAlike(string a, string b)
    {
        if (KanaOnly(a) && LatinOnly(b)) (a, b) = (b, a);
        else if (!(LatinOnly(a) && KanaOnly(b))) return false;
        string latin = Consonants(a), kana = Consonants(KanaRomaji.Convert(b));
        int shorter = Math.Min(latin.Length, kana.Length);
        // Katakana drops some English sounds ("-er" becomes a long vowel): one slip in a long title.
        return shorter >= 3 && (latin == kana || shorter >= 5 && Distance(latin, kana) <= 1);
    }

    static bool LatinOnly(string s) => s.Any(char.IsLetter) && s.All(c => !char.IsLetter(c) || c < 'ɐ');

    static bool KanaOnly(string s) => s.Count(KanaRomaji.IsKana) >= 2 && s.All(c => !char.IsLetter(c) || KanaRomaji.IsKana(c));

    /// <summary>"betelgeuse" → "btrgs": no vowels, y or h, l as r, v as b, c and q as k, no doubles.</summary>
    static string Consonants(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            var sounds = c switch { 'l' => "r", 'v' => "b", 'c' or 'q' => "k", 'x' => "ks", _ => c.ToString() };
            foreach (char k in sounds)
            {
                if (k is < 'a' or > 'z' || "aeiouyh".Contains(k) || sb.Length > 0 && sb[^1] == k) continue;
                sb.Append(k);
            }
        }
        return sb.ToString();
    }

    static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    /// <summary>A title stripped of bracketed parts, for searching.</summary>
    public static string BareTitle(string title) => Regex.Replace(title, @"\s*[\(（\[【].*?[\)）\]】]", "");

    /// <summary>Whether any of <paramref name="artists"/> is the playing artist, under any of their names.</summary>
    public static bool ArtistMatches(IEnumerable<string> artists, Track track)
    {
        var playing = new[] { track.Artist }.Concat(track.ArtistAliases).Select(Normalize).Where(s => s.Length > 0).ToList();
        return artists.Any(a => Normalize(a) is { Length: > 0 } n && playing.Any(p => p.Contains(n) || n.Contains(p)));
    }

    /// <summary>The playing title, its bracketed parts and its learned other titles, normalised.</summary>
    public static HashSet<string> WantedTitles(Track track) =>
        new[] { track.Title }.Concat(Bracketed(track.Title)).Concat(track.TitleAliases).Select(Normalize).Where(s => s.Length > 0).ToHashSet();

    /// <summary>Whether one of <paramref name="titles"/> is the playing title (or one of its other
    /// names): exactly, contained, or sounding the same in kana.</summary>
    public static bool TitleMatches(IEnumerable<string> titles, Track track)
    {
        var titleList = titles.ToList();
        var wantedTitles = new[] { track.Title }.Concat(Bracketed(track.Title)).Concat(track.TitleAliases).ToList();
        var offeredTitles = titleList.Concat(titleList.SelectMany(Bracketed)).ToList();
        var wanted = WantedTitles(track);
        var offered = offeredTitles.Select(Normalize).Where(s => s.Length > 0).ToHashSet();
        return wanted.Overlaps(offered)
            || wanted.Any(w => offered.Any(o => o.Contains(w) || w.Contains(o)))
            || wantedTitles.Any(w => offeredTitles.Any(o => SoundsAlike(w, o)));
    }

    /// <param name="album">The result's album: "English edition" marks a version too.</param>
    public static double Score(IEnumerable<string> titles, IEnumerable<string> artists, double duration, Track track, string? album = null)
    {
        var titleList = titles.ToList();
        var artistList = artists.ToList();
        double score = 0;
        var wantedTitles = new[] { track.Title }.Concat(Bracketed(track.Title)).Concat(track.TitleAliases).ToList();
        var offeredTitles = titleList.Concat(titleList.SelectMany(Bracketed)).ToList();
        var wanted = WantedTitles(track);
        var offered = offeredTitles.Select(Normalize).Where(s => s.Length > 0).ToHashSet();
        bool titleMatches = true;
        if (wanted.Overlaps(offered)) score += 3;
        else if (wanted.Any(w => offered.Any(o => o.Contains(w) || w.Contains(o)))) score += 1.5;
        else if (wantedTitles.Any(w => offeredTitles.Any(o => SoundsAlike(w, o)))) score += 1.5;
        else titleMatches = false;
        // No title in common: only the same artist with the same length identifies the song
        // (an English title for a song listed only in Chinese). Anything looser is another
        // song by that artist (晴天 matched 骑士精神, 11 s apart).
        if (!titleMatches && (track.Duration <= 0 || duration <= 0 || Math.Abs(duration - track.Duration) >= 3)) score -= 3;

        // Versions: the English version of a Japanese song can share its title (NetEase lists
        // "Dream lantern (English ver.)" under 夢灯籠 too), artist and length, but not its words.
        var playingVersions = Versions(track.Title);
        playingVersions.UnionWith(Versions(track.Album).Where(v => v.StartsWith("lang:", StringComparison.Ordinal)));
        var offeredVersions = titleList.SelectMany(Versions).ToHashSet();
        if (album != null) offeredVersions.UnionWith(Versions(album).Where(v => v.StartsWith("lang:", StringComparison.Ordinal)));
        // Another language or no vocals: wrong lyrics however well the rest matches.
        foreach (var v in offeredVersions.Except(playingVersions)) score -= v == "variant" ? 1.5 : 6;
        // The player names a version the result doesn't: likely the other one.
        foreach (var v in playingVersions.Except(offeredVersions)) if (v != "variant") score -= 2;

        // The artist under any of their names. A known artist who doesn't match counts
        // against: that's how covers (same title and length) lose to the original.
        if (ArtistMatches(artistList, track)) score += 3;
        else if (artistList.Count > 0 && new[] { track.Artist }.Concat(track.ArtistAliases).Any(a => Normalize(a).Length > 0)) score -= 3;

        if (track.Duration > 0 && duration > 0)
        {
            double diff = Math.Abs(duration - track.Duration);
            if (diff < 1) score += 2.5;
            else if (diff < 3) score += 1.5;
            else if (diff < 6) score += 0.5;
            else if (diff > 15) score -= 4;
        }
        return score;
    }
}
