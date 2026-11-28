namespace Lyricod.Core.Tests;

public class HandleRevealTests
{
    /// <summary>A handle brought up by a rest on the area that began at 0.</summary>
    static HandleReveal Shown()
    {
        var reveal = new HandleReveal();
        reveal.Update(true, false, 0);
        Assert.True(reveal.Update(true, false, 0.4));
        return reveal;
    }

    [Fact]
    public void ShowsOnceThePointerHasRestedOnTheArea()
    {
        var reveal = new HandleReveal();
        Assert.False(reveal.Update(true, false, 10));
        Assert.False(reveal.Update(true, false, 10.39));
        Assert.True(reveal.Update(true, false, 10.41));
        Assert.True(reveal.Shown);
    }

    [Fact]
    public void PassingOverDoesNotShowIt()
    {
        var reveal = new HandleReveal();
        Assert.False(reveal.Update(true, false, 0));
        Assert.False(reveal.Update(true, false, 0.3));
        Assert.False(reveal.Update(false, false, 0.35));
        // Back on the area, the rest starts again.
        Assert.False(reveal.Update(true, false, 0.5));
        Assert.False(reveal.Update(true, false, 0.89));
        Assert.True(reveal.Update(true, false, 0.91));
    }

    [Fact]
    public void TheHandlesPlaceDoesNotBringItUp()
    {
        // Hidden, the handle isn't there to rest on.
        var reveal = new HandleReveal();
        Assert.False(reveal.Update(false, true, 0));
        Assert.False(reveal.Update(false, true, 5));
    }

    [Fact]
    public void StaysWhileThePointerIsOnTheAreaOrTheHandle()
    {
        var reveal = Shown();
        Assert.True(reveal.Update(true, false, 3));
        Assert.True(reveal.Update(false, true, 6));
        Assert.True(reveal.Update(false, true, 60));
        Assert.True(reveal.Update(true, false, 61));
    }

    [Fact]
    public void GoesAMomentAfterThePointerHasLeftBoth()
    {
        var reveal = Shown();
        Assert.True(reveal.Update(false, true, 2));
        Assert.True(reveal.Update(false, false, 2.1));
        Assert.True(reveal.Update(false, false, 2.29));
        Assert.False(reveal.Update(false, false, 2.31));
        Assert.False(reveal.Shown);
    }

    [Fact]
    public void CrossingTheGapBetweenAreaAndHandleKeepsIt()
    {
        var reveal = Shown();
        Assert.True(reveal.Update(true, false, 1));
        Assert.True(reveal.Update(false, false, 1.2));
        Assert.True(reveal.Update(false, true, 1.25));
        // The moment is counted from the handle now, not from the area.
        Assert.True(reveal.Update(false, false, 1.5));
        Assert.False(reveal.Update(false, false, 1.56));
    }

    [Fact]
    public void LetGoAfterALongDragItStillStaysItsMoment()
    {
        // Nothing is checked while the handle is dragged: let go, it's told the pointer was on it till then.
        var reveal = Shown();
        Assert.True(reveal.Update(false, true, 8));
        Assert.True(reveal.Update(false, false, 8.29));
        Assert.False(reveal.Update(false, false, 8.31));
    }

    [Fact]
    public void OnceGoneItTakesAnotherRestToShow()
    {
        var reveal = Shown();
        Assert.False(reveal.Update(false, false, 1));
        Assert.False(reveal.Update(true, false, 1.1));
        Assert.False(reveal.Update(false, true, 1.2)); // its old place counts for nothing
        Assert.False(reveal.Update(true, false, 1.3));
        Assert.False(reveal.Update(true, false, 1.69));
        Assert.True(reveal.Update(true, false, 1.71));
    }

    [Fact]
    public void ResetHidesItAndStartsTheRestAfresh()
    {
        var reveal = Shown();
        reveal.Reset();
        Assert.False(reveal.Shown);
        Assert.False(reveal.Update(true, false, 0.5));
        Assert.False(reveal.Update(true, false, 0.89));
        Assert.True(reveal.Update(true, false, 0.91));

        // A rest under way when the window went away doesn't count toward the next one.
        var resting = new HandleReveal();
        resting.Update(true, false, 0);
        resting.Reset();
        Assert.False(resting.Update(true, false, 0.4));
        Assert.True(resting.Update(true, false, 0.81));
    }
}
