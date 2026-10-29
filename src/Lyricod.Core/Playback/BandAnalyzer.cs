namespace Lyricod.Core;

/// <summary>Keeps the island's frequency bands consistent across platforms. Use on one audio thread.</summary>
public sealed class BandAnalyzer
{
    public const int BandCount = 6;
    public const int FftSize = 2048;
    static readonly float[] Edges = [40, 120, 350, 900, 2200, 5500, 14000];
    readonly float[] _window = new float[FftSize];
    readonly float[] _real = new float[FftSize];
    readonly float[] _imaginary = new float[FftSize];
    readonly float[] _peaks = Enumerable.Repeat(-40f, BandCount).ToArray();
    readonly float[] _levels = new float[BandCount];
    readonly float _sampleRate;

    public BandAnalyzer(float sampleRate = 48000)
    {
        if (!float.IsFinite(sampleRate) || sampleRate < 28000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        for (int i = 0; i < FftSize; i++)
            _window[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / FftSize);
    }

    /// <summary>Analyzes one mono block; silence still advances the meter's decay.</summary>
    public void Analyze(ReadOnlySpan<float> samples, Span<float> levels, out bool hasSound)
    {
        if (samples.Length != FftSize || levels.Length < BandCount)
            throw new ArgumentException("Expected 2048 samples and space for six levels.");
        Array.Clear(_imaginary);
        for (int i = 0; i < FftSize; i++)
            _real[i] = (float.IsFinite(samples[i]) ? samples[i] : 0) * _window[i];
        for (int i = 1, j = 0; i < FftSize; i++)
        {
            int bit = FftSize >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (_real[i], _real[j]) = (_real[j], _real[i]);
        }
        for (int width = 2; width <= FftSize; width <<= 1)
        {
            for (int start = 0; start < FftSize; start += width)
            {
                for (int k = 0; k < width / 2; k++)
                {
                    float angle = -2 * MathF.PI * k / width;
                    float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
                    int a = start + k, b = a + width / 2;
                    float re = cos * _real[b] - sin * _imaginary[b];
                    float im = sin * _real[b] + cos * _imaginary[b];
                    _real[b] = _real[a] - re;
                    _imaginary[b] = _imaginary[a] - im;
                    _real[a] += re;
                    _imaginary[a] += im;
                }
            }
        }
        hasSound = false;
        for (int band = 0; band < BandCount; band++)
        {
            int lo = Math.Max(1, (int)(Edges[band] * FftSize / _sampleRate));
            int hi = Math.Min(FftSize / 2 - 1, Math.Max(lo + 1, (int)(Edges[band + 1] * FftSize / _sampleRate)));
            float power = 0;
            for (int i = lo; i < hi; i++)
                power += _real[i] * _real[i] + _imaginary[i] * _imaginary[i];
            // Accelerate's real FFT returns twice the conventional DFT amplitude.
            power *= 4f / (FftSize * FftSize);
            float db = 10 * MathF.Log10(Math.Max(power, 1e-12f));
            _peaks[band] = Math.Max(db, _peaks[band] - 0.04f);
            float x = Math.Clamp((db - (_peaks[band] - 18)) / 18, 0, 1);
            float target = db < -70 ? 0 : x * x;
            hasSound |= db >= -70;
            _levels[band] += (target - _levels[band]) * (target > _levels[band] ? 0.65f : 0.25f);
            levels[band] = _levels[band];
        }
    }
}
