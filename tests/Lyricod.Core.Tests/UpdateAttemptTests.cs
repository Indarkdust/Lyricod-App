namespace Lyricod.Core.Tests;

public class UpdateAttemptTests
{
    [Fact]
    public void APatchThatDidNotTakeIsRememberedUntilThatVersionIsReached()
    {
        using var temp = new TempDirectory();
        string updates = Path.Combine(temp.Path, "Updates");
        Assert.Null(UpdateAttempt.Failed(updates, new(1, 2, 2)));

        UpdateAttempt.Write(updates, new(1, 2, 3));
        // Back on the old version: the patch didn't take, however often Lyricod is opened.
        Assert.Equal(new Version(1, 2, 3), UpdateAttempt.Failed(updates, new(1, 2, 2)));
        Assert.Equal(new Version(1, 2, 3), UpdateAttempt.Failed(updates, new(1, 2, 2)));
        // On the new one, or past it: it took, and the note is gone.
        Assert.Null(UpdateAttempt.Failed(updates, new(1, 2, 3)));
        Assert.Empty(Directory.EnumerateFiles(updates));

        UpdateAttempt.Write(updates, new(1, 2, 3));
        Assert.Null(UpdateAttempt.Failed(updates, new(1, 3, 0)));
        Assert.Empty(Directory.EnumerateFiles(updates));
    }

    [Fact]
    public void ANoteThatCannotBeReadIsDropped()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "patch-attempt.txt"), "soon");
        Assert.Null(UpdateAttempt.Failed(temp.Path, new(1, 2, 2)));
        Assert.Empty(Directory.EnumerateFiles(temp.Path));
    }

    [Fact]
    public void ClearingRemovesTheNote()
    {
        using var temp = new TempDirectory();
        UpdateAttempt.Write(temp.Path, new(1, 2, 3));
        UpdateAttempt.Clear(temp.Path);
        Assert.Null(UpdateAttempt.Failed(temp.Path, new(1, 2, 2)));
        UpdateAttempt.Clear(Path.Combine(temp.Path, "missing"));
    }
}
