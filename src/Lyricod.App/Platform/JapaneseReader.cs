using Lyricod.Core.Romanization;
using Windows.Globalization;

namespace Lyricod.Platform;

/// <summary>
/// Japanese words and their kana readings from Windows' own analyser (the one the Japanese
/// IME uses), for romaji. Text between words (spaces the analyser drops, punctuation) comes
/// back with no reading.
/// </summary>
public sealed class JapaneseReader : IJapaneseReader
{
    /// <summary>
    /// Whether Windows can analyse Japanese here. The analyser comes with the Japanese input
    /// method; without it Windows refuses to create it ("ClassFactory cannot supply requested
    /// class"), and Japanese pronunciation comes only from the lyric sources' own romaji.
    /// </summary>
    public static bool IsAvailable { get; } = Probe();

    static bool Probe()
    {
        try { return JapanesePhoneticAnalyzer.GetWords("日本", false).Count > 0; }
        catch (Exception) { return false; }
    }

    // The analyser takes at most 100 characters at a time.
    const int MaxChunk = 100;

    public IReadOnlyList<WordReading> Read(string text)
    {
        var words = new List<WordReading>();
        for (int start = 0; start < text.Length; start += MaxChunk)
        {
            var chunk = text.Substring(start, Math.Min(MaxChunk, text.Length - start));
            int at = 0;
            foreach (var phoneme in JapanesePhoneticAnalyzer.GetWords(chunk, false))
            {
                var display = phoneme.DisplayText;
                int found = display.Length == 0 ? -1 : chunk.IndexOf(display, at, StringComparison.Ordinal);
                if (found < 0) continue;
                if (found > at) words.Add(new WordReading(chunk[at..found], null));
                bool isWord = display.Any(char.IsLetterOrDigit);
                words.Add(new WordReading(display, isWord ? phoneme.YomiText : null));
                at = found + display.Length;
            }
            if (at < chunk.Length) words.Add(new WordReading(chunk[at..], null));
        }
        return words;
    }
}
