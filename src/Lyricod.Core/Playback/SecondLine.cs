namespace Lyricod.Core;

/// <summary>What goes under the line being sung (the desktop strip's and the island's layout).</summary>
public enum SecondLine { TranslationOrNext, Translation, PronunciationOrTranslation, Pronunciation, Next, None }

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
            SecondLine.PronunciationOrTranslation => (line.Pronunciation?.Text ?? line.Translation) is { } t ? (t, true) : null,
            SecondLine.Pronunciation => line.Pronunciation is { } p ? (p.Text, true) : null,
            SecondLine.Next => next != null ? (next, false) : null,
            _ => null,
        };
    }
}
