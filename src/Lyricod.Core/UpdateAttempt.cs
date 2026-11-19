namespace Lyricod.Core;

/// <summary>
/// A note that the launcher is about to swap a patched version in, kept across the restart. An installed
/// copy has two ways to update, the launcher's swap and Setup. If it comes back on the old version with the
/// note still there, the launcher never got as far as starting the new one (it was stopped, or couldn't
/// move the folders): the patch is not tried again for that release, and Setup is. A new version the
/// launcher did start removes the note at once, so one that then fails and is put back doesn't lead to
/// Setup, which would install the same files with no way back.
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
