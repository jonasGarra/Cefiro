using Cefiro.Audio;

namespace Cefiro.Core.Tests;

public class AudioTests
{
    [Fact]
    public void Fft_finds_the_frequency_of_a_pure_tone()
    {
        const int n = 1024;
        var re = new double[n];
        var im = new double[n];
        for (var i = 0; i < n; i++)
            re[i] = Math.Sin(2 * Math.PI * 64 * i / n); // 64 ciclos en la ventana → bin 64

        SpectrumAnalyzer.Fft(re, im);

        var magnitudes = Enumerable.Range(0, n / 2).Select(k => Math.Sqrt(re[k] * re[k] + im[k] * im[k])).ToArray();
        Assert.Equal(64, Array.IndexOf(magnitudes, magnitudes.Max()));
    }

    [Fact]
    public void Bass_tone_lights_the_low_bands_and_not_the_high_ones()
    {
        var analyzer = new SpectrumAnalyzer(48000);
        var samples = new float[4800];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(0.8 * Math.Sin(2 * Math.PI * 80 * i / 48000.0)); // 80 Hz

        float[] bands = [];
        for (var frame = 0; frame < 10; frame++)
        {
            analyzer.Push(samples);
            (bands, _) = analyzer.Compute(0.033);
        }

        var low = bands.Take(4).Max();
        var high = bands.Skip(10).Max();
        Assert.True(low > 0.5, $"graves: {low}");
        Assert.True(high < low / 3, $"agudos: {high}, graves: {low}");
    }

    [Fact]
    public void Silence_gives_low_level()
    {
        var analyzer = new SpectrumAnalyzer(48000);
        analyzer.Push(new float[4096]);
        var (_, level) = analyzer.Compute(0.033);
        Assert.True(level < 0.01);
    }
}
