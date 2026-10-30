using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Lyricod;

public sealed partial class LyricsWindow
{
    void OnTranslation(object sender, RoutedEventArgs e) => _settings.ShowTranslations = !_settings.ShowTranslations;
    void OnPronunciation(object sender, RoutedEventArgs e) => _settings.ShowPronunciation = !_settings.ShowPronunciation;
    void OnChooseLyrics(object sender, RoutedEventArgs e) => _chooseLyrics();

    void OnFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();

    void ToggleFullScreen() =>
        AppWindow.SetPresenter(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Overlapped : AppWindowPresenterKind.FullScreen);

    void UpdateFullScreenIcon()
    {
        bool full = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        FullScreenIcon.Glyph = full ? "\uE73F" : "\uE740"; // Back to window / Full screen
        TitleBar.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.F11:
                ToggleFullScreen();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape when AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen:
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Space:
                _ = _model.PlayPause();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Keeps the toggle buttons showing the settings.</summary>
    void SyncButtons()
    {
        TranslationButton.Opacity = _settings.ShowTranslations ? 1 : 0.5;
        PronunciationButton.Opacity = _settings.ShowPronunciation ? 1 : 0.5;
    }
}
