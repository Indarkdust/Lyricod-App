using Lyricod.Core.Fonts;
using Lyricod.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lyricod;

public sealed partial class SettingsWindow
{
    readonly Microsoft.UI.Dispatching.DispatcherQueue _fontDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    CancellationTokenSource? _fontDownload;
    /// <summary>The font being downloaded, while <see cref="_fontDownload"/> runs.</summary>
    string? _fontDownloading;
    FontDownloadProgress? _fontProgress;
    string? _fontError, _fontListError, _fontRemoveError;
    /// <summary>The font <see cref="_fontError"/> is about, when a download of it failed.</summary>
    string? _fontErrorFont;
    bool _fontClosed;
    Action? _fontRefresh;
    IReadOnlyList<string>? _installedFonts;

    static readonly string[] DownloadableFonts = [Fonts.MiSans, Fonts.WenKai];

    static bool IsDownloaded(string font) => font == Fonts.WenKai ? WenKaiDownload.IsDownloaded : MiSansDownload.IsDownloaded;

    /// <summary>How much of a font is on disk: a finished download, or what an unfinished one left.</summary>
    static long OnDisk(string font) =>
        (font == Fonts.WenKai
            ? new[] { WenKaiDownload.FontPath, WenKaiDownload.PartPath, WenKaiDownload.LicencePath }
            : new[] { MiSansDownload.FontPath, MiSansDownload.LicencePath })
        .Where(File.Exists).Sum(path => new FileInfo(path).Length);

    static string Megabytes(long bytes) => $"{bytes / 1_000_000.0:0.#} MB";

