using System.Numerics;
using Lyricod.Core;
using Lyricod.Core.Romanization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Lyricod;

public sealed partial class LyricsWindow
{
    void BuildLines()
    {
        Lines.Children.Clear();
        _views.Clear();
        _current = null;
        var lyrics = _model.Lyrics;
        MessageButton.Visibility = MessageDetail.Visibility = Visibility.Collapsed;
        if (_model.Track == null || _model.IsSearching || lyrics == null)
        {
            MessageText.Text = _model.Track == null ? L.Get("NothingPlaying") : _model.IsSearching ? L.Get("Searching") : _model.IsNotSong ? L.Get("NotASong") : L.Get("NoLyrics");
            if (_model.Track != null && !_model.IsSearching)
            {
                // As on the Mac: search for them yourself and pick the right song.
                MessageDetail.Text = L.Get("NoLyricsDetail");
                MessageButton.Content = L.Get("ChooseLyricsTip");
                MessageDetail.Visibility = _model.IsNotSong ? Visibility.Collapsed : Visibility.Visible;
                MessageButton.Visibility = Visibility.Visible;
            }
            Message.Visibility = Visibility.Visible;
            return;
        }
        Message.Visibility = Visibility.Collapsed;
        UpdatePadding();
        foreach (var line in lyrics.Lines)
        {
            var panel = new StackPanel
            {
                Spacing = _fontSize * 0.2, Margin = new Thickness(0, 0, 0, _fontSize * 0.73),
                HorizontalAlignment = _settings.LyricsCentered ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent), // hit-testable for clicks
                // Fading and shrinking run on the compositor.
                OpacityTransition = new ScalarTransition { Duration = Transition },
                ScaleTransition = new Vector3Transition { Duration = Transition },
            };
            // Lines shrink towards their start (or middle, when centred), as on the Mac.
            panel.SizeChanged += (_, e) => panel.CenterPoint =
                new Vector3(_settings.LyricsCentered ? (float)e.NewSize.Width / 2 : 0, (float)e.NewSize.Height / 2, 0);
            var view = new LineView(line, panel);
            panel.Tapped += (_, _) => { if (lyrics.IsSynced) _model.Seek(line); };
            _views.Add(view);
            RenderLine(view, current: false);
            Lines.Children.Add(panel);
        }
        MoveToCurrent(animate: false);
    }

    /// <summary>Draws a line plain (wrapped, faded) or, while it's being sung, as rows that sweep.</summary>
    void RenderLine(LineView view, bool current)
    {
        var lyrics = _model.Lyrics;
        if (lyrics == null) return;
        var line = view.Line;
        var song = Romanizer.SongLanguage(lyrics);
        var align = _settings.LyricsCentered ? TextAlignment.Center : TextAlignment.Left;
        var font = Fonts.For(line.Text, song);
        view.Panel.Children.Clear();
        view.Rows.Clear();
        view.Soft.Clear();

        if (current && lyrics.IsSynced)
        {
            var pieces = LinePieces.Split(line, TextWidth, Measure(font));
            // Without word timing every row lights as the line starts, as on the Mac.
            if (line.Words == null) pieces = [.. pieces.Select(p => p with { Start = line.Start })];
            foreach (var piece in pieces)
            {
                var text = new TextBlock
                {
                    Text = piece.Text, FontFamily = font, FontSize = _fontSize, FontWeight = Fonts.BoldWeight,
                    Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.NoWrap,
                };
                // The unsung text is dimmed by a wrapper: the sweep takes the text's shape, which
                // would be dim too if the text itself were.
                var dim = new Grid { Opacity = 0.35 };
                dim.Children.Add(text);
                var host = new Grid { IsHitTestVisible = false };
                var row = new Grid { HorizontalAlignment = _settings.LyricsCentered ? HorizontalAlignment.Center : HorizontalAlignment.Left };
                row.Children.Add(dim);
                row.Children.Add(host);
                var sweep = new SweepPainter(text, host) { Color = Colors.White, Effect = _settings.LyricsEffect };
                view.Rows.Add((piece, Math.Ceiling(Measure(font)(piece.Text)) + 1, sweep));
                view.Panel.Children.Add(row);
            }
        }
        else
        {
            Add(view, new TextBlock
            {
                Text = line.Text, FontFamily = font, FontSize = _fontSize, FontWeight = Fonts.BoldWeight,
                Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.Wrap, TextAlignment = align,
            }, soft: true);
        }
        if (_settings.ShowPronunciation && line.Pronunciation is { } p)
            Add(view, Extra(p.Text, Math.Round(_fontSize * 0.5), current ? 0.7 : 0.3, song, align), soft: !current);
        if (_settings.ShowTranslations && line.Translation is { } tr)
            Add(view, Extra(tr, Math.Round(_fontSize * 0.6), current ? 0.75 : 0.3, song, align), soft: !current);
        ApplyDistance(view);
    }

    /// <summary>Adds a text to a line, able to soften (blur) unless the line is being sung.</summary>
    static void Add(LineView view, TextBlock text, bool soft)
    {
        if (!soft)
        {
            view.Panel.Children.Add(text);
            return;
        }
        var softText = new SoftText(text);
        view.Soft.Add(softText);
        view.Panel.Children.Add(softText.Element);
    }

    static TextBlock Extra(string text, double size, double opacity, LyricLanguage? song, TextAlignment align) => new()
    {
        Text = text, FontFamily = Fonts.For(text, song), FontSize = size, FontWeight = Fonts.SemiboldWeight,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(opacity * 255), 255, 255, 255)),
        TextWrapping = TextWrapping.Wrap, TextAlignment = align,
    };

    /// <summary>
    /// The line being sung is full size and sharp; the others shrink a little, and fade and blur more
    /// the further they are (as on the Mac: up to 8 points of blur). Unsynced lyrics are all plain.
    /// </summary>
    void ApplyDistance(LineView view)
    {
        bool synced = _model.Lyrics is { IsSynced: true };
        bool current = synced && view.Line.Id == _current;
        int distance = Math.Abs(view.Line.Id - (_current ?? -1));
        view.Panel.Opacity = !synced ? 0.85 : current ? 1 : 0.35 * Math.Max(0.45, 1 - (distance - 1) * 0.12);
        view.Panel.Scale = current || !synced ? Vector3.One : new Vector3(0.96f, 0.96f, 1);
        double blur = !synced || current || _current == null || !_settings.LyricsBlurDistant ? 0 : Math.Min(distance * 1.2, 8);
        foreach (var soft in view.Soft) soft.Blur(blur);
    }

    Func<string, double> Measure(FontFamily font)
    {
        if (Measurer.FontFamily != font || Measurer.FontSize != _fontSize)
        {
            Measurer.FontFamily = font;
            Measurer.FontSize = _fontSize;
            Measurer.FontWeight = Fonts.BoldWeight;
            _widths.Clear();
        }
        return text =>
        {
            if (_widths.TryGetValue(text, out var w)) return w;
            Measurer.Text = text;
            Measurer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            w = Measurer.DesiredSize.Width;
            if (_widths.Count > 400) _widths.Clear();
            return _widths[text] = w;
        };
    }

    /// <summary>Redraws the old and new sung lines, refades the rest and scrolls to the new one.</summary>
    void MoveToCurrent(bool animate)
    {
        var lyrics = _model.Lyrics;
        int? index = lyrics is { IsSynced: true } ? _model.CurrentIndex : null;
        if (index is int n && n >= _views.Count) index = null;
        if (index == _current && animate) return;
        var old = _current;
        _current = index;
        if (old is int o && o < _views.Count) RenderLine(_views[o], current: false);
        if (index is int i) RenderLine(_views[i], current: true);
        foreach (var view in _views) ApplyDistance(view);
        AnimateCurrent();
        ScrollToCurrent(animate);
    }

    /// <summary>Puts the sung line a third of the way down.</summary>
    void ScrollToCurrent(bool animate)
    {
        if (_current is not int target || target >= _views.Count) return;
        Lines.UpdateLayout();
        // Before the window's first layout the viewport has no height yet; Relayout scrolls again.
        double viewport = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight;
        var top = _views[target].Panel.TransformToVisual(Lines).TransformPoint(new Point(0, 0)).Y;
        Scroller.ScrollTo(0, Math.Max(0, top - viewport * 0.3),
            new ScrollingScrollOptions(animate ? ScrollingAnimationMode.Enabled : ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
    }

    /// <summary>Scrolls to the next line with a soft, spring-like ease (the Mac's spring), not Windows' quick default.</summary>
    void OnScrollAnimationStarting(ScrollView sender, ScrollingScrollAnimationStartingEventArgs args)
    {
        if (args.Animation is not Microsoft.UI.Composition.Vector3KeyFrameAnimation stock) return;
        var compositor = stock.Compositor;
        var eased = compositor.CreateVector3KeyFrameAnimation();
        eased.InsertKeyFrame(1, new Vector3((float)args.EndPosition.X, (float)args.EndPosition.Y, 0),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.25f, 1)));
        eased.Duration = TimeSpan.FromSeconds(0.6);
        args.Animation = eased;
    }

    void AnimateCurrent()
    {
        if (_current is not int i || i >= _views.Count) return;
        double now = _model.Position();
        var font = Fonts.For(_views[i].Line.Text, _model.Lyrics is { } l ? Romanizer.SongLanguage(l) : null);
        var measure = Measure(font);
        foreach (var (piece, width, sweep) in _views[i].Rows)
            sweep.Animate(piece, width, now, _model.IsPlaying, measure);
    }

    /// <summary>
    /// Where the lyrics are the point (wide windows and full screen, while playing), the controls
    /// fade once the pointer rests, as on the Mac, and come back when it moves.
    /// </summary>
    void UpdateControls()
    {
        bool hide = _settings.LyricsAutoHide && _wide && _model.IsPlaying && _pointerIdle;
        TopButtons.Opacity = BigProgressHost.Opacity = BigTransportHost.Opacity = hide ? 0 : 1;
    }
}
