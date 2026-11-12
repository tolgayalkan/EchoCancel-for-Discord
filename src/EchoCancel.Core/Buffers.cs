namespace EchoCancel.Core;

public static class Dsp
{
    public static float RmsDb(ReadOnlySpan<float> x)
    {
        double s = 0;
        for (int i = 0; i < x.Length; i++) s += (double)x[i] * x[i];
        return EnergyDb(s / Math.Max(1, x.Length));
    }

    public static float EnergyDb(double meanSquare) =>
        meanSquare <= 1e-10 ? -100f : (float)(10.0 * Math.Log10(meanSquare));
}

/// <summary>
/// İki farklı donanım saati arasında köprü kuran halka arabellek.
/// Yazan taraf kaynak saatinde, okuyan taraf hedef saatinde çalışır. Okuma sırasında doluluk
/// seviyesini izleyen yavaş bir PI denetleyici yeniden örnekleme oranını ayarlayarak saat kaymasını
/// (drift) telafi eder. Döngü bilerek yavaştır: hızlı oran değişimleri AEC'nin gördüğü gecikmeyi
/// kaydırır ve yankı sızıntısına yol açar. Öğrenilen kayma <see cref="LearnedPpm"/> ile dışarı
/// alınıp bir sonraki başlatmada geri verilebilir. Enterpolasyon: 4 noktalı kübik Hermite.
/// </summary>
public sealed class DriftBuffer
{
    private readonly object _lock = new();
    private readonly float[] _buf;
    private readonly int _mask;
    private readonly double _srcRate, _dstRate, _nominal;
    private readonly double _target, _maxExtra;
    private long _w;
    private double _r = 1;
    private double _setpoint, _fillAvg, _integ, _adj, _settleTime, _settleSum;
    private int _settleCount;
    private bool _buffering = true;
    private int _underruns, _overruns;

    private const double Kp = 0.03, Ki = 0.0002, MaxAdj = 0.005, SmoothSeconds = 4.0, SettleSeconds = 2.0;

    public DriftBuffer(int srcRate, int dstRate, double targetMs, double maxExtraMs = 80, double initialPpm = 0)
    {
        _srcRate = srcRate; _dstRate = dstRate; _nominal = (double)srcRate / dstRate;
        _target = targetMs * srcRate / 1000.0;
        _maxExtra = maxExtraMs * srcRate / 1000.0;
        int cap = 1;
        while (cap < (_target * 1.6 + _maxExtra) * 2 + srcRate / 2) cap <<= 1;
        _buf = new float[cap];
        _mask = cap - 1;
        _setpoint = _fillAvg = _target;
        _integ = _adj = Math.Clamp(initialPpm * 1e-6, -MaxAdj, MaxAdj);
    }

    public int Underruns => Volatile.Read(ref _underruns);
    public int Overruns => Volatile.Read(ref _overruns);
    public double FillMs { get { lock (_lock) return Math.Max(0, _w - _r) * 1000.0 / _srcRate; } }
    public double TargetMs => _target * 1000.0 / _srcRate;
    /// <summary>Şu an uygulanan saat düzeltmesi (milyonda parça).</summary>
    public double CorrectionPpm { get { lock (_lock) return _adj * 1e6; } }
    /// <summary>Denetleyicinin öğrendiği kalıcı saat farkı (integral terimi, ppm).</summary>
    public double LearnedPpm { get { lock (_lock) return _integ * 1e6; } }
    public bool IsBuffering { get { lock (_lock) return _buffering; } }

    public void Write(ReadOnlySpan<float> src)
    {
        lock (_lock)
        {
            for (int i = 0; i < src.Length; i++) _buf[(int)((_w + i) & _mask)] = src[i];
            _w += src.Length;
            if (_w - _r > _setpoint + _maxExtra)
            {
                // Okuyan taraf durmuş ya da çok yavaş: en eski veriyi at, gecikmeyi hedefe çek.
                _r = Math.Max(1, _w - _setpoint);
                _fillAvg = _setpoint;
                if (!_buffering) _overruns++;
            }
        }
    }

    public void Reset()
    {
        lock (_lock) { _r = Math.Max(1, _w); _buffering = true; }
    }

