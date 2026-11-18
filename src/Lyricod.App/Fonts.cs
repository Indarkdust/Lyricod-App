using Lyricod.Core;
using Lyricod.Core.Fonts;
using Lyricod.Core.Romanization;
using Lyricod.Platform;
using Microsoft.UI.Xaml;
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
    /// <summary>The fonts Lyricod downloads, as saved in the font setting.</summary>
    public const string MiSans = "MiSans", WenKai = "WenKai";
    public static bool IsDownloadable(string choice) => choice is MiSans or WenKai;

    static string _choice = "";
    /// <summary>The chosen downloadable font, once it's on disk and loads; null otherwise.</summary>
    static string? _download, _hash, _appFont;
    static bool _resolved;

    public static event Action? Changed;
    public static string? Error { get; private set; }
    /// <summary>The downloadable font in use, or null.</summary>
    public static string? InUse => _download == null ? null : _choice;
    static LyricFont Kind => InUse switch
    {
        MiSans => LyricFont.MiSans,
        WenKai => LyricFont.WenKai,
        _ => _choice == "" || IsDownloadable(_choice) ? LyricFont.Default : LyricFont.Other,
    };
    public static FontWeight BoldWeight => new(FontWeights.Get(LyricWeight.Bold, Kind));
    public static FontWeight SemiboldWeight => new(FontWeights.Get(LyricWeight.Semibold, Kind));
    public static FontWeight Weight(IslandWeight weight) => new(FontWeights.Get((LyricWeight)weight, Kind));
    public static string DiagnosticsText => InUse switch
    {
        MiSans => $"MiSans (downloaded, {(_hash == MiSansArchive.KnownHash ? "hash ok" : "hash differs")})",
        WenKai => "LXGW WenKai Lite (downloaded)",
        _ => _choice == "" || IsDownloadable(_choice) ? "Microsoft YaHei (default)" : _choice,
    };

    /// <summary>The chosen font, or null for the default (Microsoft YaHei for lyrics, Windows' own font elsewhere).</summary>
    static string? Chosen => IsDownloadable(_choice) ? _download : _choice.Length > 0 ? _choice : null;

    /// <summary>
    /// Puts the chosen font on the rest of Lyricod's text too (Settings, tray panel, buttons, song
    /// titles). WinUI reads it as each window opens, so a later change needs a restart (<see
    /// cref="AppNeedsRestart"/>). The default keeps Windows' font: its Chinese is Microsoft YaHei
    /// too, and it has the semibold that headings use and YaHei lacks.
    /// </summary>
    public static void ApplyToApp(ResourceDictionary resources)
    {
        _appFont = Chosen;
        if (_appFont == null) return;
        var family = new FontFamily(_appFont + ", " + Chinese);
        resources["ContentControlThemeFontFamily"] = family; // controls, and text inside them
        Style WithFont(Style? basedOn) => new(typeof(TextBlock)) { BasedOn = basedOn, Setters = { new Setter(TextBlock.FontFamilyProperty, family) } };
        resources[typeof(TextBlock)] = WithFont(null); // plain text
        // Text in WinUI's own styles takes the font of the control it's in, or Windows' outside one
        // (the tray panel's song title), so these get it too.
        foreach (string key in TextStyles)
            if (resources.TryGetValue(key, out object? style) && style is Style found) resources[key] = WithFont(found);
    }

    static readonly string[] TextStyles = ["CaptionTextBlockStyle", "BodyTextBlockStyle", "BodyStrongTextBlockStyle", "BodyLargeTextBlockStyle",
        "SubtitleTextBlockStyle", "TitleTextBlockStyle", "TitleLargeTextBlockStyle", "DisplayTextBlockStyle"];

    /// <summary>Whether the font changed since Lyricod started, so its own text needs a restart to follow.</summary>
    public static bool AppNeedsRestart => Chosen != _appFont;

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
        if (IsDownloadable(choice)) Resolve();
        Families.Clear();
        Notify();
    }

    // Cache the probe until the files change or the user retries.
    public static void Reload()
    {
        _resolved = false;
        _download = _hash = Error = null;
        Apply(_choice);
    }

    static void Notify()
    {
        if (Changed == null) return;
        foreach (Action listener in Changed.GetInvocationList())
        {
            try { listener(); }
            catch (Exception e) { Log.Write("font refresh: " + e); }
        }
    }

    /// <summary>A downloadable font's name, as the font list shows it.</summary>
    public static string NameOf(string choice) => choice == WenKai ? L.Get("FontWenKai") : "MiSans";

    static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        bool wenKai = _choice == WenKai;
        if (!(wenKai ? WenKaiDownload.IsDownloaded : MiSansDownload.IsDownloaded)) return;
        try
        {
            if (wenKai) _download = Load(WenKaiDownload.FontPath, WenKaiDownload.AppCopyPath, WenKaiFont.Families);
            else
            {
                _hash = MiSansArchive.ValidateAsync(MiSansDownload.FontPath).GetAwaiter().GetResult();
                _download = Load(MiSansDownload.FontPath, MiSansDownload.AppCopyPath, ["MiSans VF"]);
            }
            if (_download == null) Error = L.Format("FontLoadFailed", NameOf(_choice));
        }
        catch (Exception e) { Error = L.Format("FontLoadFailed", NameOf(_choice)) + " " + e.Message; }
    }

    /// <summary>
    /// The font at <paramref name="path"/> under the first of its <paramref name="families"/> that loads.
    /// Where Windows won't load a font from the user's folder, a copy beside the app does.
    /// </summary>
    static string? Load(string path, string appCopy, IReadOnlyList<string> families)
    {
        string file = new Uri(path).AbsoluteUri;
        foreach (string family in families)
            if (Loads(file + "#" + family)) return file + "#" + family;
        Directory.CreateDirectory(Path.GetDirectoryName(appCopy)!);
        string temp = appCopy + ".tmp";
        try
        {
            File.Copy(path, temp, true);
            File.Move(temp, appCopy, true);
        }
        finally { File.Delete(temp); }
        foreach (string family in families)
        {
            string app = "ms-appx:///Fonts/" + Path.GetFileName(appCopy) + "#" + family;
            if (Loads(app)) return app;
        }
        return null;
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
        string? selected = Chosen;
        string source = selected == null ? fallback : selected + ", " + fallback;
        if (!Families.TryGetValue(source, out var family)) Families[source] = family = new FontFamily(source);
        return family;
    }
}