    /// <summary>The font for lyrics and the rest of Lyricod; <paramref name="changed"/> follows each change.</summary>
    void FontPicker(Action changed)
    {
        var options = new List<(string Value, string Name)>
        {
            ("", L.Get("FontDefault")), (Fonts.MiSans, L.Get("FontRecommended")), (Fonts.WenKai, L.Get("FontWenKai")),
        };
        string? displayedChoice = null;
        var picker = new AutoSuggestBox { Width = 300, PlaceholderText = L.Get("FontSearch") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, L.Get("FontLabel"));
        // About the chosen font, when it's one Lyricod downloads: what it is, its licence, getting it.
        var info = new StackPanel { Spacing = 12, Margin = new Thickness(16, 4, 16, 12) };
        var description = new TextBlock { Style = Style("Detail"), TextWrapping = TextWrapping.Wrap };
        var licence = new HyperlinkButton { Content = L.Get("FontViewLicence"), Padding = new Thickness(0) };
        var download = new Button { Content = L.Get("FontDownload") };
        var cancel = new Button { Content = L.Get("ChooseLyricsCancel") };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Width = 240, VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock { Style = Style("Detail") };
        var error = new TextBlock { Style = Style("Detail"), TextWrapping = TextWrapping.Wrap };
        var listError = new TextBlock { Style = Style("Detail"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 0) };
        var retry = new Button { Content = L.Get("FontRetry") };
        var progressRow = Row(progress, cancel);
        info.Children.Add(description);
        info.Children.Add(licence);
        info.Children.Add(Row(download, retry));
        info.Children.Add(progressRow);
        info.Children.Add(status);
        info.Children.Add(error);
        Card(L.Get("FontLabel"), null, picker);
        Page.Children.Add(info);
        Page.Children.Add(listError);

        // The fonts on disk, each with its size and a way to remove it; only there while one is.
        var rows = new Dictionary<string, (FrameworkElement Row, TextBlock Detail)>();
        var removeError = new TextBlock { Style = Style("Detail"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 12) };
        var fold = Fold(L.Get("FontsDownloaded"), "", () =>
        {
            foreach (string font in DownloadableFonts)
            {
                var remove = new Button { Content = L.Get("FontRemove") };
                remove.Click += (_, _) => RemoveFont(font);
                var detail = Card(Fonts.NameOf(font), "", remove)!;
                rows[font] = ((FrameworkElement)_folding!.Children[^1], detail);
            }
            _folding!.Children.Add(removeError);
        });
        var foldDetail = (TextBlock)((StackPanel)fold.Header).Children[1];

        void Suggestions() => picker.ItemsSource = options.Where(o =>
            o.Name.Contains(picker.Text, StringComparison.CurrentCultureIgnoreCase)).Select(o => o.Name).ToArray();
        void Sync()
        {
            string choice = _settings.LyricsFont;
            if (displayedChoice != choice)
            {
                picker.Text = options.FirstOrDefault(o => o.Value == choice).Name ?? choice;
                displayedChoice = choice;
            }
            bool busy = _fontDownload != null, downloadable = Fonts.IsDownloadable(choice);
            bool wenKai = choice == Fonts.WenKai;
            // A failed download is shown under that font only, not under one chosen since.
            string? problem = (_fontErrorFont == null || _fontErrorFont == choice ? _fontError : null) ?? (downloadable ? Fonts.Error : null);
            info.Visibility = downloadable || busy || problem != null ? Visibility.Visible : Visibility.Collapsed;
            description.Text = L.Get(wenKai ? "WenKaiDownloadDetail" : "MiSansDownloadDetail");
            licence.NavigateUri = new Uri(wenKai ? WenKaiFont.LicenceUrl : MiSansArchive.LicenceUrl);
            description.Visibility = licence.Visibility = downloadable ? Visibility.Visible : Visibility.Collapsed;
            download.Visibility = !busy && downloadable && !IsDownloaded(choice) && problem == null ? Visibility.Visible : Visibility.Collapsed;
            progressRow.Visibility = status.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            error.Text = problem ?? "";
            error.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
            retry.Visibility = !busy && problem != null ? Visibility.Visible : Visibility.Collapsed;
            progress.IsIndeterminate = _fontProgress?.Total is not > 0;
            progress.Value = _fontProgress is { Total: > 0 } p ? 100.0 * p.Completed / p.Total.Value : 0;
            status.Text = _fontProgress?.Phase switch
            {
                FontDownloadPhase.Archive => L.Get("FontDownloadingArchive"),
                FontDownloadPhase.Licence => L.Get("FontDownloadingLicence"),
                _ => L.Format("FontDownloading", Fonts.NameOf(_fontDownloading ?? choice)),
            };
            listError.Text = _fontListError ?? "";
            listError.Visibility = _fontListError == null ? Visibility.Collapsed : Visibility.Visible;

            var onDisk = new List<string>();
            foreach (string font in DownloadableFonts)
            {
                long size = OnDisk(font);
                bool shown = size > 0 && _fontDownloading != font;
                rows[font].Row.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                rows[font].Detail.Text = Megabytes(size) + (Fonts.InUse == font ? " · " + L.Get("FontInUse")
                    : IsDownloaded(font) ? "" : " · " + L.Get("FontPartial"));
                if (shown) onDisk.Add(Fonts.NameOf(font) + " " + Megabytes(size));
            }
            foldDetail.Text = string.Join(" · ", onDisk);
            removeError.Text = _fontRemoveError ?? "";
            removeError.Visibility = _fontRemoveError == null ? Visibility.Collapsed : Visibility.Visible;
            fold.Visibility = onDisk.Count > 0 || _fontRemoveError != null ? Visibility.Visible : Visibility.Collapsed;
            changed();
        }
        void Select(string name)
        {
            var match = options.FindIndex(o => o.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
            if (match >= 0) { _fontError = _fontErrorFont = null; _settings.LyricsFont = options[match].Value; }
            displayedChoice = null;
            Sync();
        }
        picker.TextChanged += (_, args) =>
        {
            try { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) Suggestions(); }
            catch (Exception e) { _fontListError = e.Message; }
        };
        picker.GotFocus += (_, _) =>
        {
            try { picker.ItemsSource = options.Select(o => o.Name).ToArray(); picker.IsSuggestionListOpen = true; }
            catch (Exception e) { _fontListError = e.Message; }
        };
        picker.SuggestionChosen += (_, args) =>
        {
            try { if (args.SelectedItem is string name) Select(name); }
            catch (Exception e) { _fontError = e.Message; RefreshFontPane(); }
        };
        picker.QuerySubmitted += (_, args) =>
        {
            try { Select(args.QueryText); }
            catch (Exception e) { _fontError = e.Message; RefreshFontPane(); }
        };
        download.Click += (_, _) => _ = DownloadFont(_settings.LyricsFont);
        cancel.Click += (_, _) =>
        {
            try { _fontDownload?.Cancel(); }
            catch (Exception e) { _fontError = e.Message; RefreshFontPane(); }
        };
        void RemoveFont(string font)
        {
            try
            {
                // Only the font in use falls back to the default; removing another leaves the choice
                // and the font in use alone.
                bool inUse = _settings.LyricsFont == font;
                if (inUse) _settings.LyricsFont = "";
                if (font == Fonts.WenKai) WenKaiDownload.Remove(); else MiSansDownload.Remove();
                _fontRemoveError = null;
                if (inUse) Fonts.Reload();
            }
            catch (Exception e) { _fontRemoveError = L.Format("FontRemoveFailed", Fonts.NameOf(font)) + " " + e.Message; }
            RefreshFontPane();
        }
        retry.Click += (_, _) =>
        {
            try
            {
                // The font whose download failed, or else the chosen one, which may not have loaded.
                string font = _fontErrorFont ?? _settings.LyricsFont;
                _fontError = _fontErrorFont = null;
                if (!Fonts.IsDownloadable(font) || IsDownloaded(font)) { Fonts.Reload(); RefreshFontPane(); }
                else _ = DownloadFont(font);
            }
            catch (Exception e) { _fontError = e.Message; RefreshFontPane(); }
        };
        _fontRefresh = Sync;
        Watch(nameof(Settings.LyricsFont), Sync);
        Sync();
        _ = LoadFontNames();

        async Task LoadFontNames()
        {
            try
            {
                string language = L.Language;
                var names = _installedFonts ?? await Task.Run(() => InstalledFonts.Read(language));
                FontUi(() =>
                {
                    _installedFonts = names;
                    // A downloaded font Windows also lists is already in the list under its own entry.
                    var ours = new HashSet<string>(WenKaiFont.Families.Append("MiSans").Concat(options.Select(o => o.Name)), StringComparer.OrdinalIgnoreCase);
                    options.AddRange(names.Where(n => !ours.Contains(n)).Select(n => (n, n)));
                    if (_fontRefresh == Sync) Sync();
                });
            }
            catch (Exception e)
            {
                FontUi(() => { _fontListError = L.Get("FontListFailed") + " " + e.Message; RefreshFontPane(); });
            }
        }
    }