    /// <summary>Hedef saatinde dst.Length örnek üretir. Veri yetmezse sıfır doldurur ve false döner.</summary>
    public bool Read(Span<float> dst)
    {
        lock (_lock)
        {
            if (_buffering)
            {
                if (_w - _r < _target + 3) { dst.Clear(); return false; }
                _r = Math.Max(1, _w - _target); // hedef gecikmeyle (yeniden) başla
                _buffering = false;
                _setpoint = _fillAvg = _target;
                _settleTime = _settleSum = 0; _settleCount = 0;
                _adj = _integ;
            }

            double step = _nominal * (1.0 + _adj);
            if (_w - _r < dst.Length * step + 3)
            {
                dst.Clear();
                _buffering = true;
                _underruns++;
                return false;
            }

            double r = _r;
            for (int n = 0; n < dst.Length; n++)
            {
                long i = (long)r;
                float f = (float)(r - i);
                float y0 = _buf[(int)((i - 1) & _mask)], y1 = _buf[(int)(i & _mask)];
                float y2 = _buf[(int)((i + 1) & _mask)], y3 = _buf[(int)((i + 2) & _mask)];
                float c1 = 0.5f * (y2 - y0);
                float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
                float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
                dst[n] = ((c3 * f + c2) * f + c1) * f + y1;
                r += step;
            }
            _r = r;

            double dt = dst.Length / _dstRate;
            double fill = _w - _r;
            if (_settleTime < SettleSeconds)
            {
                // Başlangıç: oranı oynatma; yalnızca okuma anındaki ortalama doluluğu ölç ve
                // onu çalışma noktası kabul et (paketlerin geliş fazına bağlıdır).
                _settleSum += fill; _settleCount++;
                _settleTime += dt;
                if (_settleTime >= SettleSeconds)
                    _setpoint = _fillAvg = Math.Clamp(_settleSum / _settleCount, _target * 0.6, _target * 1.6);
                return true;
            }
            _fillAvg += Math.Min(1.0, dt / SmoothSeconds) * (fill - _fillAvg);
            double errSec = (_fillAvg - _setpoint) / _srcRate;
            _integ = Math.Clamp(_integ + Ki * errSec * dt, -MaxAdj, MaxAdj);
            _adj = Math.Clamp(Kp * errSec + _integ, -MaxAdj, MaxAdj);
            return true;
        }
    }
}

/// <summary>Sabit oranlı, akış tabanlı kübik yeniden örnekleyici (örn. 16 kHz mikrofon → 48 kHz).</summary>
public sealed class StreamResampler
{
    private readonly double _step;
    private float[] _v = new float[4096];
    private readonly float[] _hist = new float[3];
    private double _p = 1;

    public StreamResampler(int srcRate, int dstRate) { _step = (double)srcRate / dstRate; }
    public StreamResampler(double step) { _step = step; }

    public int MaxOutput(int inputCount) => (int)(inputCount / _step) + 8;

    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int len = input.Length + 3;
        if (_v.Length < len) _v = new float[len * 2];
        _hist.CopyTo(_v, 0);
        input.CopyTo(_v.AsSpan(3));
        int n = 0;
        double p = _p;
        while (true)
        {
            int i = (int)p;
            if (i + 2 >= len || n >= output.Length) break;
            float f = (float)(p - i);
            float y0 = _v[i - 1], y1 = _v[i], y2 = _v[i + 1], y3 = _v[i + 2];
            float c1 = 0.5f * (y2 - y0);
            float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
            float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
            output[n++] = ((c3 * f + c2) * f + c1) * f + y1;
            p += _step;
        }
        _v.AsSpan(len - 3, 3).CopyTo(_hist);
        _p = p - (len - 3);
        return n;
    }
}

public enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }

/// <summary>
/// Aygıt biçimindeki (çok kanallı, tamsayı ya da kayan nokta) baytları mono float'a çevirir.
/// 50 kHz üstü aygıtlarda örtüşmeyi sınırlamak için 2 ya da 4 örneklik ortalama ile ön seyreltme yapar.
/// </summary>
public sealed class MonoConverter
{
    private readonly SampleKind _kind;
    private readonly int _channels, _bytesPerSample, _decim;
    private double _acc; private int _accN;

    public MonoConverter(SampleKind kind, int channels, int sampleRate)
    {
        _kind = kind; _channels = Math.Max(1, channels);
        _bytesPerSample = kind switch { SampleKind.Pcm16 => 2, SampleKind.Pcm24 => 3, _ => 4 };
        _decim = sampleRate > 100000 ? 4 : sampleRate > 50000 ? 2 : 1;
        OutputRate = sampleRate / _decim;
    }

    public int OutputRate { get; }
    public int BytesPerFrame => _bytesPerSample * _channels;
    public int MaxOutput(int byteCount) => byteCount / BytesPerFrame / _decim + 2;

    public int Convert(ReadOnlySpan<byte> data, Span<float> dst)
    {
        int frames = data.Length / BytesPerFrame, n = 0;
        float inv = 1f / _channels;
        for (int f = 0; f < frames; f++)
        {
            int o = f * BytesPerFrame;
            float sum = 0;
            for (int c = 0; c < _channels; c++, o += _bytesPerSample)
            {
                sum += _kind switch
                {
                    SampleKind.Float32 => BitConverter.ToSingle(data.Slice(o, 4)),
                    SampleKind.Pcm16 => BitConverter.ToInt16(data.Slice(o, 2)) / 32768f,
                    SampleKind.Pcm24 => (((sbyte)data[o + 2] << 16) | (data[o + 1] << 8) | data[o]) / 8388608f,
                    _ => BitConverter.ToInt32(data.Slice(o, 4)) / 2147483648f,
                };
            }
            float mono = sum * inv;
            if (float.IsNaN(mono) || float.IsInfinity(mono)) mono = 0;
            if (_decim == 1) { dst[n++] = mono; continue; }
            _acc += mono;
            if (++_accN == _decim) { dst[n++] = (float)(_acc / _decim); _acc = 0; _accN = 0; }
        }
        return n;
    }
}
