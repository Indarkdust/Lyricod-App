namespace Lyricod.Core;

/// <summary>Acceptance and diagnostics for one automatic lookup.</summary>
public sealed class LyricsSearch(bool isMusicPlayer, bool lookUpOtherPlayers = true)
{
    // An exact title and artist score 6; an exact title and length alone score 5.5.
    public const double ConfidentScore = 5.5;
    public bool IsMusicPlayer { get; } = isMusicPlayer;
    public bool AutomaticLookupAllowed { get; } = isMusicPlayer || lookUpOtherPlayers;
    public double? BestScore { get; private set; }

    /// <summary>Music sources keep their existing thresholds; other players need evidence.</summary>
    public bool Accepts(double? score, bool? titleMatched) =>
        IsMusicPlayer || score >= ConfidentScore && titleMatched == true;

    internal void Observe(double? score)
    {
        if (score is { } value && double.IsFinite(value) && (BestScore == null || value > BestScore)) BestScore = value;
    }

    internal List<LyricsCandidate> Observe(List<LyricsCandidate> candidates)
    {
        foreach (var candidate in candidates) Observe(candidate.Score);
        return candidates;
    }

    /// <summary>A music video's artist prefix is not part of its song title.</summary>
    public Track Prepare(Track track)
    {
        if (IsMusicPlayer) return track;
        int dash = track.Title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash <= 0) return track;
        var prefix = TrackMatcher.Normalize(track.Title[..dash]);
        if (prefix.Length == 0 || prefix != TrackMatcher.Normalize(track.Artist)) return track;
        var title = track.Title[(dash + 3)..].Trim();
        return title.Length == 0 ? track : track with { Title = title };
    }
}
