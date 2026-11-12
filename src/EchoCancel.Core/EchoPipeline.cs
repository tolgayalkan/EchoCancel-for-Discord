namespace EchoCancel.Core;

public sealed class PipelineOptions
{
    public NoiseSuppression NoiseSuppression { get; set; } = NoiseSuppression.High;
    public bool AutoGain { get; set; }
    public bool GateEnabled { get; set; } = true;
    public GateSensitivity GateSensitivity { get; set; } = GateSensitivity.Medium;
    public int LookaheadMs { get; set; } = 30;
    public int HangoverMs { get; set; } = 350;
    public double EchoGuardDb { get; set; } = 12;
    /// <summary>Mikrofon yoluna eklenen başlangıç gecikmesi (referansın yankıdan önce gelmesini güvenceye alır).</summary>
    public int MicDelayMs { get; set; } = 10;
    public bool AutoMicDelay { get; set; } = true;
    /// <summary>Başlangıçta hoparlör çalarken AEC yakınsayana kadar (≈2,5 s) kapıyı kapalı tut.</summary>
    public bool WarmupMute { get; set; } = true;
    /// <summary>Mikrofon gecikmesi bu aygıtlar için daha önce ölçülmüşse true: hizanın yeniden doğrulanması beklenmez.</summary>
    public bool AlignmentKnown { get; set; }
}

/// <summary>
/// Platformdan bağımsız işlem zinciri (48 kHz mono, 10 ms çerçeve):
/// mikrofon → (gecikme) → WebRTC AEC3 + NS → ses kapısı → çıkış.
/// Tek iş parçacığından çağrılır; istatistik alanları başka iş parçacıklarından okunabilir.
/// </summary>
public sealed class EchoPipeline : IDisposable
{
    public const int Frame = ApmProcessor.FrameSize;
    private const int MaxMicDelayMs = 150;

    private readonly ApmProcessor _apm;
    private readonly VoiceGate _gate;
    private readonly DelayEstimator _estimator = new();
    private readonly PipelineOptions _opt;
    private readonly float[] _micRing = new float[(MaxMicDelayMs + 20) * 48];
    private readonly float[] _micDelayed = new float[Frame], _work = new float[Frame];
    private int _micRingPos, _micDelaySamples, _renderHold;
    private float _prevLag = float.NaN;

    // Teşhis (yaklaşık değerler; kilitlemeden okunur)
    public volatile float MicDb = -100, RefDb = -100, OutDb = -100, RemovedDb, EstimatedLagMs = float.NaN, LagConfidence;
    public volatile float GateFloorDb = -80, OutBandDb = -100;
    public volatile bool GateOpen, RenderActive, WarmingUp;
    public volatile int MicDelayMs, ApmErrors;
    public long Frames;

    public int LookaheadMs => _gate.LookaheadMs;
    /// <summary>İşlem zincirinin kendi eklediği sabit gecikme (APM'in iç süzgeç gecikmesi hariç).</summary>
    public int AlgorithmicDelayMs => MicDelayMs + _gate.LookaheadMs;

    public EchoPipeline(PipelineOptions opt)
    {
        _opt = opt;
        _apm = new ApmProcessor(echoCancellation: true, opt.NoiseSuppression, highPass: true, opt.AutoGain);
        _gate = new VoiceGate(opt.LookaheadMs)
        {
            Enabled = opt.GateEnabled,
            HangoverFrames = Math.Max(5, opt.HangoverMs / 10),
            EchoGuardDb = (float)opt.EchoGuardDb,
            WarmupRenderFrames = opt.WarmupMute ? 250 : 0,
            AlignmentPending = opt.AutoMicDelay && !opt.AlignmentKnown,
        };
        _gate.SetSensitivity(opt.GateSensitivity);
        SetMicDelay(Math.Clamp(opt.MicDelayMs, 0, MaxMicDelayMs));
    }

