using Cefiro.Ipc.Contracts;

namespace Cefiro.Core.Sensors;

/// <summary>
/// Historial de todos los sensores:
/// - nivel fino: 1 muestra por segundo durante 10 minutos (también da los últimos 60 s);
/// - nivel grueso: la media de cada 5 s durante 1 hora.
/// Memoria fija: (600 + 720) × nº de sensores × 4 bytes.
/// </summary>
public sealed class SensorHistory
{
    public const int FineCapacity = 600;
    public const int FineIntervalMs = 1000;
    public const int CoarseFactor = 5;
    public const int CoarseCapacity = 720;
    public const int CoarseIntervalMs = FineIntervalMs * CoarseFactor;

    private readonly object _gate = new();
    private readonly int _sensorCount;
    private readonly float[] _fine;
    private readonly float[] _coarse;
    private readonly float[] _accumulator;
    private readonly int[] _accumulatorCount;
    private int _fineHead;
    private int _fineCount;
    private int _coarseHead;
    private int _coarseCount;
    private int _samplesInBucket;
    private long _lastTimestamp;

    public SensorHistory(int sensorCount)
    {
        _sensorCount = sensorCount;
        _fine = new float[FineCapacity * sensorCount];
        _coarse = new float[CoarseCapacity * sensorCount];
        _accumulator = new float[sensorCount];
        _accumulatorCount = new int[sensorCount];
        Array.Fill(_fine, float.NaN);
        Array.Fill(_coarse, float.NaN);
    }

    public int SensorCount => _sensorCount;

    /// <summary>Añade una muestra (se espera una por segundo).</summary>
    public void Add(long timestamp, ReadOnlySpan<float?> values)
    {
        if (values.Length != _sensorCount)
            throw new ArgumentException("El número de valores no coincide con el de sensores.", nameof(values));

        lock (_gate)
        {
            var row = _fineHead * _sensorCount;
            for (var i = 0; i < _sensorCount; i++)
            {
                var v = values[i];
                var value = v is { } f && float.IsFinite(f) ? f : float.NaN;
                _fine[row + i] = value;

                if (!float.IsNaN(value))
                {
                    _accumulator[i] += value;
                    _accumulatorCount[i]++;
                }
            }

            _fineHead = (_fineHead + 1) % FineCapacity;
            _fineCount = Math.Min(_fineCount + 1, FineCapacity);
            _samplesInBucket++;
            _lastTimestamp = timestamp;

            if (_samplesInBucket >= CoarseFactor)
            {
                var coarseRow = _coarseHead * _sensorCount;
                for (var i = 0; i < _sensorCount; i++)
                {
                    _coarse[coarseRow + i] = _accumulatorCount[i] > 0 ? _accumulator[i] / _accumulatorCount[i] : float.NaN;
                    _accumulator[i] = 0;
                    _accumulatorCount[i] = 0;
                }

                _coarseHead = (_coarseHead + 1) % CoarseCapacity;
                _coarseCount = Math.Min(_coarseCount + 1, CoarseCapacity);
                _samplesInBucket = 0;
            }
        }
    }

    public long LastTimestamp
    {
        get { lock (_gate) return _lastTimestamp; }
    }

    /// <summary>
    /// Serie de un sensor, del más antiguo al más reciente. Siempre devuelve el tamaño completo del
    /// rango (60, 600 o 720 puntos), con null donde aún no hay datos.
    /// </summary>
    public float?[] GetSeries(int sensorIndex, HistoryRange range, out int intervalMs)
    {
        if ((uint)sensorIndex >= (uint)_sensorCount)
            throw new ArgumentOutOfRangeException(nameof(sensorIndex));

        lock (_gate)
        {
            return range switch
            {
                HistoryRange.Last60Seconds => Extract(_fine, FineCapacity, _fineHead, _fineCount, 60, sensorIndex, FineIntervalMs, out intervalMs),
                HistoryRange.Last10Minutes => Extract(_fine, FineCapacity, _fineHead, _fineCount, FineCapacity, sensorIndex, FineIntervalMs, out intervalMs),
                HistoryRange.LastHour => Extract(_coarse, CoarseCapacity, _coarseHead, _coarseCount, CoarseCapacity, sensorIndex, CoarseIntervalMs, out intervalMs),
                _ => throw new ArgumentOutOfRangeException(nameof(range)),
            };
        }
    }

    private float?[] Extract(float[] buffer, int capacity, int head, int count, int points, int sensorIndex, int interval, out int intervalMs)
    {
        intervalMs = interval;
        var result = new float?[points];
        var available = Math.Min(count, points);

        // La muestra más reciente está en head-1; se rellena desde el final del resultado hacia atrás.
        for (var k = 0; k < available; k++)
        {
            var slot = ((head - 1 - k) % capacity + capacity) % capacity;
            var value = buffer[slot * _sensorCount + sensorIndex];
            result[points - 1 - k] = float.IsNaN(value) ? null : value;
        }

        return result;
    }
}
