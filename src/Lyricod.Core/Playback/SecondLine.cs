namespace Lyricod.Core;

/// <summary>
/// What goes under the line being sung (the desktop strip's and the island's layout).
/// <c>PronunciationAndTranslation</c> is two rows, so only the desktop strip offers it.
/// </summary>
public enum SecondLine { TranslationOrNext, Translation, PronunciationOrTranslation, Pronunciation, PronunciationAndTranslation, Next, None }

public static class SecondLineExtensions
{
    /// <summary>What the island shows under line <paramref name="i"/>, or null for no second row
    /// (nothing chosen, no line yet, unsynced lyrics, or nothing under this line), so the row comes
    /// and goes with the line.</summary>
    public static (string Text, bool IsExtra)? RowUnder(this SecondLine mode, int? i, Lyrics? lyrics)
    {
        if (mode == SecondLine.None || i is not int index || lyrics is not { IsSynced: true }) return null;
        return mode.TextUnder(index, lyrics) is { } under && !string.IsNullOrWhiteSpace(under.Text) ? under : null;
    }

    /// <summary>
    /// The rows under line <paramref name="i"/> on the desktop strip: the pronunciation and then
    /// the translation for <c>PronunciationAndTranslation</c> (each only when the line has it),
    /// otherwise the one row <see cref="TextUnder"/> gives, if any.
    /// </summary>
    public static List<(string Text, bool IsExtra)> RowsUnder(this SecondLine mode, int i, Lyrics lyrics)
    {
        if (mode != SecondLine.PronunciationAndTranslation)
            return mode.TextUnder(i, lyrics) is { } under ? [under] : [];
        if (i < 0 || i >= lyrics.Lines.Count) return [];
        var line = lyrics.Lines[i];
        var rows = new List<(string Text, bool IsExtra)>();
        if (line.Pronunciation?.Text is { } p && !string.IsNullOrWhiteSpace(p)) rows.Add((p, true));
        if (line.Translation is { } t && !string.IsNullOrWhiteSpace(t)) rows.Add((t, true));
        return rows;
    }

    /// <summary>How many rows <paramref name="mode"/> can put under a line.</summary>
    public static int MaxRows(this SecondLine mode) => mode switch
    {
        SecondLine.None => 0,
        SecondLine.PronunciationAndTranslation => 2,
        _ => 1,
    };

    /// <summary>What goes under line <paramref name="i"/>; <c>IsExtra</c> is false for the next
    /// line, which is drawn dimmer.</summary>
    public static (string Text, bool IsExtra)? TextUnder(this SecondLine mode, int i, Lyrics lyrics)
    {
        if (i < 0 || i >= lyrics.Lines.Count) return null;
        var line = lyrics.Lines[i];
        var next = i + 1 < lyrics.Lines.Count ? lyrics.Lines[i + 1].Text : null;
        return mode switch
        {
            SecondLine.TranslationOrNext => line.Translation != null ? (line.Translation, true) : next != null ? (next, false) : null,
            SecondLine.Translation => line.Translation != null ? (line.Translation, true) : null,
            SecondLine.PronunciationOrTranslation or SecondLine.PronunciationAndTranslation =>
                (line.Pronunciation?.Text ?? line.Translation) is { } t ? (t, true) : null,
            SecondLine.Pronunciation => line.Pronunciation is { } p ? (p.Text, true) : null,
            SecondLine.Next => next != null ? (next, false) : null,
            _ => null,
        };
    }
}
