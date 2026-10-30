using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lyricod;

public sealed partial class LyricsWindow
{
    /// <summary>Wide (cover beside the lyrics) or narrow (control bar), and the text size.</summary>
    void ApplyLayout(bool force = false)
    {
        double w = Root.ActualWidth > 0 ? Root.ActualWidth : 520, h = Root.ActualHeight > 0 ? Root.ActualHeight : 760;
        bool wide = w >= 820 && w > h * 1.05;
        double automatic = wide ? Math.Clamp(h / 19, 30, 64) : 30;
        double size = Math.Round(_settings.LyricsTextSize switch
        {
            LyricsTextSize.Small => automatic * 0.8,
            LyricsTextSize.Large => automatic * 1.25,
            _ => automatic,
        });
        bool changed = force || wide != _wide || size != _fontSize;
        _wide = wide;
        _fontSize = size;

        // Wide, as on the Mac: the cover and the lyrics as one centred block, the lyrics set in from
        // the cover, so the pair sits balanced instead of hugging the left edge in full screen; the
        // cover, title and controls grow with the window. (Every resize: the block follows the width.)
        double block = Math.Min(w, 1700), margin = wide ? (w - block) / 2 : 0;
        CoverColumn.Width = new GridLength(wide ? margin + Math.Min(block * 0.4, 600) : 0);
        CoverPanel.Margin = new Thickness(margin, 0, 0, 0);
        Scroller.Margin = Message.Margin = new Thickness(wide ? block * 0.04 : 0, 0, margin, 0);
        double side = Math.Max(Math.Min(Math.Min(block * 0.38 - 96, h * 0.5), 460), 120);
        BigCoverFrame.Width = BigCoverFrame.Height = BigTitles.Width = BigProgressHost.Width = side;
        BigCoverFrame.CornerRadius = new CornerRadius(side * 0.06);
        double titleSize = Math.Clamp(side / 13, 17, 32);
        BigTitle.FontSize = Math.Round(titleSize);
        BigArtist.FontSize = Math.Round(titleSize * 0.7);
        if (!changed) return;

        CoverPanel.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        ControlBar.Visibility = !wide && _model.Track != null ? Visibility.Visible : Visibility.Collapsed;
        Move(_transport, wide ? BigTransportHost : SmallTransportHost);
        Move(_progress, wide ? BigProgressHost : SmallProgressHost);
        _transport.SetLayoutScale(wide ? 1.35 : 1, 16);
        _progress.SetLayoutScale(wide ? 1.15 : 1);
        UpdateControls();
        _widths.Clear();
        BuildLines();
    }

    static void Move(FrameworkElement element, Panel host)
    {
        if (element.Parent == host) return;
        if (element.Parent is Panel old) old.Children.Remove(element);
        host.Children.Add(element);
    }

    void Relayout()
    {
        ApplyLayout();
        // Same layout, new width: only the sung line's rows change.
        if (_current is int i && i < _views.Count)
        {
            RenderLine(_views[i], current: true);
            AnimateCurrent();
        }
        UpdatePadding();
        ScrollToCurrent(animate: false);
    }

    void UpdatePadding()
    {
        double h = Scroller.ActualHeight > 0 ? Scroller.ActualHeight : 600;
        double side = _wide ? 40 : 28;
        // Room above and below, so the first and last lines can sit at the reading point.
        Lines.Padding = new Thickness(side, h * 0.3, side, h * 0.6 + 100);
    }

    double TextWidth => Math.Max((Scroller.ActualWidth > 0 ? Scroller.ActualWidth : 480) - 2 * (_wide ? 40 : 28), 100);
}
