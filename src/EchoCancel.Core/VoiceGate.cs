namespace EchoCancel.Core;

internal struct Biquad
{
    private float _b0, _b1, _b2, _a1, _a2, _z1, _z2;

    public static Biquad HighPass(double fc, double fs, double q = 0.7071)
    {
        double w = 2 * Math.PI * fc / fs, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q), a0 = 1 + al;
        return new Biquad { _b0 = (float)((1 + cs) / 2 / a0), _b1 = (float)(-(1 + cs) / a0), _b2 = (float)((1 + cs) / 2 / a0), _a1 = (float)(-2 * cs / a0), _a2 = (float)((1 - al) / a0) };
    }

    public static Biquad LowPass(double fc, double fs, double q = 0.7071)
    {
        double w = 2 * Math.PI * fc / fs, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q), a0 = 1 + al;
        return new Biquad { _b0 = (float)((1 - cs) / 2 / a0), _b1 = (float)((1 - cs) / a0), _b2 = (float)((1 - cs) / 2 / a0), _a1 = (float)(-2 * cs / a0), _a2 = (float)((1 - al) / a0) };
    }

    public float Step(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        // Denormal sayıları sıfırla (sessizlikte CPU sıçramasını önler)
        if (MathF.Abs(_z1) < 1e-18f) _z1 = 0f;
        if (MathF.Abs(_z2) < 1e-18f) _z2 = 0f;
        return y;
    }
}

/// <summary>Konuşma bandı (yaklaşık 200–3800 Hz) enerjisini ölçer.</summary>
internal sealed class BandMeter
{
    private Biquad _hp = Biquad.HighPass(200, 48000), _lp = Biquad.LowPass(3800, 48000);
    private double _smooth = 1e-10;

    public float Measure(ReadOnlySpan<float> x)
    {
        double s = 0;
        for (int i = 0; i < x.Length; i++) { float y = _lp.Step(_hp.Step(x[i])); s += (double)y * y; }
        if (double.IsNaN(s)) { _hp = Biquad.HighPass(200, 48000); _lp = Biquad.LowPass(3800, 48000); s = 0; }
        _smooth += 0.6 * (s / x.Length - _smooth); // ~20 ms yumuşatma
        return Dsp.EnergyDb(_smooth);
    }
}

public enum GateSensitivity { Low, Medium, High }

/// <summary>
/// Yankı giderimi SONRASI çalışan ses etkinliği kapısı.
///  - Uyarlanır gürültü tabanı + konuşma bandı SNR eşiği (histerezisli)
///  - Hoparlör çalarken ek koruma: AEC sinyalin büyük kısmını sildiyse (yalnızca yankı vardı) kapı açılmaz
///  - İleri bakış (lookahead) gecikmesi: karar, sesin birkaç çerçeve gerisine uygulanır; kelime başları kesilmez
///  - Kapalıyken çıkış tam sayısal sessizliktir (0.0)
/// </summary>
public sealed class VoiceGate
{
    private const int Frame = ApmProcessor.FrameSize;

    public bool Enabled = true;
    public float OpenSnrDb = 10, CloseSnrDb = 5, AbsoluteFloorDb = -58;
    public float EchoGuardDb = 12, RenderExtraDb = 6;
    public int ConfirmFrames = 2, HangoverFrames = 35;
    public float AttackMs = 5, ReleaseMs = 80;
    /// <summary>Başlangıçtan sonra hoparlörün çaldığı ilk N çerçevede (AEC yakınsarken) kapı açılmaz. 0 = kapalı.</summary>
    public int WarmupRenderFrames = 250;
    /// <summary>Referans–mikrofon hizası henüz doğrulanmadı: doğrulanana (ya da hoparlörün çaldığı 8 sn dolana) kadar kapalı kal.</summary>
    public bool AlignmentPending;
    private const int AlignmentTimeoutFrames = 800;

    private readonly BandMeter _outMeter = new(), _micMeter = new();
    private readonly float[] _delay;
    private readonly int _lookaheadFrames;
    private int _delayPos, _consec, _hang, _renderFrames;
    private float _floorDb = -80, _gain;

