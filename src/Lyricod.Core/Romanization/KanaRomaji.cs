using System.Text;

namespace Lyricod.Core.Romanization;

/// <summary>
/// Kana → romaji, in the style the macOS app shows: Hepburn consonants ("shi", "chi", "tsu",
/// "fu", "ji"), long vowels spelled out ("toukyou", "koohii"), small っ doubling the next
/// consonant ("matte"). Anything that isn't kana passes through unchanged.
/// </summary>
public static class KanaRomaji
{
    static readonly Dictionary<char, string> Basic = new()
    {
        ['あ'] = "a", ['い'] = "i", ['う'] = "u", ['え'] = "e", ['お'] = "o",
        ['か'] = "ka", ['き'] = "ki", ['く'] = "ku", ['け'] = "ke", ['こ'] = "ko",
        ['さ'] = "sa", ['し'] = "shi", ['す'] = "su", ['せ'] = "se", ['そ'] = "so",
        ['た'] = "ta", ['ち'] = "chi", ['つ'] = "tsu", ['て'] = "te", ['と'] = "to",
        ['な'] = "na", ['に'] = "ni", ['ぬ'] = "nu", ['ね'] = "ne", ['の'] = "no",
        ['は'] = "ha", ['ひ'] = "hi", ['ふ'] = "fu", ['へ'] = "he", ['ほ'] = "ho",
        ['ま'] = "ma", ['み'] = "mi", ['む'] = "mu", ['め'] = "me", ['も'] = "mo",
        ['や'] = "ya", ['ゆ'] = "yu", ['よ'] = "yo",
        ['ら'] = "ra", ['り'] = "ri", ['る'] = "ru", ['れ'] = "re", ['ろ'] = "ro",
        ['わ'] = "wa", ['ゐ'] = "i", ['ゑ'] = "e", ['を'] = "wo", ['ん'] = "n",
        ['が'] = "ga", ['ぎ'] = "gi", ['ぐ'] = "gu", ['げ'] = "ge", ['ご'] = "go",
        ['ざ'] = "za", ['じ'] = "ji", ['ず'] = "zu", ['ぜ'] = "ze", ['ぞ'] = "zo",
        ['だ'] = "da", ['ぢ'] = "ji", ['づ'] = "zu", ['で'] = "de", ['ど'] = "do",
        ['ば'] = "ba", ['び'] = "bi", ['ぶ'] = "bu", ['べ'] = "be", ['ぼ'] = "bo",
        ['ぱ'] = "pa", ['ぴ'] = "pi", ['ぷ'] = "pu", ['ぺ'] = "pe", ['ぽ'] = "po",
        ['ゔ'] = "vu",
        ['ぁ'] = "a", ['ぃ'] = "i", ['ぅ'] = "u", ['ぇ'] = "e", ['ぉ'] = "o", ['ゎ'] = "wa", ['ゕ'] = "ka", ['ゖ'] = "ke",
    };

    /// <summary>Katakana → hiragana (ヴ and the small forms included); other text unchanged.</summary>
    public static char ToHiragana(char c) => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c;

    public static bool IsKana(char c) => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ' or 'ー' or 'ゝ' or 'ゞ' or 'ヽ' or 'ヾ';

    public static string Convert(string kana)
    {
        var syllables = new List<string>();
        bool sokuon = false;
        foreach (var raw in kana)
        {
            char c = ToHiragana(raw);
            switch (c)
            {
                case 'っ':
                    sokuon = true;
                    continue;
                case 'ー' when syllables.Count > 0 && LastVowel(syllables[^1]) is { } v:
                    syllables[^1] += v;
                    continue;
                case 'ゃ' or 'ゅ' or 'ょ' when syllables.Count > 0 && syllables[^1].Length > 1 && syllables[^1].EndsWith('i'):
                {
                    // きゃ → kya, しゃ → sha, ちゃ → cha, じゃ → ja.
                    string vowel = c == 'ゃ' ? "a" : c == 'ゅ' ? "u" : "o";
                    string stem = syllables[^1][..^1];
                    // The stem may carry a doubled consonant from っ ("ssh", "tch").
                    syllables[^1] = stem.EndsWith("sh") || stem.EndsWith("ch") || stem.EndsWith('j') ? stem + vowel : stem + "y" + vowel;
                    continue;
                }
                case 'ぁ' or 'ぃ' or 'ぅ' or 'ぇ' or 'ぉ' when syllables.Count > 0 && Small(syllables[^1], Basic[c]) is { } joined:
                    syllables[^1] = joined;
                    continue;
            }
            string s = Basic.TryGetValue(c, out var r) ? r : raw.ToString();
            if (sokuon)
            {
                sokuon = false;
                // A small っ doubles the next consonant ("tch" for ch); before a vowel it's just a catch.
                if (s.Length > 0 && "bcdfghjkmpqrstvwxz".Contains(s[0])) s = (s.StartsWith("ch") ? "t" : s[0].ToString()) + s;
            }
            syllables.Add(s);
        }
        // A trailing っ ("あっ") is a catch in the voice, not a sound.
        return string.Concat(syllables);
    }

    /// <summary>A small vowel after a syllable: ふぁ → fa, てぃ → ti, うぃ → wi, しぇ → she.</summary>
    static string? Small(string prev, string vowel) => prev switch
    {
        "fu" => "f" + vowel,
        "vu" => "v" + vowel,
        "te" when vowel == "i" => "ti",
        "de" when vowel == "i" => "di",
        "to" when vowel == "u" => "tu",
        "do" when vowel == "u" => "du",
        "u" when vowel is "i" or "e" or "o" => "w" + vowel,
        "shi" or "chi" or "ji" when vowel == "e" => prev[..^1] + "e",
        "i" when vowel == "e" => "ye",
        "tsu" => "ts" + vowel,
        _ => null,
    };

    static string? LastVowel(string syllable)
    {
        for (int i = syllable.Length - 1; i >= 0; i--)
            if ("aeiou".Contains(syllable[i])) return syllable[i].ToString();
        return null;
    }
}
