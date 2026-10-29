
namespace Lyricod.Core.Tests;

public class BandAnalyzerTests
{
    static float[] Tone(float frequency, float amplitude = 0.5f, float sampleRate = 48000)
        => Enumerable.Range(0, BandAnalyzer.FftSize)
            .Select(i => amplitude * MathF.Sin(2 * MathF.PI * frequency * i / sampleRate)).ToArray();

    [Theory]
    [InlineData(70, 0)]
    [InlineData(200, 1)]
    [InlineData(600, 2)]
    [InlineData(1000, 3)]
    [InlineData(3500, 4)]
    [InlineData(9000, 5)]
    public void ToneLightsItsBand(float frequency, int band)
    {
        var analyzer = new BandAnalyzer();
        var levels = new float[BandAnalyzer.BandCount];
        analyzer.Analyze(Tone(frequency), levels, out bool hasSound);
        Assert.True(hasSound);
        Assert.Equal(0.65f, levels[band], 5);
        Assert.Equal(band, Array.IndexOf(levels, levels.Max()));
        Assert.All(levels, x => Assert.InRange(x, 0, 1));
    }

    [Fact]
    public void SilenceStaysZero()
    {
        var analyzer = new BandAnalyzer();
        var levels = new float[BandAnalyzer.BandCount];
        for (int i = 0; i < 100; i++)
        {
            analyzer.Analyze(new float[BandAnalyzer.FftSize], levels, out bool hasSound);
            Assert.False(hasSound);
            Assert.All(levels, x => Assert.Equal(0, x));
        }
    }

    [Fact]
    public void RiseAndFallMatchMac()
    {
        var analyzer = new BandAnalyzer();
        var levels = new float[BandAnalyzer.BandCount];
        var tone = Tone(1000);
        analyzer.Analyze(tone, levels, out _);
        Assert.Equal(0.65f, levels[3], 5);
        analyzer.Analyze(tone, levels, out _);
        Assert.Equal(0.8775f, levels[3], 5);
        analyzer.Analyze(new float[BandAnalyzer.FftSize], levels, out bool hasSound);
        Assert.False(hasSound);
        Assert.Equal(0.8775f * 0.75f, levels[3], 5);
    }

    [Fact]
    public void PeakDecaysByPointZeroFourDbPerBlock()
    {
        var analyzer = new BandAnalyzer();
        var levels = new float[BandAnalyzer.BandCount];
        analyzer.Analyze(Tone(1000), levels, out _);
        analyzer.Analyze(Tone(1000, 0.25f), levels, out _);
        float x = (18 - 20 * MathF.Log10(2) + 0.04f) / 18;
        Assert.Equal(0.65f + (x * x - 0.65f) * 0.25f, levels[3], 5);
    }

    [Fact]
    public void RealFftScalingMatchesAccelerateNearSilenceThreshold()
    {
        // A bin-centered Hann-windowed sine has summed power 3/8 * amplitude squared.
        var analyzer = new BandAnalyzer();
        var levels = new float[BandAnalyzer.BandCount];
        analyzer.Analyze(Tone(46 * 48000f / 2048, 0.0006f), levels, out bool hasSound);
        Assert.True(hasSound);
        analyzer.Analyze(Tone(46 * 48000f / 2048, 0.0004f), levels, out hasSound);
        Assert.False(hasSound);
    }

    [Fact]
    public void SampleRateControlsBandAssignment()
    {
        var analyzer = new BandAnalyzer(44100);
        var levels = new float[BandAnalyzer.BandCount];
        analyzer.Analyze(Tone(1000, sampleRate: 44100), levels, out _);
        Assert.Equal(3, Array.IndexOf(levels, levels.Max()));
    }

    [Fact]
    public void InvalidSamplesDoNotPoisonLaterBlocks()
    {
        var analyzer = new BandAnalyzer();
        var levels = new float[BandAnalyzer.BandCount];
        analyzer.Analyze(Enumerable.Repeat(float.NaN, BandAnalyzer.FftSize).ToArray(), levels, out bool hasSound);
        Assert.False(hasSound);
        Assert.All(levels, x => Assert.Equal(0, x));
        analyzer.Analyze(Tone(1000), levels, out hasSound);
        Assert.True(hasSound);
        Assert.Equal(0.65f, levels[3], 5);
    }
}
