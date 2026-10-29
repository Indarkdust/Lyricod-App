using System.Globalization;
using Lyricod.Core;
using Lyricod.Core.Romanization;

// Prints the lyrics Lyricod would find for a song, like the macOS app's --probe:
//   dotnet run --project tools/Lyricod.Probe -- "<title>" "<artist>" <seconds> [--qq] [--search] [--cached]
// --cached uses what earlier runs saved, as the app does, instead of searching afresh.
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: Lyricod.Probe <title> <artist> <seconds> [--qq] [--search] [--cached]");
    return 1;
}
Console.OutputEncoding = System.Text.Encoding.UTF8;
var track = new Track { TrackId = "probe", Title = args[0], Artist = args[1], Duration = double.Parse(args[2], CultureInfo.InvariantCulture) };
var cache = Path.Combine(Path.GetTempPath(), "lyricod-probe-cache");
var provider = new LyricsProvider(cache, "probe");

if (args.Contains("--search"))
{
    var results = await provider.Candidates($"{track.Title} {track.Artist}", track, [LyricsSource.Qq, LyricsSource.Netease, LyricsSource.Lrclib]);
    foreach (var c in results)
        Console.WriteLine($"{c.Score,5:0.0}  {c.Id.Source,-8} {c.Title} — {c.Artists} [{c.Album}] {c.Duration:0}s");
    return 0;
}

var lyrics = args.Contains("--qq") ? await provider.QqReference(track, ignoreCache: true) : await provider.GetLyrics(track, ignoreCache: !args.Contains("--cached"));
if (lyrics == null) { Console.WriteLine("no lyrics"); return 0; }
Console.WriteLine($"source={lyrics.Source} lines={lyrics.Lines.Count} wordSynced={lyrics.IsWordSynced} ref={lyrics.Ref}");
// Pronunciation as the source gave it (‹…›), then what's shown (⟨…⟩). Japanese needs the
// Windows analyser, so on other systems only the source's romaji shows for it.
var given = lyrics.Lines.Select(l => l.Pronunciation?.Text).ToList();
new Romanizer().Fill(lyrics);
for (int i = 0; i < Math.Min(lyrics.Lines.Count, 14); i++)
{
    var line = lyrics.Lines[i];
    Console.WriteLine($"{line.Start,7:0.00}  {(line.IsCredit ? "[credit] " : "")}{line.Text}{(line.Translation != null ? "  / " + line.Translation : "")}");
    if (given[i] != null) Console.WriteLine($"         ‹{given[i]}›");
    if (line.Pronunciation != null) Console.WriteLine($"         ⟨{line.Pronunciation.Text}⟩ {line.Pronunciation.Language}");
}
return 0;
