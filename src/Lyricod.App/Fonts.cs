using Lyricod.Core;
using Lyricod.Core.Fonts;
using Lyricod.Core.Romanization;
using Lyricod.Platform;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Lyricod;

static class Fonts
{
    const string Chinese = "Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic";
    const string Japanese = "Yu Gothic UI, Microsoft YaHei UI, Malgun Gothic";
    const string Korean = "Malgun Gothic, Microsoft YaHei UI, Yu Gothic UI";
    static readonly Dictionary<string, FontFamily> Families = [];
    static string _choice = "";
    static string? _miSans, _hash;
    static bool _resolved;

    public static event Action? Changed;
    public static string? Error { get; private set; }
    static bool UsingMiSans => _choice == "MiSans" && _miSans != null;
    static LyricFont Kind => UsingMiSans ? LyricFont.MiSans : _choice is "" or "MiSans" ? LyricFont.Default : LyricFont.Other;
    public static FontWeight BoldWeight => new(FontWeights.Get(LyricWeight.Bold, Kind));
    public static FontWeight SemiboldWeight => new(FontWeights.Get(LyricWeight.Semibold, Kind));
    public static FontWeight Weight(IslandWeight weight) => new(FontWeights.Get((LyricWeight)weight, Kind));
    public static string DiagnosticsText => UsingMiSans
        ? $"MiSans (downloaded, {(_hash == MiSansArchive.KnownHash ? "hash ok" : "hash differs")})"
        : _choice is "" or "MiSans" ? "Microsoft YaHei (default)" : _choice;

    public static void Start(Settings settings)
    {
        Apply(settings.LyricsFont);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Settings.LyricsFont)) Apply(settings.LyricsFont);
        };
    }

    static void Apply(string choice)
    {
        _choice = choice;
        if (choice == "MiSans") Resolve();
        Families.Clear();
        Notify();
    }

    // Cache the probe until the files change or the user retries.
    public static void Reload()
    {
        _resolved = false;
        _miSans = _hash = Error = null;
        Apply(_choice);
    }

    static void Notify()
    {
        if (Changed == null) return;
        foreach (Action listener in Changed.GetInvocationList())
        {
            try { listener(); }
            catch (Exception e) { System.Diagnostics.Debug.WriteLine("font refresh: " + e); }
        }
    }

    static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        if (!MiSansDownload.IsDownloaded) return;
        try
        {
            _hash = MiSansArchive.ValidateAsync(MiSansDownload.FontPath).GetAwaiter().GetResult();
            string file = new Uri(MiSansDownload.FontPath).AbsoluteUri + "#MiSans VF";
            if (Loads(file)) { _miSans = file; return; }
            Directory.CreateDirectory(Path.GetDirectoryName(MiSansDownload.AppCopyPath)!);
            string temp = MiSansDownload.AppCopyPath + ".tmp";
            try
            {
                File.Copy(MiSansDownload.FontPath, temp, true);
                File.Move(temp, MiSansDownload.AppCopyPath, true);
            }
            finally { File.Delete(temp); }
            const string app = "ms-appx:///Fonts/MiSansVF.ttf#MiSans VF";
            if (Loads(app)) { _miSans = app; return; }
            Error = L.Get("FontLoadFailed");
        }
        catch (Exception e) { Error = L.Get("FontLoadFailed") + " " + e.Message; }
    }

    static bool Loads(string source)
    {
        try
        {
            double Measure(string name)
            {
                var text = new TextBlock { Text = "Lyricod 歌词 かし 0123", FontSize = 40, FontFamily = new FontFamily(name) };
                text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                return text.DesiredSize.Width;
            }
            // A font that doesn't load falls back to XAML's default (Segoe UI Variable on Windows 11), so
            // compare with a name that can't exist: that measures exactly as the fallback.
            double width = Measure(source);
            return width > 0 && Math.Abs(width - Measure("Lyricod No Such Font")) > 0.1;
        }
        catch (Exception) { return false; }
    }

    public static FontFamily For(string text, LyricLanguage? song)
    {
        var fallback = Romanizer.LineLanguage(text, song) switch
        {
            LyricLanguage.Japanese => Japanese,
            LyricLanguage.Korean => Korean,
            LyricLanguage.Chinese => Chinese,
            _ => song == LyricLanguage.Japanese ? Japanese : song == LyricLanguage.Korean ? Korean : Chinese,
        };
        string? selected = _choice == "MiSans" ? _miSans : _choice.Length > 0 ? _choice : null;
        string source = selected == null ? fallback : selected + ", " + fallback;
        if (!Families.TryGetValue(source, out var family)) Families[source] = family = new FontFamily(source);
        return family;
    }
}
