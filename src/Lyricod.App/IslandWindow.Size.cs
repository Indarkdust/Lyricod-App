using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Lyricod;

public sealed partial class IslandWindow
{
    /// <summary>Whether the current line has something under it (translation, pronunciation or the
    /// next line, as chosen); the second row and its height come and go with it.</summary>
    bool HasSecondRow { get; set; }

    void ApplyLayout()
    {
        double s = LayoutScale;
        LyricRow.Height = new GridLength(RowHeight);
        SecondRow.Height = new GridLength(HasSecondRow ? SecondHeight : 0);
        MainRow.Padding = new Thickness(CoverInset * s, 0, CoverInset * s, 0);
        MainRow.ColumnSpacing = 10 * s;
        CoverColumn.Width = BarsColumn.Width = new GridLength(CoverSize * s);
        ArtFrame.Width = ArtFrame.Height = CoverSize * s;
        ArtFrame.CornerRadius = new CornerRadius(CoverRadius * s);
        Bars.Height = BarHeight;
        Bars.Spacing = 1.5 * s;
        for (int i = 0; i < _bars.Count; i++)
        {
            _bars[i].Width = 2 * s;
            _bars[i].Height = BarHeight;
            _bars[i].RadiusX = _bars[i].RadiusY = s;
            _barVisuals[i].CenterPoint = new Vector3((float)s, (float)BarHeight / 2, 0);
        }
        SecondText.FontSize = 11 * s;
        // No cover beside the row under the lyric: it's kept to the text's width by its MaxWidth
        // (see FitText), and may use the gaps beside the covers while the pill widens, as the lyric does.
        Second.Margin = new Thickness((CoverInset + CoverSize) * s, -4 * s, (CoverInset + CoverSize) * s, 4 * s);
        Expanded.Padding = new Thickness(18 * s, 6 * s, 18 * s, 10 * s);
        Expanded.Spacing = 6 * s;
        PlayerRow.ColumnSpacing = 10 * s;
        BigArtFrame.Width = BigArtFrame.Height = 34 * s;
        BigArtFrame.CornerRadius = new CornerRadius(7 * s);
        SongLabels.Spacing = s;
        SongTitle.FontSize = 13 * s;
        SongArtist.FontSize = 12 * s;
        PlayerDivider.Width = s;
        PlayerDivider.Height = 18 * s;
        foreach (var button in new[] { LyricsButton, SettingsButton })
        {
            button.Padding = new Thickness(8 * s, 6 * s, 8 * s, 6 * s);
            button.CornerRadius = new CornerRadius(4 * s);
            if (button.Content is FontIcon icon) icon.FontSize = 13 * s;
        }
        _transport.SetLayoutScale(s, 13);
        _progress.SetLayoutScale(s);
        _widths.Clear();
    }

    /// <summary>Fits the displayed text, keeping room for the mini player while expanded.</summary>
    void Resize()
    {
        double required = _textWidth + 2 * SideWidth;
        double width = _expanded ? Math.Max(required, ExpandedMinWidth) : required;
        double height = RowHeight + (HasSecondRow ? SecondHeight : 0) + (_expanded ? ExpandedHeight : 0);
        if (width != _widthTarget)
        {
            double from = Pill.Width;
            _widthAnimation?.Stop();
            Pill.Width = from;
            _widthTarget = width;
            var story = Animate(Pill, "Width", width, from, width > from ? 0.18 : 0.5);
            _widthAnimation = story;
            _widthAnimating = true;
            story.Completed += (_, _) =>
            {
                try
                {
                    if (_closed || !ReferenceEquals(_widthAnimation, story)) return;
                    _widthAnimating = false;
                    if (_waitingForWidth)
                    {
                        _waitingForWidth = false;
                        RevealText();
                    }
                    Place();
                }
                catch (Exception e) { Log.Write(e); }
            };
            story.Begin();
        }
        // Don't expose new text inside a pill that's still too narrow: hide it only while the pill
        // is widening and too narrow for it (see TextFits), and never for long. Shrinking is safe:
        // the incoming text already fits.
        _waitingForWidth = _widthAnimating && !TextFits(Pill.Width);
        if (_waitingForWidth)
        {
            _lineAnimation?.Stop();
            LyricBox.Opacity = Second.Opacity = 0;
            _revealTimer.Stop();
            _revealTimer.Start();
        }
        if (height != _heightTarget)
        {
            double from = Pill.Height;
            _heightAnimation?.Stop();
            Pill.Height = from;
            _heightTarget = height;
            var story = Animate(Pill, "Height", height, from);
            _heightAnimation = story;
            story.Completed += (_, _) =>
            {
                try
                {
                    if (_closed || !ReferenceEquals(_heightAnimation, story)) return;
                    _playerMoving = false;
                    Place();
                    ApplyCorners();
                }
                catch (Exception e) { Log.Write(e); }
            };
            story.Begin();
        }
        ApplyCorners();
        if (!_waitingForWidth) RevealText();
    }

