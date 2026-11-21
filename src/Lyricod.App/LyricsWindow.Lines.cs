using System.Globalization;
using System.Numerics;
using Lyricod.Core;
using Lyricod.Core.Romanization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Lyricod;

public sealed partial class LyricsWindow
{
    /// <summary>What the window last showed: a song's lyrics, or the message in their place.</summary>
    object? _builtFor;
    /// <summary>Measures a pronunciation's rows, in their font.</summary>
    readonly TextRuler _extraRuler;

    /// <summary>
    /// What's new (another song's lyrics, or word that there are none) fades in; the same lyrics
    /// laid out again (a resize, a setting) don't.
    /// </summary>
    static void FadeIn(UIElement element)
    {
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromSeconds(0.35), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        new Storyboard { Children = { fade } }.Begin();
    }

    void BuildLines()
    {
        Lines.Children.Clear();
        _views.Clear();
        _current = null;
        _browsing = false;
        _browse.Stop();
        _settling = null;
        _settle.Stop();
        var lyrics = _model.Lyrics;
        MessageButton.Visibility = MessageDetail.Visibility = Visibility.Collapsed;
        if (_model.Track == null || _model.IsSearching || lyrics == null)
        {
            string message = _model.Track == null ? L.Get("NothingPlaying") : _model.IsSearching ? L.Get("Searching") : _model.IsNotSong ? L.Get("NotASong") : L.Get("NoLyrics");
            if (!Equals(_builtFor, message)) FadeIn(Message);
            _builtFor = message;
            MessageText.Text = message;
            if (_model.Track != null && !_model.IsSearching)
            {
                // Search for them yourself and pick the right song.
                MessageDetail.Text = L.Get("NoLyricsDetail");
                MessageButton.Content = L.Get("ChooseLyricsTip");
                MessageDetail.Visibility = _model.IsNotSong ? Visibility.Collapsed : Visibility.Visible;
                MessageButton.Visibility = Visibility.Visible;
            }
            Message.Visibility = Visibility.Visible;
            return;
        }
        Message.Visibility = Visibility.Collapsed;
        if (!ReferenceEquals(_builtFor, lyrics)) FadeIn(Lines);
        _builtFor = lyrics;
        UpdatePadding();
        _linesWidth = TextWidth;
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
            // Lines shrink towards their start (or middle, when centred).
            panel.SizeChanged += (_, e) => panel.CenterPoint =
                new Vector3(_settings.LyricsCentered ? (float)e.NewSize.Width / 2 : 0, (float)e.NewSize.Height / 2, 0);
            var view = new LineView(line, panel);
            panel.Tapped += (_, _) =>
            {
                if (!lyrics.IsSynced) return;
                // Picking a line ends the look around: the window goes there.
                Browse(false);
                _model.Seek(line);
            };
            _views.Add(view);
            RenderLine(view, current: false);
            Lines.Children.Add(panel);
        }
        MoveToCurrent(animate: false);
    }

    /// <summary>How bright the part of the sung line still to come is.</summary>
    const double Unsung = 0.35;
    /// <summary>How far a sung word is raised by the lift, of the text's size.</summary>
    const double LiftRise = 0.075;

    /// <summary>
    /// Draws a line plain (faded) or, while it's being sung, as rows that sweep. Both break into
    /// the same rows (<c>LinePieces</c>), so a line doesn't move or rewrap when it lights up.
    /// <paramref name="settling"/>: the line was just sung and its words are still coming down, so
    /// its rows stay and only what's under them is drawn anew, plain.
    /// </summary>
    void RenderLine(LineView view, bool current, bool settling = false)
    {
        var lyrics = _model.Lyrics;
        if (lyrics == null) return;
        var line = view.Line;
        var song = Romanizer.SongLanguage(lyrics);
        var align = _settings.LyricsCentered ? TextAlignment.Center : TextAlignment.Left;
        var font = Fonts.For(line.Text, song);
        // A line's rows come first in its panel, one each.
        if (settling)
        {
            while (view.Panel.Children.Count > view.Rows.Count) view.Panel.Children.RemoveAt(view.Panel.Children.Count - 1);
        }
        else
        {
            view.Panel.Children.Clear();
            view.Rows.Clear();
        }
        view.ExtraRows.Clear();
        view.Soft.Clear();

        if (settling)
        {
            // The rows stay, coming down.
        }
        else if (current && lyrics.IsSynced)
        {
            var pieces = LinePieces.Split(line, TextWidth, Measure(font));
            // Without word timing every row lights as the line starts.
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
                var dim = new Grid { Opacity = Unsung };
                dim.Children.Add(text);
                var host = new Grid { IsHitTestVisible = false };
                var row = new Grid { HorizontalAlignment = _settings.LyricsCentered ? HorizontalAlignment.Center : HorizontalAlignment.Left };
                row.Children.Add(dim);
                row.Children.Add(host);
                var sweep = new SweepPainter(text, host) { Color = Colors.White, Effect = _settings.LyricsEffect };
                // Words that rise as they're sung are drawn by the sweep, the unsung part too: the
                // text only gives them its shape (it can't be fully transparent and still have one).
                if (_settings.LyricsEffect == SungEffect.Lift && sweep.Lift(piece, Measure(font), Unsung, _fontSize * LiftRise))
                    dim.Opacity = 0.01;
                view.Rows.Add((piece, Math.Ceiling(Measure(font)(piece.Text)) + 1, sweep));
                view.Panel.Children.Add(row);
            }
        }
        else
        {
            foreach (var piece in LinePieces.Split(line, TextWidth, Measure(font)))
                Add(view, new TextBlock
                {
                    Text = piece.Text, FontFamily = font, FontSize = _fontSize, FontWeight = Fonts.BoldWeight,
                    Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.NoWrap,
                    HorizontalAlignment = _settings.LyricsCentered ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                }, soft: true);
        }
        if (_settings.ShowPronunciation && line.Pronunciation is { } p)
            AddPronunciation(view, p, current, song, align);
        if (_settings.ShowTranslations && line.Translation is { } tr)
            Add(view, Extra(tr, Math.Round(_fontSize * 0.6), current ? 0.75 : 0.3, song, align), soft: !current);
        ApplyDistance(view);
    }

    /// <summary>
    /// The pronunciation under a line. One with timing is lit as it's sung, in rows broken as
    /// the line's are (the same sung or not, so it doesn't rewrap). Any other is one text that
    /// wraps.
    /// </summary>
    void AddPronunciation(LineView view, Pronunciation p, bool current, LyricLanguage? song, TextAlignment align)
    {
        double size = Math.Round(_fontSize * 0.5);
        if (_model.Lyrics is not { IsSynced: true } || p.Sung(view.Line) is not { } sung)
        {
            Add(view, Extra(p.Text, size, current ? 0.7 : 0.3, song, align), soft: !current);
            return;
        }
        var font = Fonts.For(p.Text, song);
        _extraRuler.Use(font, size, Fonts.SemiboldWeight);
        var side = _settings.LyricsCentered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        var rows = new StackPanel { HorizontalAlignment = side };
        foreach (var piece in LinePieces.Split(sung, TextWidth, _extraRuler.Measure))
        {
            var text = new TextBlock
            {
                Text = piece.Text, FontFamily = font, FontSize = size, FontWeight = Fonts.SemiboldWeight, TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = side,
                Foreground = new SolidColorBrush(current ? Colors.White : Windows.UI.Color.FromArgb((byte)(0.3 * 255), 255, 255, 255)),
            };
            if (!current)
            {
                var soft = new SoftText(text);
                view.Soft.Add(soft);
                rows.Children.Add(soft.Element);
                continue;
            }
            // As the line's rows: dimmed by a wrapper, the sung part over it (a little under the line's white).
            var dim = new Grid { Opacity = 0.4 };
            dim.Children.Add(text);
            var host = new Grid { IsHitTestVisible = false };
            var row = new Grid { HorizontalAlignment = side };
            row.Children.Add(dim);
            row.Children.Add(host);
            var sweep = new SweepPainter(text, host)
            {
                Color = Windows.UI.Color.FromArgb(217, 255, 255, 255),
                // No glow: that's too much on small text.
                Effect = _settings.LyricsEffect == SungEffect.Line ? SungEffect.Line : SungEffect.Sweep,
            };
            view.ExtraRows.Add((piece, Math.Ceiling(_extraRuler.Measure(piece.Text)) + 1, sweep, font));
            rows.Children.Add(row);
        }
        view.Panel.Children.Add(rows);
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
    /// The line being sung is full size and sharp; the others shrink, and fade and blur more the
    /// further they are (up to 8 points of blur). Unsynced lyrics are all plain. While the user
    /// scrolls the lyrics the others are sharp and a little brighter, to be read.
    /// </summary>
    void ApplyDistance(LineView view)
    {
        bool synced = _model.Lyrics is { IsSynced: true };
        bool current = synced && view.Line.Id == _current;
        int distance = Math.Abs(view.Line.Id - (_current ?? -1));
        view.Panel.Opacity = !synced ? 0.85 : current ? 1 : _browsing ? 0.5 : 0.35 * Math.Max(0.45, 1 - (distance - 1) * 0.12);
        view.Panel.Scale = current || !synced ? Vector3.One : new Vector3(0.96f, 0.96f, 1);
        double blur = !synced || current || _current == null || _browsing || !_settings.LyricsBlurDistant ? 0 : Math.Min(distance * 1.2, 8);
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
        Settled();
        if (old is int o && o < _views.Count)
        {
            // Words that rose as they were sung come back down together first; the line is drawn plain once they have.
            var view = _views[o];
            bool down = false;
            if (animate) foreach (var (_, _, sweep) in view.Rows) down |= sweep.Release();
            RenderLine(view, current: false, settling: down);
            if (down)
            {
                _settling = view;
                _settle.Start();
            }
        }
        if (index is int i) RenderLine(_views[i], current: true);
        foreach (var view in _views) ApplyDistance(view);
        AnimateCurrent();
        // Not while the user is looking around: the window goes back when they've stopped.
        if (!_browsing) ScrollToCurrent(animate);
    }

    /// <summary>The words of the line sung before this one are down (or it's needed otherwise): it's drawn plain.</summary>
    void Settled()
    {
        _settle.Stop();
        var view = _settling;
        _settling = null;
        if (view == null) return;
        int at = _views.IndexOf(view);
        if (at >= 0 && at != _current) RenderLine(view, current: false);
    }

    /// <summary>
    /// The user is scrolling through the lyrics (<paramref name="on"/>), or has stopped: a few
    /// seconds after the last scroll, or on picking a line. Meanwhile every line is sharp and the
    /// window stays where they put it; afterwards the blur comes back and it glides to the line
    /// being sung. Unsynced lyrics have neither.
    /// </summary>
    void Browse(bool on)
    {
        on &= _model.Lyrics is { IsSynced: true };
        _browse.Stop();
        if (on) _browse.Start();
        if (on == _browsing) return;
        _browsing = on;
        foreach (var view in _views) ApplyDistance(view);
        if (!on) ScrollToCurrent(animate: true);
    }

    /// <summary>Puts the sung line 30% of the way down.</summary>
    void ScrollToCurrent(bool animate)
    {
        if (_current is not int target || target >= _views.Count) return;
        Lines.UpdateLayout();
        // Before the window's first layout the viewport has no height yet; Relayout scrolls again.
        double viewport = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight;
        var top = _views[target].Panel.TransformToVisual(Lines).TransformPoint(new Point(0, 0)).Y;
        double to = Math.Max(0, top - viewport * 0.3);
        // Moving on to the next line: the lines below follow one after another.
        _stagger = animate && Math.Abs(to - Scroller.VerticalOffset) > 1;
        Scroller.ScrollTo(0, to,
            new ScrollingScrollOptions(animate ? ScrollingAnimationMode.Enabled : ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
    }

    /// <summary>How long the list takes to glide to the next line.</summary>
    const double Glide = 0.6;

    /// <summary>
    /// Scrolls to the next line with a soft, spring-like ease, not Windows' quick default. Moving
    /// on a line, each line below the one just sung sets off a moment after the one above (like
    /// Apple Music's lyrics), held back by a translation on the compositor that ends at nothing. A
    /// line still catching up from the last move eases the rest of the way instead of jumping.
    /// </summary>
    void OnScrollAnimationStarting(ScrollView sender, ScrollingScrollAnimationStartingEventArgs args)
    {
        if (args.Animation is not Microsoft.UI.Composition.Vector3KeyFrameAnimation stock) return;
        var compositor = stock.Compositor;
        bool stagger = _stagger;
        _stagger = false;
        double from = args.StartPosition.Y, delta = args.EndPosition.Y - from;
        double viewport = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight;
        List<(StackPanel Panel, double Delay)> followers = stagger && delta > 0 && delta < viewport ? Followers() : [];
        if (followers.Count == 0)
        {
            var eased = compositor.CreateVector3KeyFrameAnimation();
            eased.InsertKeyFrame(1, new Vector3((float)args.EndPosition.X, (float)args.EndPosition.Y, 0),
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.25f, 1)));
            eased.Duration = TimeSpan.FromSeconds(Glide);
            args.Animation = eased;
            return;
        }

        // The scroll and every line's lag share the same frames, so they stay in step.
        const int steps = 24;
        double total = Glide + followers.Max(f => f.Delay);
        var linear = compositor.CreateLinearEasingFunction();
        var scroll = compositor.CreateVector3KeyFrameAnimation();
        for (int k = 0; k <= steps; k++)
            scroll.InsertKeyFrame((float)k / steps, new Vector3((float)args.EndPosition.X, (float)(from + delta * Ease(total * k / steps / Glide)), 0), linear);
        scroll.Duration = TimeSpan.FromSeconds(total);
        args.Animation = scroll;
        foreach (var (panel, delay) in followers)
        {
            var lag = compositor.CreateVector3KeyFrameAnimation();
            for (int k = 0; k <= steps; k++)
            {
                double t = total * k / steps;
                // Where this line would be had it set off later, less where the scroll puts it;
                // plus what's left of the last move's lag, fading as the scroll goes.
                double behind = delta * (Ease(t / Glide) - Ease((t - delay) / Glide));
                string left = (1 - Ease(t / Glide)).ToString("0.####", CultureInfo.InvariantCulture);
                lag.InsertExpressionKeyFrame((float)k / steps,
                    $"this.StartingValue * {left} + Vector3(0, {behind.ToString("0.###", CultureInfo.InvariantCulture)}, 0)", linear);
            }
            lag.Duration = TimeSpan.FromSeconds(total);
            lag.Target = "Translation";
            panel.StartAnimation(lag);
        }
    }

    /// <summary>The lines near the one being sung and how long each waits before following.</summary>
    List<(StackPanel Panel, double Delay)> Followers()
    {
        if (_current is not int current) return [];
        var followers = new List<(StackPanel, double)>();
        for (int i = Math.Max(0, current - 4); i < Math.Min(_views.Count, current + 14); i++)
            followers.Add((_views[i].Panel, Math.Clamp(i - current + 1, 0, 9) * 0.04));
        return followers;
    }

    /// <summary>The scroll's ease, cubic-bezier(0.2, 0.9, 0.25, 1), at <paramref name="x"/> of the way through.</summary>
    static double Ease(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        static double Curve(double s, double p1, double p2) => 3 * (1 - s) * (1 - s) * s * p1 + 3 * (1 - s) * s * s * p2 + s * s * s;
        double lo = 0, hi = 1, at = x;
        for (int k = 0; k < 24; k++)
        {
            at = (lo + hi) / 2;
            if (Curve(at, 0.2, 0.25) < x) lo = at; else hi = at;
        }
        return Curve(at, 0.9, 1);
    }

    void AnimateCurrent()
    {
        if (_current is not int i || i >= _views.Count) return;
        double now = _model.Position();
        var font = Fonts.For(_views[i].Line.Text, _model.Lyrics is { } l ? Romanizer.SongLanguage(l) : null);
        var measure = Measure(font);
        foreach (var (piece, width, sweep) in _views[i].Rows)
            sweep.Animate(piece, width, now, _model.IsPlaying, measure);
        foreach (var (piece, width, sweep, extraFont) in _views[i].ExtraRows)
        {
            _extraRuler.Use(extraFont, Math.Round(_fontSize * 0.5), Fonts.SemiboldWeight);
            sweep.Animate(piece, width, now, _model.IsPlaying, _extraRuler.Measure);
        }
    }

    /// <summary>
    /// Where the lyrics are the point (wide windows and full screen, while playing), the controls
    /// fade once the pointer rests, and come back when it moves.
    /// </summary>
    void UpdateControls()
    {
        bool hide = _settings.LyricsAutoHide && _wide && _model.IsPlaying && _pointerIdle;
        TopButtons.Opacity = BigProgressHost.Opacity = BigTransportHost.Opacity = hide ? 0 : 1;
    }
}
