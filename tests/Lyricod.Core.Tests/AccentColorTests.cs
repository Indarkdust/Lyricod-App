namespace Lyricod.Core.Tests;

public class AccentColorTests
{
    static (byte R, byte G, byte B) Rgb(string hex)
    {
        uint v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    static string Hex((byte R, byte G, byte B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Degrees round the colour wheel.</summary>
    static double Hue((byte R, byte G, byte B) c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B)), d = max - Math.Min(c.R, Math.Min(c.G, c.B));
        double h = max == c.R ? (c.G - c.B) / d % 6 : max == c.G ? (c.B - c.R) / d + 2 : (c.R - c.G) / d + 4;
        return (h * 60 + 360) % 360;
    }

    [Theory]
    [InlineData("#4968F2", "#BFCAFA")] // a blue-violet cover's colour
    [InlineData("#FF5A5F", "#FFBABC")] // red
    [InlineData("#1B2A6B", "#C8CBDB")] // a navy picked by hand
    [InlineData("#000000", "#CCCCCC")] // black: a grey of that luminance
    public void ADarkColourIsLiftedToTheTextLuminance(string colour, string expected)
    {
        var lifted = AccentColor.Lifted(Rgb(colour), AccentColor.TextLuminance);
        Assert.Equal(expected, Hex(lifted));
        // Just far enough: there, and no lighter than one step of a channel takes it.
        Assert.InRange(AccentColor.Luminance(lifted), 0.6, 0.61);
    }

    [Theory]
    [InlineData("#8CD9FF")]
    [InlineData("#FFD84D")]
    [InlineData("#FFFFFF")]
    public void AColourLightEnoughIsKept(string colour) =>
        Assert.Equal(colour, Hex(AccentColor.Lifted(Rgb(colour), AccentColor.TextLuminance)));

    [Theory]
    [InlineData("#4968F2")]
    [InlineData("#FF5A5F")]
    [InlineData("#1B2A6B")]
    [InlineData("#2E8B57")]
    public void ALiftedColourKeepsItsHue(string colour)
    {
        var lifted = AccentColor.Lifted(Rgb(colour), AccentColor.TextLuminance);
        // Within what rounding to whole channels moves a pale colour's hue.
        Assert.InRange(Math.Abs(Hue(lifted) - Hue(Rgb(colour))), 0, 3);
    }

    [Fact]
    public void TheLuminanceAskedForIsTheOneReached()
    {
        Assert.Equal("#778FF5", Hex(AccentColor.Lifted(Rgb("#4968F2"), 0.3)));
        Assert.Equal("#4DA3FF", Hex(AccentColor.Lifted(Rgb("#4DA3FF"), 0.3)));
        Assert.Equal("#A3D0FF", Hex(AccentColor.Lifted(Rgb("#4DA3FF"), 0.6)));
    }

    [Fact]
    public void LuminanceRunsFromBlackToWhite()
    {
        Assert.Equal(0, AccentColor.Luminance((0, 0, 0)));
        Assert.Equal(1, AccentColor.Luminance((255, 255, 255)), 9);
        Assert.Equal(0.2126, AccentColor.Luminance((255, 0, 0)), 9);
        Assert.Equal(0.6242, AccentColor.Luminance(Rgb("#8CD9FF")), 4);
    }
}