    /// <summary>The mini player's corner, at Medium size.</summary>
    const double PlayerRadius = 20;

    /// <summary>
    /// The default island is square where it meets the top edge; a floating one has the same corner
    /// all round. Either way the cover's corners share their centre with the island's. The mini
    /// player's corner is larger, and grows and shrinks with the player's height as it opens and
    /// closes rather than changing all at once.
    /// </summary>
    void ApplyCorners()
    {
        double open = _expanded ? 1 : 0;
        if (_playerMoving)
        {
            double closed = RowHeight + (HasSecondRow ? SecondHeight : 0);
            double height = Pill.ActualHeight > 0 ? Pill.ActualHeight : Pill.Height;
            open = Math.Clamp((height - closed) / ExpandedHeight, 0, 1);
        }
        double radius = (PillRadius + (PlayerRadius - PillRadius) * open) * LayoutScale;
        var corners = Floating ? new CornerRadius(radius) : new CornerRadius(0, 0, radius, radius);
        if (Pill.CornerRadius != corners) Pill.CornerRadius = corners;
        UpdateShadow();
        if (_shaped) Shape();
        UpdateLiquid();
    }

    /// <summary>A fast start and a soft settle, without overshooting.</summary>
    static Storyboard Animate(FrameworkElement element, string property, double to, double from, double seconds = 0.5)
    {
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = TimeSpan.FromSeconds(seconds), EnableDependentAnimation = true,
            EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, property);
        return new Storyboard { Children = { animation } };
    }

    void FitText()
    {
        _textWidth = Math.Clamp(Math.Ceiling(Math.Max(_pieceWidth, _secondWidth)), MinText, MaxText);
        SecondText.MaxWidth = _textWidth;
        Resize();
    }

    /// <summary>
    /// Whether the pill is wide enough to show its text between the covers on either side. The gaps
    /// beside the covers may still be closing: the widening pill gets there within the first tenth
    /// of a second, well before it settles, so the text comes in then. The row under the lyric has
    /// the same room (see ApplyLayout).
    /// </summary>
    bool TextFits(double pillWidth) => pillWidth - 2 * (CoverInset + CoverSize) * LayoutScale >= _textWidth;

    /// <summary>Shows text hidden while the pill widened as soon as it fits (called as the pill grows).</summary>
    void RevealWhenItFits()
    {
        if (!_waitingForWidth || _closed || !TextFits(Pill.ActualWidth)) return;
        _waitingForWidth = false;
        _revealTimer.Stop();
        RevealText();
    }

    void RevealText()
    {
        LyricBox.Opacity = 1;
        Second.Opacity = 1;
        if (_transitionPending)
        {
            _transitionPending = false;
            MoveIn();
        }
    }

    double Measure(string text, FontFamily font, double size, Windows.UI.Text.FontWeight weight)
    {
        var key = (text, font.Source, size, weight.Weight);
        if (_widths.TryGetValue(key, out var w)) return w;
        Measurer.FontFamily = font;
        Measurer.FontSize = size;
        Measurer.FontWeight = weight;
        Measurer.Text = text;
        Measurer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        w = Measurer.DesiredSize.Width;
        if (_widths.Count > 2000) _widths.Clear();
        return _widths[key] = w;
    }

    double MeasureLyric(string text) => Measure(text, Lyric.FontFamily, Lyric.FontSize, Lyric.FontWeight);

    double MeasureSecond(string text) => Measure(text, SecondText.FontFamily, SecondText.FontSize, SecondText.FontWeight);
}
