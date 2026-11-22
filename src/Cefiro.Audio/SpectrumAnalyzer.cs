namespace Cefiro.Audio;

/// <summary>
/// Convierte muestras de audio en 16 bandas logarítmicas (40 Hz a 16 kHz) normalizadas de 0 a 1,
/// con control automático de ganancia y suavizado (subida rápida, bajada lenta).
/// Sin dependencias: FFT radix-2 propia.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int BandCount = 16;
    public const int FftSize = 1024;

    private readonly float[] _window = new float[FftSize];
    private readonly float[] _ring = new float[FftSize * 2];
    private readonly double[] _re = new double[FftSize];
    private readonly double[] _im = new double[FftSize];
    private readonly float[] _bands = new float[BandCount];
    private readonly (int Low, int High)[] _bins = new (int, int)[BandCount];
    private int _ringPos;
    private int _filled;
    private double _peak = 1e-3;
    private float _level;

    public SpectrumAnalyzer(int sampleRate)
    {
        SampleRate = sampleRate;
        for (var i = 0; i < FftSize; i++)
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1))); // Hann

        const double minHz = 40, maxHz = 16000;
        var hzPerBin = (double)sampleRate / FftSize;
        for (var b = 0; b < BandCount; b++)
        {
            var lowHz = minHz * Math.Pow(maxHz / minHz, (double)b / BandCount);
            var highHz = minHz * Math.Pow(maxHz / minHz, (double)(b + 1) / BandCount);
            var low = Math.Max(1, (int)Math.Floor(lowHz / hzPerBin));
            var high = Math.Max(low, Math.Min(FftSize / 2 - 1, (int)Math.Ceiling(highHz / hzPerBin)));
            _bins[b] = (low, high);
        }
    }

    public int SampleRate { get; }

    /// <summary>Añade muestras mono (−1 a 1).</summary>
    public void Push(ReadOnlySpan<float> mono)
    {
        foreach (var s in mono)
        {
            _ring[_ringPos] = s;
            _ringPos = (_ringPos + 1) % _ring.Length;
        }

        _filled = Math.Min(_ring.Length, _filled + mono.Length);
    }

    /// <summary>Calcula el espectro con las últimas 1024 muestras. Devuelve las bandas (0-1) y el nivel global.</summary>
    public (float[] Bands, float Level) Compute(double dtSeconds)
    {
        if (_filled < FftSize)
            return ((float[])_bands.Clone(), _level);

        double energy = 0;
        for (var i = 0; i < FftSize; i++)
        {
            var idx = (_ringPos - FftSize + i + _ring.Length) % _ring.Length;
            var sample = _ring[idx];
            energy += sample * sample;
            _re[i] = sample * _window[i];
            _im[i] = 0;
        }

        Fft(_re, _im);

        var raw = new double[BandCount];
        for (var b = 0; b < BandCount; b++)
        {
            var (low, high) = _bins[b];
            double sum = 0;
            for (var k = low; k <= high; k++)
                sum += Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
            // Compensación ligera: los agudos tienen menos energía.
            raw[b] = sum / (high - low + 1) * (1 + b * 0.12);
        }

        // Control automático de ganancia: el pico baja despacio para adaptarse al volumen.
        var framePeak = raw.Max();
        _peak = Math.Max(framePeak, _peak * Math.Exp(-dtSeconds / 4.0));
        _peak = Math.Max(_peak, 1e-3);

        var attack = Math.Min(1, dtSeconds * 25);
        var release = Math.Min(1, dtSeconds * 6);
        for (var b = 0; b < BandCount; b++)
        {
            var target = (float)Math.Clamp(Math.Pow(raw[b] / _peak, 0.6), 0, 1);
            var k = target > _bands[b] ? attack : release;
            _bands[b] += (float)((target - _bands[b]) * k);
        }

        var rms = Math.Sqrt(energy / FftSize);
        var levelTarget = (float)Math.Clamp(rms * 4, 0, 1);
        _level += (levelTarget - _level) * (float)(levelTarget > _level ? attack : release);

        return ((float[])_bands.Clone(), _level);
    }

    /// <summary>FFT compleja in situ (Cooley-Tukey iterativa). La longitud debe ser potencia de 2.</summary>
    internal static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wRe = Math.Cos(angle);
            var wIm = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var aRe = re[i + k];
                    var aIm = im[i + k];
                    var bRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm;
                    var bIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe;
                    re[i + k] = aRe + bRe;
                    im[i + k] = aIm + bIm;
                    re[i + k + len / 2] = aRe - bRe;
                    im[i + k + len / 2] = aIm - bIm;
                    var next = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = next;
                }
            }
        }
    }
}
