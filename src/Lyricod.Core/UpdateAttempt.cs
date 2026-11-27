namespace Lyricod.Core;

/// <summary>
/// Written just before the launcher swaps a patch in. Found at the next start with the old version still
/// running, the swap never happened, so that release goes to Setup. A version the launcher started clears
/// it at once: one that then fails and is put back mustn't go to Setup, which would install the same files
/// with no way back.
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