    public bool IsOpen { get; private set; }
    public float NoiseFloorDb => _floorDb;
    public float OutBandDb { get; private set; } = -100;
    public float RemovedDb { get; private set; }
    public int LookaheadMs => _lookaheadFrames * 10;
    public bool WarmingUp => WarmupRenderFrames > 0 && (_renderFrames < WarmupRenderFrames || (AlignmentPending && _renderFrames < AlignmentTimeoutFrames));

    public VoiceGate(int lookaheadMs)
    {
        _lookaheadFrames = Math.Clamp((lookaheadMs + 5) / 10, 0, 10);
        _delay = new float[Math.Max(1, _lookaheadFrames) * Frame];
    }

    /// <summary>AEC'nin hizası değişti (ör. mikrofon gecikmesi ayarlandı): yeniden yakınsayana kadar kapıyı kapalı tut.</summary>
    public void RestartWarmup(int frames) => _renderFrames = Math.Min(_renderFrames, WarmupRenderFrames - frames);

    public void SetSensitivity(GateSensitivity s)
    {
        (OpenSnrDb, CloseSnrDb, AbsoluteFloorDb) = s switch
        {
            GateSensitivity.Low => (14f, 8f, -50f),
            GateSensitivity.High => (7f, 3f, -64f),
            _ => (10f, 5f, -58f),
        };
    }

    /// <param name="processed">AEC+NS çıkışı (10 ms)</param>
    /// <param name="rawMic">Aynı çerçevenin APM öncesi hali (yankı guard'ı için)</param>
    /// <param name="renderActive">Hoparlörde son ~300 ms içinde ses var mı</param>
    /// <param name="output">Kapıdan geçmiş, lookahead kadar gecikmiş çıkış</param>
    public void Process(ReadOnlySpan<float> processed, ReadOnlySpan<float> rawMic, bool renderActive, Span<float> output)
    {
        float outDb = _outMeter.Measure(processed);
        float micDb = _micMeter.Measure(rawMic);
        OutBandDb = outDb;
        RemovedDb = Math.Max(0, micDb - outDb);

        if (renderActive && _renderFrames < AlignmentTimeoutFrames) _renderFrames++;
        bool echoDominated = renderActive && (RemovedDb > EchoGuardDb || WarmingUp);
        float open = Math.Max(_floorDb + OpenSnrDb + (renderActive ? RenderExtraDb : 0), AbsoluteFloorDb);
        float close = Math.Max(_floorDb + CloseSnrDb, AbsoluteFloorDb - 4);
        bool candidate = outDb > open && !echoDominated;
        bool sustain = outDb > close && !echoDominated;

        _consec = candidate ? _consec + 1 : 0;
        if (_consec >= ConfirmFrames) { IsOpen = true; _hang = HangoverFrames; }
        else if (IsOpen)
        {
            if (sustain) _hang = HangoverFrames;
            else if (--_hang <= 0) IsOpen = false;
        }

        // Gürültü tabanı: aşağı hızlı, yukarı yavaş (kapı açıkken çok daha yavaş)
        if (outDb < _floorDb) _floorDb += 0.3f * (outDb - _floorDb);
        else _floorDb += Math.Min(outDb - _floorDb, IsOpen ? 0.003f : 0.03f);
        _floorDb = Math.Clamp(_floorDb, -95f, -40f);

        float target = !Enabled || IsOpen ? 1f : 0f;
        float up = 1f / (AttackMs * 48f), down = 1f / (ReleaseMs * 48f);

        for (int i = 0; i < Frame; i++)
        {
            float s;
            if (_lookaheadFrames == 0) s = processed[i];
            else { s = _delay[_delayPos]; _delay[_delayPos] = processed[i]; if (++_delayPos == _delay.Length) _delayPos = 0; }

            if (_gain < target) _gain = Math.Min(target, _gain + up);
            else if (_gain > target) _gain = Math.Max(target, _gain - down);
            output[i] = _gain <= 0f ? 0f : s * _gain;
        }
    }
}
