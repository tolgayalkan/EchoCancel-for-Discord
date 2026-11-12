namespace EchoCancel.Core;

/// <summary>
/// Referans (hoparlör) ile mikrofon arasındaki gecikmeyi 1 kHz'lik genlik zarflarının
/// çapraz korelasyonuyla kestirir. Pozitif değer: yankı mikrofona referanstan SONRA ulaşıyor (olması gereken).
/// Yalnızca teşhis ve otomatik mikrofon gecikmesi ayarı için kullanılır; hizalamanın kendisini AEC3 yapar.
/// </summary>
public sealed class DelayEstimator
{
    private const int EnvPerFrame = 10, Block = 48;
    public const float MinConfidence = 0.6f;
    private const int N = 3000, Window = 2000, MaxLag = 320, MinLag = -80;
    private readonly float[] _ref = new float[N], _mic = new float[N];
    private readonly float[] _a = new float[Window], _b = new float[N];
    private readonly double[] _pre = new double[N + 1];
    private int _frames, _activeFrames;

    public float LagMs { get; private set; } = float.NaN;
    public float Confidence { get; private set; }
    public int Estimates { get; private set; }

    /// <summary>10 ms'lik çerçeveleri besler. Yeni bir kestirim üretildiyse true döner.</summary>
    public bool Push(ReadOnlySpan<float> reference, ReadOnlySpan<float> mic, bool refActive)
    {
        Array.Copy(_ref, EnvPerFrame, _ref, 0, N - EnvPerFrame);
        Array.Copy(_mic, EnvPerFrame, _mic, 0, N - EnvPerFrame);
        for (int k = 0; k < EnvPerFrame; k++)
        {
            float sr = 0, sm = 0;
            for (int i = k * Block; i < (k + 1) * Block; i++) { sr += Math.Abs(reference[i]); sm += Math.Abs(mic[i]); }
            _ref[N - EnvPerFrame + k] = sr / Block;
            _mic[N - EnvPerFrame + k] = sm / Block;
        }
        if (refActive) _activeFrames++;
        if (++_frames < 200) return false;

        bool enough = _activeFrames >= 80;
        _frames = 0; _activeFrames = 0;
        return enough && Estimate();
    }

    private bool Estimate()
    {
        int start = N - Window - MaxLag; // referans penceresinin başı
        double meanA = 0;
        for (int i = 0; i < Window; i++) meanA += _ref[start + i];
        meanA /= Window;
        double ea = 0;
        for (int i = 0; i < Window; i++) { _a[i] = (float)(_ref[start + i] - meanA); ea += _a[i] * _a[i]; }
        double meanB = 0;
        for (int i = 0; i < N; i++) meanB += _mic[i];
        meanB /= N;
        for (int i = 0; i < N; i++) _b[i] = (float)(_mic[i] - meanB);
        if (ea < 1e-9) return false;

        // Mikrofon zarfının kayan pencere enerjisi için önek toplamları
        _pre[0] = 0;
        for (int i = 0; i < N; i++) _pre[i + 1] = _pre[i] + (double)_b[i] * _b[i];

        double best = 0; int bestLag = 0;
        var a = new ReadOnlySpan<float>(_a, 0, Window);
        for (int lag = MinLag; lag <= MaxLag; lag++)
        {
            int o = start + lag;
            double eb = _pre[o + Window] - _pre[o];
            if (eb < 1e-9) continue;
            double rho = Dot(a, new ReadOnlySpan<float>(_b, o, Window)) / Math.Sqrt(ea * eb);
            if (rho > best) { best = rho; bestLag = lag; }
        }
        Confidence = (float)best;
        if (best < MinConfidence) return false; // ilişkisiz ses (ör. çift konuşma) ölçümlerde 0,6'nın altında kaldı
        LagMs = bestLag;
        Estimates++;
        return true;
    }

    private static float Dot(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        int n = x.Length, w = System.Numerics.Vector<float>.Count, i = 0;
        var acc = System.Numerics.Vector<float>.Zero;
        for (; i <= n - w; i += w) acc += new System.Numerics.Vector<float>(x.Slice(i, w)) * new System.Numerics.Vector<float>(y.Slice(i, w));
        float s = System.Numerics.Vector.Sum(acc);
        for (; i < n; i++) s += x[i] * y[i];
        return s;
    }
}
