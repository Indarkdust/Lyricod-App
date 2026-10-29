using Lyricod.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lyricod;

public sealed partial class SettingsWindow
{
    void UpdateControls()
    {
        var status = new TextBlock { Style = Style("Detail"), TextWrapping = TextWrapping.Wrap };
        var detail = new TextBlock { Style = Style("Detail"), TextWrapping = TextWrapping.Wrap };
        var check = new Button { Content = L.Get("CheckNow") };
        var action = new Button();
        var cancel = new Button { Content = L.Get("ChooseLyricsCancel") };
        var page = new HyperlinkButton();
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Width = 240, VerticalAlignment = VerticalAlignment.Center };
        var progressText = new TextBlock { Style = Style("Detail"), VerticalAlignment = VerticalAlignment.Center };
        var progressRow = Row(progress, progressText, cancel);
        check.Click += async (_, _) => { try { await _updates.CheckAsync(); } catch (Exception) { } };
        action.Click += async (_, _) => { try { await _updates.ActivateAsync(); } catch (Exception) { } };
        cancel.Click += (_, _) => { try { _updates.Cancel(); } catch (Exception) { } };
        page.Click += (_, _) => { try { _updates.OpenReleasePage(); } catch (Exception) { } };
        void Sync()
        {
            bool busy = _updates.State is UpdateState.Downloading or UpdateState.Applying;
            bool available = _updates.Available != null, install = _updates.CanInstall;
            check.IsEnabled = !_updates.Checking && _updates.State != UpdateState.Applying;
            status.Text = available ? L.Format("NewVersionAvailable", _updates.Available!.Version.ToString(3))
                : _updates.Checking ? L.Get("CheckingUpdates") : _updates.Result?.Status switch
                {
                    UpdateStatus.UpToDate => L.Get("UpToDate"),
                    UpdateStatus.CouldntCheck => L.Get("CouldntCheckUpdates"),
                    _ => "",
                };
            detail.Text = _updates.Error ?? (_updates.State switch
            {
                UpdateState.Ready => L.Get("UpdateRestartNote"),
                UpdateState.Applying => L.Get("UpdateApplying"),
                UpdateState.Downloading when _updates.Progress?.Percent == 100 => L.Get("UpdatePreparing"),
                _ => "",
            });
            detail.Visibility = detail.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            action.Content = L.Get(_updates.State switch
            {
                UpdateState.Ready => "UpdateRestart",
                UpdateState.Error => "UpdateRetry",
                _ => "UpdateDownloadInstall",
            });
            action.Visibility = available && install && !busy ? Visibility.Visible : Visibility.Collapsed;
            page.Content = L.Get(install ? "UpdateWhatsNew" : "DownloadUpdate");
            page.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            progressRow.Visibility = _updates.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
            progress.Value = _updates.Progress?.Percent ?? 0;
            progressText.Text = L.Format("UpdatePercent", _updates.Progress?.Percent ?? 0);
        }
        _songWatchers.Add(Sync);
        Sync();
        Group(status, detail, progressRow, Row(check, action, page));
    }
}
