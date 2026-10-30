using Lyricod.Core;
using Lyricod.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Lyricod;

public sealed partial class IslandWindow
{
    void CheckPointer()
    {
        if (!_shown || _closed || !Win32.GetCursorPos(out var p)) return;
        double k = _overlay.Scale;
        var pos = AppWindow.Position;
        double width = Pill.ActualWidth * k, height = Pill.ActualHeight * k;
        double left = pos.X + (AppWindow.Size.Width - width) / 2, top = pos.Y;
        bool inside = p.X >= left - 2 && p.X <= left + width + 2 && p.Y >= top && p.Y <= top + height + 2;
        bool near = p.Y <= top + height + 120 && p.X >= left - 160 && p.X <= left + width + 160;
        _pointerTimer.Interval = TimeSpan.FromMilliseconds(near || _hovered ? 33 : 250);
        double now = PlaybackModel.Now;
        if (inside)
        {
            _lastInside = now;
            if (!_hovered)
            {
                // Use the same two-pixel margin as the hover check.
                bool fromBelow = IslandEntry.IsFromBelow(p.X, p.Y, left - 2, top, width + 4, height + 2, _lastOutside);
                SetHovered(true, fromBelow);
            }
        }
        else
        {
            _lastOutside = (p.X, p.Y);
            if (_hovered && now - _lastInside > 0.3) SetHovered(false); // a moment's grace when brushing past
        }
    }

    void SetHovered(bool hovered, bool fromBelow = false)
    {
        if (hovered == _hovered) return;
        _hovered = hovered;
        if (hovered)
            _hoverAction = _settings.IslandHover == IslandHover.ByDirection
                ? (fromBelow ? IslandHover.Hide : IslandHover.Expand) : _settings.IslandHover;
        bool expand = hovered && _hoverAction == IslandHover.Expand;
        // Takes clicks only while expanded; otherwise whatever is underneath stays usable.
        _overlay.SetClickThrough(!expand);
        // "Hide": out of the way while the pointer is over it, so what's underneath can be seen too.
        if (_hoverAction == IslandHover.Hide) Fade(Pill, hovered ? 0 : 1, hovered ? 0.16 : 0.28, delay: hovered ? 0 : 0.12);
        if (expand != _expanded)
        {
            _expanded = expand;
            // Keep the lyric in place; the player has its own cover.
            Fade(ArtFrame, expand ? 0 : 1, expand ? 0.18 : 0.28);
            Fade(Bars, expand ? 0 : 1, expand ? 0.18 : 0.28);
            if (expand)
            {
                Expanded.Visibility = Visibility.Visible;
                _progress.Update();
                _transport.Update();
                if (_model.IsPlaying) _progressTimer.Start();
            }
            else _progressTimer.Stop();
            // As on the Mac: the mini player fades in once the pill has mostly grown, and goes at once on collapse.
            Fade(Expanded, expand ? 1 : 0, expand ? 0.18 : 0.08, () => { if (!_expanded) Expanded.Visibility = Visibility.Collapsed; },
                delay: expand ? 0.12 : 0);
            Resize();
        }
    }
}
