namespace Lyricod.Core;

/// <summary>
/// A note that a patch is about to be applied, kept across the restart. An installed copy has two ways to
/// update, the patch and Setup: if it comes back still on the old version (the launcher put it back, or
/// never ran), the patch is not tried again for that release, and Setup is.
/// </summary>
public static class UpdateAttempt
{
    const string FileName = "patch-attempt.txt";

    public static void Write(string directory, Version target)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, FileName), target.ToString(3));
    }

    public static void Clear(string directory)
    {
        try { File.Delete(Path.Combine(directory, FileName)); }
        catch (Exception) { }
    }

    /// <summary>The release whose patch didn't take, when this version is still below it. Once it has
    /// been reached, by whichever way, the note goes.</summary>
    public static Version? Failed(string directory, Version running)
    {
        try
        {
            string path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return null;
            var target = UpdateCheck.ParseVersion(File.ReadAllText(path).Trim());
            if (target != null && running < target) return target;
            File.Delete(path);
        }
        catch (Exception) { }
        return null;
    }
}