    private void SetMicDelay(int ms)
    {
        MicDelayMs = ms;
        _micDelaySamples = ms * 48;
    }

    public void Process(ReadOnlySpan<float> mic, ReadOnlySpan<float> reference, Span<float> output)
    {
        if (mic.Length != Frame || reference.Length != Frame || output.Length != Frame)
            throw new ArgumentException("Tüm çerçeveler 480 örnek olmalı.");

        float refDb = Dsp.RmsDb(reference);
        if (refDb > -60) _renderHold = 30; else if (_renderHold > 0) _renderHold--;
        bool renderActive = _renderHold > 0;

        // Mikrofon gecikme hattı
        int len = _micRing.Length;
        for (int i = 0; i < Frame; i++)
        {
            _micRing[_micRingPos] = mic[i];
            int rd = _micRingPos - _micDelaySamples; if (rd < 0) rd += len;
            _micDelayed[i] = _micRing[rd];
            if (++_micRingPos == len) _micRingPos = 0;
        }

        _micDelayed.AsSpan().CopyTo(_work);
        int e1 = _apm.ProcessRender(reference);
        int e2 = _apm.ProcessCapture(_work);
        if (e1 != 0 || e2 != 0) { ApmErrors++; Array.Clear(_work); }

        _gate.Process(_work, _micDelayed, renderActive, output);

        if (_estimator.Push(reference, mic, refDb > -50))
        {
            float lag = _estimator.LagMs;
            EstimatedLagMs = lag;
            // Zarf tabanlı kestirim ±5 ms oynar; art arda iki tutarlı ölçüm olmadan gecikmeye dokunma.
            if (_opt.AutoMicDelay && !float.IsNaN(_prevLag) && Math.Abs(lag - _prevLag) <= 10 && MicDelayMs < MaxMicDelayMs)
            {
                // Etkin öncelik = ham gecikme + mikrofon yoluna eklediğimiz gecikme; en az 10 ms olsun, 20 ms'ye tamamla.
                float effective = Math.Min(lag, _prevLag) + MicDelayMs;
                if (effective < 10)
                {
                    SetMicDelay(Math.Min(MaxMicDelayMs, MicDelayMs + (int)Math.Ceiling((20 - effective) / 5.0) * 5));
                    _gate.RestartWarmup(400); // AEC yeni hizaya otururken 4 sn daha kapalı kal
                }
                _gate.AlignmentPending = false;
            }
            _prevLag = lag;
        }
        LagConfidence = _estimator.Confidence;

        MicDb = Dsp.RmsDb(mic); RefDb = refDb; OutDb = Dsp.RmsDb(output);
        GateFloorDb = _gate.NoiseFloorDb; OutBandDb = _gate.OutBandDb;
        RemovedDb = _gate.RemovedDb; GateOpen = _gate.IsOpen; RenderActive = renderActive; WarmingUp = _gate.WarmingUp;
        Frames++;
    }

    /// <summary>
    /// Ses kapısı ayarlarını zinciri yeniden kurmadan uygular (ses kesilmez).
    /// Gürültü bastırma, otomatik kazanç ve ileri bakış süresi için zincirin yeniden kurulması gerekir.
    /// </summary>
    public void UpdateLive(PipelineOptions o)
    {
        _gate.Enabled = o.GateEnabled;
        _gate.HangoverFrames = Math.Max(5, o.HangoverMs / 10);
        _gate.EchoGuardDb = (float)o.EchoGuardDb;
        _gate.SetSensitivity(o.GateSensitivity);
        _gate.WarmupRenderFrames = o.WarmupMute ? 250 : 0;
        _opt.GateEnabled = o.GateEnabled; _opt.HangoverMs = o.HangoverMs; _opt.EchoGuardDb = o.EchoGuardDb;
        _opt.GateSensitivity = o.GateSensitivity; _opt.WarmupMute = o.WarmupMute; _opt.AutoMicDelay = o.AutoMicDelay;
    }

    public void Dispose() => _apm.Dispose();
}