    async Task DownloadFont(string font)
    {
        if (_fontDownload != null || _fontClosed || !Fonts.IsDownloadable(font)) return;
        using var stop = new CancellationTokenSource();
        try
        {
            _fontDownload = stop;
            _fontDownloading = font;
            _fontError = _fontErrorFont = null;
            _fontProgress = null;
            RefreshFontPane();
            var progress = new FontProgress(p => FontUi(() =>
            {
                if (_fontDownload != stop) return;
                _fontProgress = p;
                RefreshFontPane();
            }));
            Task work = font == Fonts.WenKai ? WenKaiDownload.DownloadAsync(progress, stop.Token) : MiSansDownload.DownloadAsync(progress, stop.Token);
            await work.ConfigureAwait(false);
            // Load it if it's the font chosen; a font downloaded for later only needs the pane refreshed.
            FontUi(() => { if (_settings.LyricsFont == font) Fonts.Reload(); }, evenIfClosed: true);
        }
        catch (OperationCanceledException)
        {
            if (!stop.IsCancellationRequested) FontUi(() => { _fontError = L.Get("FontDownloadTimeout"); _fontErrorFont = font; });
        }
        catch (Exception e) { FontUi(() => { _fontError = L.Format("FontDownloadFailed", Fonts.NameOf(font)) + " " + e.Message; _fontErrorFont = font; }); }
        finally { FontUi(() => { _fontDownload = null; _fontDownloading = null; RefreshFontPane(); }); }
    }

    void FontUi(Action action, bool evenIfClosed = false)
    {
        try
        {
            _fontDispatcher.TryEnqueue(() =>
            {
                try { if (!_fontClosed || evenIfClosed) action(); }
                catch (Exception e) { _fontError = e.Message; Log.Write(e); }
            });
        }
        catch (Exception e) { Log.Write(e); }
    }

    void RefreshFontPane()
    {
        try { if (!_fontClosed) _fontRefresh?.Invoke(); }
        catch (Exception e) { Log.Write(e); }
    }

    void OnFontsChanged()
    {
        try { RefreshFontPane(); Refresh(); }
        catch (Exception e) { Log.Write(e); }
    }

    sealed class FontProgress(Action<FontDownloadProgress> report) : IProgress<FontDownloadProgress>
    {
        public void Report(FontDownloadProgress value) => report(value);
    }
}
