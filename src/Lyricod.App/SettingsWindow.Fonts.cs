using Lyricod.Core.Fonts;
using Lyricod.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lyricod;

public sealed partial class SettingsWindow
{
    readonly Microsoft.UI.Dispatching.DispatcherQueue _fontDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    CancellationTokenSource? _fontDownload;
    FontDownloadProgress? _fontProgress;
    string? _fontError, _fontListError;
    bool _fontClosed, _fontRemovalFailed;
    Action? _fontRefresh;
    IReadOnlyList<string>? _installedFonts;

    void FontPicker()
    {
        var options = new List<(string Value, string Name)> { ("", L.Get("FontDefault")), ("MiSans", L.Get("FontRecommended")) };
        string? displayedChoice = null;
        var picker = new AutoSuggestBox { Width = 300, PlaceholderText = L.Get("FontSearch") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, L.Get("LyricsFont"));
        var info = new StackPanel { Spacing = 12, Margin = new Thickness(16, 4, 16, 12) };
        var description = new TextBlock { Text = L.Get("MiSansDownloadDetail"), Style = Style("Detail"), TextWrapping = TextWrapping.Wrap };
        var licence = new HyperlinkButton { Content = L.Get("ViewLicence"), NavigateUri = new Uri(MiSansArchive.LicenceUrl), Padding = new Thickness(0) };
        var download = new Button { Content = L.Get("FontDownload") };
        var remove = new Button { Content = L.Get("FontRemove") };
        var cancel = new Button { Content = L.Get("ChooseLyricsCancel") };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Width = 240, VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock { Style = Style("Detail") };
        var error = new TextBlock { Style = Style("Detail"), TextWrapping = TextWrapping.Wrap };
        var listError = new TextBlock { Style = Style("Detail"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 0) };
        var retry = new Button { Content = L.Get("FontRetry") };
        var progressRow = Row(progress, cancel);
        info.Children.Add(description);
        info.Children.Add(licence);
        info.Children.Add(Row(download, remove, retry));
        info.Children.Add(progressRow);
        info.Children.Add(status);
        info.Children.Add(error);
        Card(L.Get("LyricsFont"), null, picker);
        Page.Children.Add(info);
        Page.Children.Add(listError);

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
            bool busy = _fontDownload != null, downloaded = MiSansDownload.IsDownloaded;
            info.Visibility = choice == "MiSans" || busy || downloaded || _fontError != null ? Visibility.Visible : Visibility.Collapsed;
            description.Visibility = licence.Visibility = choice == "MiSans" ? Visibility.Visible : Visibility.Collapsed;
            download.Visibility = !busy && !downloaded && choice == "MiSans" && _fontError == null ? Visibility.Visible : Visibility.Collapsed;
            remove.Visibility = !busy && (downloaded || File.Exists(MiSansDownload.FontPath)) ? Visibility.Visible : Visibility.Collapsed;
            progressRow.Visibility = status.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            string? problem = _fontError ?? (choice == "MiSans" ? Fonts.Error : null);
            error.Text = problem ?? "";
            error.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
            retry.Visibility = !busy && problem != null ? Visibility.Visible : Visibility.Collapsed;
            progress.IsIndeterminate = _fontProgress?.Total is not > 0;
            progress.Value = _fontProgress is { Total: > 0 } p ? 100.0 * p.Completed / p.Total.Value : 0;
            status.Text = L.Get(_fontProgress?.Phase switch
            {
                FontDownloadPhase.Archive => "FontDownloadingArchive",
                FontDownloadPhase.Licence => "FontDownloadingLicence",
                _ => "FontDownloading",
            });
            listError.Text = _fontListError ?? "";
            listError.Visibility = _fontListError == null ? Visibility.Collapsed : Visibility.Visible;
        }
        void Select(string name)
        {
            var match = options.FindIndex(o => o.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
            if (match >= 0) { _fontError = null; _fontRemovalFailed = false; _settings.LyricsFont = options[match].Value; }
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
        download.Click += (_, _) => _ = DownloadFont();
        cancel.Click += (_, _) =>
        {
            try { _fontDownload?.Cancel(); }
            catch (Exception e) { _fontError = e.Message; RefreshFontPane(); }
        };
        void RemoveFont()
        {
            try
            {
                _settings.LyricsFont = "";
                MiSansDownload.Remove();
                _fontError = null;
                _fontRemovalFailed = false;
                Fonts.Reload();
            }
            catch (Exception e) { _fontRemovalFailed = true; _fontError = L.Get("FontRemoveFailed") + " " + e.Message; }
            RefreshFontPane();
        }
        remove.Click += (_, _) => RemoveFont();
        retry.Click += (_, _) =>
        {
            try
            {
                _fontError = null;
                if (_fontRemovalFailed) RemoveFont();
                else if (MiSansDownload.IsDownloaded) { Fonts.Reload(); RefreshFontPane(); }
                else _ = DownloadFont();
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
                    options.AddRange(names.Where(n => !n.Equals("MiSans", StringComparison.OrdinalIgnoreCase)).Select(n => (n, n)));
                    if (_fontRefresh == Sync) Sync();
                });
            }
            catch (Exception e)
            {
                FontUi(() => { _fontListError = L.Get("FontListFailed") + " " + e.Message; RefreshFontPane(); });
            }
        }
    }

    async Task DownloadFont()
    {
        if (_fontDownload != null || _fontClosed) return;
        using var stop = new CancellationTokenSource();
        try
        {
            _fontDownload = stop;
            _fontError = null;
            _fontProgress = null;
            RefreshFontPane();
            var progress = new FontProgress(p => FontUi(() =>
            {
                if (_fontDownload != stop) return;
                _fontProgress = p;
                RefreshFontPane();
            }));
            await MiSansDownload.DownloadAsync(progress, stop.Token).ConfigureAwait(false);
            FontUi(() => Fonts.Reload(), evenIfClosed: true);
        }
        catch (OperationCanceledException)
        {
            if (!stop.IsCancellationRequested) FontUi(() => _fontError = L.Get("FontDownloadTimeout"));
        }
        catch (Exception e) { FontUi(() => _fontError = L.Get("FontDownloadFailed") + " " + e.Message); }
        finally { FontUi(() => { _fontDownload = null; RefreshFontPane(); }); }
    }

    void FontUi(Action action, bool evenIfClosed = false)
    {
        try
        {
            _fontDispatcher.TryEnqueue(() =>
            {
                try { if (!_fontClosed || evenIfClosed) action(); }
                catch (Exception e) { _fontError = e.Message; System.Diagnostics.Debug.WriteLine(e); }
            });
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }
    }

    void RefreshFontPane()
    {
        try { if (!_fontClosed) _fontRefresh?.Invoke(); }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }
    }

    void OnFontsChanged()
    {
        try { RefreshFontPane(); Refresh(); }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }
    }

    sealed class FontProgress(Action<FontDownloadProgress> report) : IProgress<FontDownloadProgress>
    {
        public void Report(FontDownloadProgress value) => report(value);
    }
}
