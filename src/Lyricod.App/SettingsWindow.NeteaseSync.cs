using Lyricod.Core;
using Lyricod.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lyricod;

/// <summary>
/// Settings › General › NetEase Cloud Music sync: sets up BetterNCM and InfLink-rs through Lyricod CM
/// Helper (see <see cref="NeteaseSync"/>), saying first what that means. Shown only when NetEase
/// Cloud Music is installed.
/// </summary>
public sealed partial class SettingsWindow
{
    /// <summary>A setup is running; one at a time, whichever page started it.</summary>
    static bool _neteaseBusy;

    void NeteaseSyncSection()
    {
        if (NeteaseSetup.Find() is not { } netease) return;
        TextBlock? status = null;
        var install = new Button { Style = Style("AccentButtonStyle") };
        var remove = new Button { Content = L.Get("NeteaseSyncRemove") };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(remove);
        buttons.Children.Add(install);
        Fold(L.Get("NeteaseSyncTitle"), L.Get("NeteaseSyncDetail"), () =>
        {
            Card(L.Get("NeteaseSyncWhat"), L.Get("NeteaseSyncWhatDetail"),
                new HyperlinkButton { Content = L.Get("NeteaseSyncLearnMore"), NavigateUri = new Uri(NeteaseSync.HelperPage) });
            Card(L.Get("NeteaseSyncBefore"), L.Get("NeteaseSyncBeforeDetail"), null);
            var version = Value();
            version.Text = L.Format(NeteaseSync.IsTested(netease.Version) ? "NeteaseSyncTested" : "NeteaseSyncUntested", netease.Version.ToString(3));
            Card(L.Get("NeteaseSyncVersion"), netease.Is64Bit ? null : L.Get("NeteaseSyncUnsupported"), version);
            // Only the 64-bit NetEase Cloud Music 3.x can take BetterNCM 1.3.4.
            if (netease.Is64Bit) status = Card(L.Get("NeteaseSyncStatus"), "", buttons);
        });
        if (status is not { } line) return;

        void Show(string? message = null)
        {
            install.Content = L.Get(netease.SyncInstalled ? "NeteaseSyncUpdate" : "NeteaseSyncInstall");
            remove.Visibility = netease.SyncInstalled ? Visibility.Visible : Visibility.Collapsed;
            install.IsEnabled = remove.IsEnabled = !_neteaseBusy;
            line.Text = message ?? L.Get(netease.SyncInstalled ? "NeteaseSyncInstalled" : "NeteaseSyncNotInstalled");
        }

        async void Run(bool removing)
        {
            if (_neteaseBusy) return;
            _neteaseBusy = true;
            Show();
            var progress = new Progress<NeteaseStage>(stage =>
            {
                try { line.Text = L.Get(stage == NeteaseStage.Downloading ? "NeteaseSyncDownloading" : "NeteaseSyncWaiting"); }
                catch (Exception) { /* the window closed meanwhile */ }
            });
            (NeteaseResult Result, string? Error) outcome;
            try { outcome = await NeteaseSetup.RunAsync(netease, removing, progress); }
            catch (Exception e) { outcome = (NeteaseResult.Failed, e.Message); }
            finally { _neteaseBusy = false; }
            try
            {
                netease = NeteaseSetup.Find() ?? netease;
                Show(outcome.Result switch
                {
                    NeteaseResult.Done => L.Get(removing ? "NeteaseSyncRemoved" : "NeteaseSyncDone"),
                    NeteaseResult.Cancelled => L.Get("NeteaseSyncCancelled"),
                    NeteaseResult.CouldntDownload => L.Get("NeteaseSyncCouldntDownload"),
                    _ => L.Format("NeteaseSyncFailed", outcome.Error ?? ""),
                });
            }
            catch (Exception) { /* the window closed meanwhile */ }
        }

        install.Click += (_, _) => Run(removing: false);
        remove.Click += (_, _) => Run(removing: true);
        Show();
    }
}
