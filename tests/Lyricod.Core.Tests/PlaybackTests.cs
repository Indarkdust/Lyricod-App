namespace Lyricod.Core.Tests;

public class PlaybackTests
{
    [Fact]
    public void BrowserStaysHiddenUntilLyricsArriveAndResetsForTheNextTrack()
    {
        Assert.Equal(SongState.Checking, SongRecognition.Classify(false, false, true, false));
        Assert.Equal(SongState.NotSong, SongRecognition.Classify(false, false, false, false));
        Assert.Equal(SongState.Checking, SongRecognition.Classify(false, false, true, false));
        Assert.Equal(SongState.Song, SongRecognition.Classify(false, false, false, true));
        Assert.Equal(SongState.Checking, SongRecognition.Classify(false, false, true, false));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void MusicPlayersAndManualPinsStaySongsWithoutLyrics(bool musicPlayer, bool pinned)
    {
        Assert.Equal(SongState.Song, SongRecognition.Classify(musicPlayer, pinned, true, false));
        Assert.Equal(SongState.Song, SongRecognition.Classify(musicPlayer, pinned, false, false));
    }

    [Fact]
    public void PlayheadCarriesForwardOnlyWhilePlaying()
    {
        var p = new Playhead();
        p.Anchor(10, at: 100, playing: true);
        Assert.Equal(12.5, p.Position(102.5), 9);
        p.Anchor(12.5, at: 102.5, playing: false);
        Assert.Equal(12.5, p.Position(200), 9);
        p.Offset = 0.5; // positive shows lyrics earlier
        Assert.Equal(13.0, p.Position(200), 9);
        Assert.Equal(12.5, p.RawPosition(200), 9);
    }

    [Fact]
    public void SmallJitterDoesNotReanchor()
    {
        var p = new Playhead();
        p.Anchor(10, 100, true);
        int v = p.Version;
        Assert.False(p.Report(11.1, 101, true));  // 0.1 s off: ignored
        Assert.Equal(v, p.Version);
        Assert.True(p.Report(30, 101, true));     // a seek
        Assert.True(p.Report(30, 101, false));    // a pause
        Assert.Equal(v + 2, p.Version);
    }

    static readonly LyricLine Line = new()
    {
        Start = 10, End = 13, Text = "aa bb",
        Words = [new LyricWord(10, 1, "aa "), new LyricWord(12, 1, "bb")],
    };

    static double Measure(string s) => s.Length * 10;

    [Fact]
    public void StopsFollowTheMeasuredText()
    {
        var stops = Sweep.Stops(Line, width: 50, Measure);
        Assert.Equal([new SweepStop(10, 11, 0, 30), new SweepStop(12, 13, 30, 50)], stops);
        Assert.Equal(15, Sweep.SungWidth(stops, Line, 50, 10.5), 9);
        Assert.Equal(30, Sweep.SungWidth(stops, Line, 50, 11.5), 9); // between words: holds
        Assert.Equal(0, Sweep.SungWidth(stops, Line, 50, 9), 9);
    }

    [Fact]
    public void PlanRunsFromNowToTheLinesEnd()
    {
        var stops = Sweep.Stops(Line, 50, Measure);
        var (times, values) = Sweep.Plan(stops, Line, 50, position: 10.5);
        Assert.Equal([0, 0.5, 1.5, 2.5], times);
        Assert.Equal([15, 30, 30, 50], values);
    }

    [Fact]
    public void LineWithoutWordTimingLightsUpAsItStarts()
    {
        var plain = Line with { Words = null };
        var (times, values) = Sweep.Plan([], plain, 50, position: 9);
        Assert.Equal([0, 1, 1.08], times.Select(t => Math.Round(t, 9)));
        Assert.Equal([0, 0, 50], values);
        Assert.Equal(50, Sweep.SungWidth([], plain, 50, 10), 9);
    }
}

public class ArtworkColorTests
{
    static byte[] Fill(int pixels, byte r, byte g, byte b)
    {
        var data = new byte[pixels * 4];
        for (int i = 0; i < pixels; i++) { data[i * 4] = b; data[i * 4 + 1] = g; data[i * 4 + 2] = r; data[i * 4 + 3] = 255; }
        return data;
    }

    [Fact]
    public void PicksTheVividColourOverDarkness()
    {
        // A dark cover with a red sign in a quarter of it.
        var data = Fill(1024, 20, 20, 24);
        Fill(256, 230, 30, 40).CopyTo(data, 0);
        var c = ArtworkColor.Dominant(data);
        Assert.NotNull(c);
        Assert.True(c!.Value.R > 240 && c.Value.G < 170 && c.Value.B < 170);
    }

    [Fact]
    public void GreyCoverHasNoColour()
    {
        Assert.Null(ArtworkColor.Dominant(Fill(1024, 128, 128, 128)));
        Assert.Null(ArtworkColor.Dominant(Fill(1024, 250, 250, 250)));
    }
}

public class ArtworkBlurTests
{
    [Fact]
    public void SpreadsAnEdgeAndKeepsFlatAreas()
    {
        // Left half black, right half white, 16×4.
        const int w = 16, h = 4;
        var data = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++) data[(y * w + x) * 4 + c] = (byte)(x >= w / 2 ? 255 : c == 3 ? 255 : 0);
        ArtworkBlur.Soften(data, w, h, 2, 1);
        byte At(int x) => data[(1 * w + x) * 4];
        Assert.Equal(0, At(0));
        Assert.Equal(255, At(w - 1));
        Assert.InRange(At(w / 2 - 1), 60, 190); // the edge is soft now
        Assert.True(At(w / 2 - 2) < At(w / 2 - 1) && At(w / 2 - 1) < At(w / 2) && At(w / 2) < At(w / 2 + 1));
    }

    [Fact]
    public void SaturationPushesColourAwayFromGrey()
    {
        var data = new byte[] { 100, 120, 180, 255 }; // B, G, R
        ArtworkBlur.Soften(data, 1, 1, 0, 1.6);
        Assert.True(data[2] > 180 && data[0] < 100);
    }
}
