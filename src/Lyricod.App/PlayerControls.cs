using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Lyricod;

/// <summary>Previous, play/pause and next for the player Lyricod follows.</summary>
sealed partial class TransportButtons : StackPanel
{
    readonly PlaybackModel _model;
    readonly FontIcon _playIcon;

    public TransportButtons(PlaybackModel model, double size = 16)
    {
        _model = model;
        Orientation = Orientation.Horizontal;
        Spacing = 4;
        Children.Add(Button("\uE892", size * 0.8, L.Get("Previous"), () => _ = _model.Previous())); // Previous
        _playIcon = new FontIcon { Glyph = "\uE768", FontSize = size };
        var play = Button(_playIcon, L.Get("PlayPause"), () => _ = _model.PlayPause());
        Children.Add(play);
        Children.Add(Button("\uE893", size * 0.8, L.Get("Next"), () => _ = _model.Next())); // Next
        Update();
    }

    /// <summary>Follows play/pause.</summary>
    public void Update() => _playIcon.Glyph = _model.IsPlaying ? "\uE769" : "\uE768"; // Pause / Play

    /// <summary>Sizes the island's controls through layout, keeping the icons crisp.</summary>
    public void SetLayoutScale(double scale, double size)
    {
        Spacing = 4 * scale;
        foreach (var button in Children.OfType<Button>())
        {
            button.Padding = new Thickness(10 * scale, 8 * scale, 10 * scale, 8 * scale);
            button.CornerRadius = new CornerRadius(8 * scale);
            if (button.Content is FontIcon icon) icon.FontSize = size * scale * (icon == _playIcon ? 1 : 0.8);
        }
    }

    static Button Button(string glyph, double size, string tip, Action action) =>
        Button(new FontIcon { Glyph = glyph, FontSize = size }, tip, action);

    static Button Button(FontIcon icon, string tip, Action action)
    {
        var button = new Button
        {
            Content = icon, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8),
        };
        button.Click += (_, _) => action();
        ToolTipService.SetToolTip(button, tip);
        return button;
    }
}

/// <summary>
/// Elapsed time, a thin bar, and the time left. Its owner calls <see cref="Update"/> on a slow timer
/// (twice a second, only while it's shown) and when the clock jumps.
/// </summary>
sealed partial class ProgressLine : Grid
{
    readonly PlaybackModel _model;
    readonly ProgressBar _bar;
    readonly TextBlock _elapsed, _left;
    /// <summary>While dragging: where the pointer is (0–1), shown instead of the playhead.</summary>
    double? _scrub;
    bool? _seekable;
    static readonly InputCursor Hand = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    public ProgressLine(PlaybackModel model)
    {
        _model = model;
        // As on the Mac: press or drag on the bar to pick a point, let go to move the player there.
        Background = new SolidColorBrush(Colors.Transparent); // so the whole strip takes the pointer
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => { if (_scrub != null) { _scrub = null; Update(); } };
        RowSpacing = 4;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, CornerRadius = new CornerRadius(2) };
        Children.Add(_bar);
        _elapsed = new TextBlock { FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Left };
        _left = new TextBlock { FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Right };
        SetRow(_elapsed, 1);
        SetRow(_left, 1);
        Children.Add(_elapsed);
        Children.Add(_left);
        Update();
    }

    public Brush BarBrush { set => _bar.Foreground = value; }

    public void SetLayoutScale(double scale)
    {
        RowSpacing = 4 * scale;
        _bar.Height = 4 * scale;
        _bar.CornerRadius = new CornerRadius(2 * scale);
        _elapsed.FontSize = _left.FontSize = 11 * scale;
    }

    bool CanSeek => _model.CanSeek && _model.Duration > 0;

    void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (!CanSeek) return;
            var p = e.GetCurrentPoint(_bar).Position;
            if (p.Y < -10 || p.Y > _bar.ActualHeight + 10) return; // the bar, not the times under it
            if (!CapturePointer(e.Pointer)) return;
            Scrub(p.X);
            e.Handled = true;
        }
        catch (Exception) { /* a pointer that went away */ }
    }

    void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_scrub == null) return;
        try { Scrub(e.GetCurrentPoint(_bar).Position.X); e.Handled = true; }
        catch (Exception) { }
    }

    void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_scrub is not double at) return;
        try
        {
            _scrub = null;
            ReleasePointerCapture(e.Pointer);
            _model.SeekPlayer(at * _model.Duration);
            Update();
            e.Handled = true;
        }
        catch (Exception) { }
    }

    void Scrub(double x)
    {
        _scrub = Math.Clamp(x / Math.Max(_bar.ActualWidth, 1), 0, 1);
        Update();
    }

    public void Update()
    {
        double duration = _model.Duration;
        double position = _scrub is double at ? at * duration : _model.PlayerPosition;
        Visibility = duration > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool seekable = CanSeek;
        if (seekable != _seekable)
        {
            _seekable = seekable;
            ProtectedCursor = seekable ? Hand : null;
            ToolTipService.SetToolTip(this, seekable ? null : L.Get("CantSeek"));
        }
        if (duration <= 0) return;
        _bar.Value = Math.Clamp(position / duration, 0, 1);
        _elapsed.Text = Time(position);
        _left.Text = "-" + Time(duration - position);
    }

    static string Time(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(seconds, 0));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
