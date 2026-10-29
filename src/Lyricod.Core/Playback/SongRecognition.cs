namespace Lyricod.Core;

public enum SongState { Checking, Song, NotSong }

public static class SongRecognition
{
    public static SongState Classify(bool musicPlayer, bool pinned, bool searching, bool hasLyrics) =>
        musicPlayer || pinned || hasLyrics ? SongState.Song
        : searching ? SongState.Checking : SongState.NotSong;
}
