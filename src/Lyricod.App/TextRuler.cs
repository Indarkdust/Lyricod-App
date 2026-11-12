using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Lyricod;

/// <summary>
/// Measures text in one font, with a TextBlock that's laid out but invisible (a collapsed one
/// measures as zero). Widths are remembered until the font changes.
/// </summary>
sealed class TextRuler(TextBlock block)
{
    readonly Dictionary<string, double> _widths = [];

    public void Use(FontFamily font, double size, Windows.UI.Text.FontWeight weight)
    {
        if (block.FontFamily == font && block.FontSize == size && block.FontWeight.Weight == weight.Weight) return;
        block.FontFamily = font;
        block.FontSize = size;
        block.FontWeight = weight;
        _widths.Clear();
    }

    /// <summary>Drawn width of <paramref name="text"/> (view pixels).</summary>
    public double Measure(string text)
    {
        if (_widths.TryGetValue(text, out var w)) return w;
        block.Text = text;
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        w = block.DesiredSize.Width;
        if (_widths.Count > 500) _widths.Clear();
        return _widths[text] = w;
    }
}
