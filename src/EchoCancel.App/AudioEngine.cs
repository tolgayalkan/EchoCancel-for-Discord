using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using EchoCancel.Core;

namespace EchoCancel.App;

public enum EngineState { Off, Starting, Running, Waiting }

public sealed record EngineSnapshot(
    EngineState State, string Status, string Mic, string Speaker, string Output,
    float MicDb, float RefDb, float OutDb, bool GateOpen, bool RenderActive, bool WarmingUp, float RemovedDb,
    float LagMs, int MicDelayMs, int LookaheadMs, double RefFillMs, double OutFillMs,
    int RefUnderruns, int OutUnderruns, double RefPpm, double OutPpm,
    double ProcAvgUs, double ProcMaxUs, long Frames, int ApmErrors, string MicFormat, string SpeakerFormat, string OutputFormat);

/// <summary>Sanal kablo olarak tanınan aygıt adları.</summary>
public static class DeviceNames
{
    public static bool IsVirtualCable(string name) =>
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) && name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
        || name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
        || name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)
        || name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Virtual Cable", StringComparison.OrdinalIgnoreCase);
}

public sealed record AudioDevice(string Id, string Name, bool Virtual);

public sealed record DeviceSnapshot(AudioDevice[] Captures, AudioDevice[] Renders, string? DefaultCaptureId, string? DefaultRenderId)
{
    public static readonly DeviceSnapshot Empty = new([], [], null, null);
    public bool HasCable => Renders.Any(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
    public AudioDevice? Find(string? id) => id == null ? null : Captures.FirstOrDefault(d => d.Id == id) ?? Renders.FirstOrDefault(d => d.Id == id);
}

/// <summary>WASAPI loopback yakalama; NAudio'nunkinden farkı: kısa arabellek ve olay güdümlü çalışabilmesi.</summary>
internal sealed class LoopbackCapture : WasapiCapture
{
    public LoopbackCapture(MMDevice device, bool eventSync) : base(device, eventSync, 20) { }
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
        AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
}

/// <summary>Sanal kabloya yazılan akış: işlenmiş mono sesi kablonun saatine ve kanal sayısına uyarlar.</summary>
internal sealed class CableProvider : IWaveProvider
{
    private readonly DriftBuffer _buf;
    private readonly int _channels;
    private float[] _mono = new float[4800];
    [ThreadStatic] private static bool t_mmcss;

    public CableProvider(DriftBuffer buf, int sampleRate, int channels)
    {
        _buf = buf; _channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (!t_mmcss) { Native.EnterProAudio(); t_mmcss = true; }
        int frames = count / (4 * _channels);
        if (_mono.Length < frames) _mono = new float[frames];
        var m = _mono.AsSpan(0, frames);
        _buf.Read(m); // veri yoksa sıfır (sessizlik) doldurur
        var dst = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, frames * 4 * _channels));
        for (int i = 0, o = 0; i < frames; i++)
        {
            float v = m[i];
            if (!(v >= -1f)) v = float.IsNaN(v) ? 0f : -1f; else if (v > 1f) v = 1f;
            for (int c = 0; c < _channels; c++) dst[o++] = v;
        }
        int used = frames * 4 * _channels;
        if (used < count) Array.Clear(buffer, offset + used, count - used);
        return count; // 0 dönmek oynatmayı durdurur; her zaman tam doldur
    }
}

internal sealed class WaitException(string message) : Exception(message);

/// <summary>
/// Aygıtları açar, akışları birbirine bağlar ve kopmalarda yeniden kurar.
/// Ham mikrofon sesi hiçbir koşulda sanal kabloya yazılmaz: kabloya giden tek yol işlem zincirinin çıkışıdır;
/// zincir durduğunda kabloya sessizlik gider.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int Frame = EchoPipeline.Frame;

    private readonly AppSettings _s;
    private readonly Thread _supervisor;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _enabled, _restart, _quit;
    private volatile EngineState _state = EngineState.Off;
    private volatile string _status = "";
    private string _lastLogged = "";

    private MMDeviceEnumerator? _enum;
    private NotificationClient? _notify;

    // Etkin ses grafiği (yalnızca denetçi iş parçacığı kurar/yıkar)
    private WasapiCapture? _mic;
    private LoopbackCapture? _loop;
    private WasapiOut? _out;
    private volatile EchoPipeline? _pipe;
    private readonly object _pipeLock = new();
    private volatile bool _devicesDirty = true, _checkFollow;
    private DriftBuffer? _refBuf, _outBuf;
    private MonoConverter? _micConv, _loopConv;
    private StreamResampler? _micRes;
    private float[] _micTmp = new float[9600], _micTmp2 = new float[9600], _loopTmp = new float[9600];
    private readonly float[] _micFrame = new float[Frame], _refFrame = new float[Frame], _outFrame = new float[Frame];
    private int _micFill;
    private volatile string? _micId, _spkId, _outId;
    private string _micName = "-", _spkName = "-", _outName = "-", _micFmt = "", _spkFmt = "", _outFmt = "", _learnKey = "";
    private bool _timerPeriod, _forcePolling, _mic48kFailed;
    private long _frames;
    private double _procAvgUs, _procMaxUs;
    [ThreadStatic] private static bool t_mmcss;

    public event Action? StateChanged;
    /// <summary>Aygıt listesi yenilendi (denetçi iş parçacığından çağrılır).</summary>
    public event Action? DevicesChanged;
    /// <summary>Son bilinen aygıt listesi. Arayüz yalnızca bunu okur; hiçbir zaman ses API'sine gitmez.</summary>
    public DeviceSnapshot Devices { get; private set; } = DeviceSnapshot.Empty;
    public string? ActiveMicId => _micId;
    public string? ActiveSpeakerId => _spkId;
    public string? ActiveOutputId => _outId;

    public AudioEngine(AppSettings settings)
    {
        _s = settings;
        _enabled = settings.Enabled;
        _supervisor = new Thread(Supervise) { IsBackground = true, Name = "EchoCancel.Denetci" };
        _supervisor.SetApartmentState(ApartmentState.MTA);
        _supervisor.Start();
    }

    /// <summary>Sanal kablo (VB-CABLE) sistemde bulunamadı.</summary>
    public volatile bool CableMissing;
    public EngineState State => _state;
    public string Status => _status;

    public void SetEnabled(bool on) { _enabled = on; _restart = true; _wake.Set(); }
    public void Restart() { _restart = true; _wake.Set(); }
    public void RefreshDevicesSoon() { _devicesDirty = true; _wake.Set(); }

    /// <summary>Ses kapısı ayarlarını anında uygular; ses akışı kesilmez.</summary>
    public void ApplyLive()
    {
        var opt = _s.ToPipelineOptions();
        lock (_pipeLock) _pipe?.UpdateLive(opt);
    }

    /// <summary>
    /// Yalnızca işlem zincirini (AEC/NS) yeniden kurar; aygıtlar açık kalır.
    /// Gürültü bastırma, otomatik kazanç ve kelime başı payı değişince kullanılır.
    /// </summary>
    public void RebuildPipeline()
    {
        var old = _pipe;
        if (old == null) return;
        var opt = _s.ToPipelineOptions();
        if (_s.AutoMicDelay) { opt.MicDelayMs = Math.Max(opt.MicDelayMs, old.MicDelayMs); opt.AlignmentKnown = true; }
        EchoPipeline fresh;
        try { fresh = new EchoPipeline(opt); }
        catch (Exception ex) { Log.Write("Pipeline rebuild failed: " + ex.Message); Restart(); return; }
        lock (_pipeLock)
        {
            if (!ReferenceEquals(_pipe, old)) { fresh.Dispose(); return; }
            _pipe = fresh;
        }
        old.Dispose();
    }

    public EngineSnapshot Snapshot()
    {
        var p = _pipe; var rb = _refBuf; var ob = _outBuf;
        double max = _procMaxUs;
        return new EngineSnapshot(_state, _status, _micName, _spkName, _outName,
            p?.MicDb ?? -100, p?.RefDb ?? -100, p?.OutDb ?? -100, p?.GateOpen ?? false, p?.RenderActive ?? false, p?.WarmingUp ?? false,
            p?.RemovedDb ?? 0, p?.EstimatedLagMs ?? float.NaN, p?.MicDelayMs ?? 0, p?.LookaheadMs ?? 0,
            rb?.FillMs ?? 0, ob?.FillMs ?? 0, rb?.Underruns ?? 0, ob?.Underruns ?? 0, rb?.CorrectionPpm ?? 0, ob?.CorrectionPpm ?? 0,
            _procAvgUs, max, Interlocked.Read(ref _frames), p?.ApmErrors ?? 0, _micFmt, _spkFmt, _outFmt);
    }

    // ------------------------------------------------------------------ denetçi
    private void Supervise()
    {
        try
        {
            _enum = new MMDeviceEnumerator();
            _notify = new NotificationClient(this);
            _enum.RegisterEndpointNotificationCallback(_notify);
        }
        catch (Exception ex) { Log.Write("Device notifications unavailable: " + ex.Message); }

        long lastFrames = -1; var lastProgress = Stopwatch.StartNew();
        _wake.Set();
        while (!_quit)
        {
            bool signaled = _wake.WaitOne(2000);
            if (_quit) break;
            if (signaled && (_restart || _devicesDirty)) { Thread.Sleep(250); _wake.Reset(); } // art arda gelen bildirimleri tek seferde ele al
            try
            {
                if (_devicesDirty) RefreshDevices();
                if (_checkFollow)
                {
                    _checkFollow = false;
                    if (_pipe != null && ((_s.MicId == null && PickDevice(null, DataFlow.Capture)?.ID is string m && m != _micId)
                                       || (_s.SpeakerId == null && PickDevice(null, DataFlow.Render)?.ID is string k && k != _spkId)))
                    {
                        Log.Write("Default device changed; switching.");
                        _restart = true;
                    }
                }
                if (!_enabled)
                {
                    if (_pipe != null || _state != EngineState.Off) { Teardown(); SetState(EngineState.Off, L.T("Kapalı — Discord'a sessizlik gidiyor", "Off — Discord receives silence")); }
                    _restart = false;
                    continue;
                }
                if (_restart || _pipe == null)
                {
                    _restart = false;
                    Teardown();
                    if (_state != EngineState.Waiting) SetState(EngineState.Starting, L.T("Başlatılıyor…", "Starting…"));
                    Build();
                    lastFrames = -1; lastProgress.Restart();
                    SetState(EngineState.Running, L.T("Çalışıyor", "Running"));
                }
                else
                {
                    long f = Interlocked.Read(ref _frames);
                    if (f != lastFrames) { lastFrames = f; lastProgress.Restart(); }
                    else if (lastProgress.ElapsedMilliseconds > 3000)
                    {
                        Log.Write("No microphone data for 3 s; rebuilding audio graph.");
                        _restart = true; _wake.Set();
                    }
                }
            }
            catch (WaitException ex) { Teardown(); SetState(EngineState.Waiting, ex.Message); }
            catch (Exception ex)
            {
                Teardown();
                SetState(EngineState.Waiting, L.T("Hata: ", "Error: ") + ex.Message + L.T(" — yeniden denenecek", " — retrying"));
            }
        }
        Teardown();
        try { if (_enum != null && _notify != null) _enum.UnregisterEndpointNotificationCallback(_notify); } catch { }
    }

    private void SetState(EngineState st, string status)
    {
        bool changed = st != _state || status != _status;
        _state = st; _status = status;
        if (!changed) return;
        if (status != _lastLogged) { Log.Write($"State: {st} — {status}"); _lastLogged = status; }
        try { StateChanged?.Invoke(); } catch { }
    }

    private MMDevice? Find(string? id, DataFlow flow)
    {
        var en = _enum ?? throw new WaitException(L.T("Ses aygıtı listesi alınamadı", "Could not list audio devices"));
        try
        {
            if (id == null)
                return en.HasDefaultAudioEndpoint(flow, Role.Console) ? en.GetDefaultAudioEndpoint(flow, Role.Console) : null;
            var d = en.GetDevice(id);
            return d.State == DeviceState.Active ? d : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Kullanıcı aygıt seçmediyse Windows varsayılanını kullanır; varsayılan bir sanal kabloysa
    /// (VB-CABLE kurulumu bunu yapabiliyor) ilk gerçek aygıta geçer.
    /// </summary>
    private MMDevice? PickDevice(string? id, DataFlow flow)
    {
        if (id != null) return Find(id, flow);
        var def = Find(null, flow);
        if (def != null && !DeviceNames.IsVirtualCable(def.FriendlyName)) return def;
        foreach (var d in _enum!.EnumerateAudioEndPoints(flow, DeviceState.Active))
            if (!DeviceNames.IsVirtualCable(d.FriendlyName)) return d;
        return null;
    }

    private void RefreshDevices()
    {
        _devicesDirty = false;
        var en = _enum;
        if (en == null) return;
        var sw = Stopwatch.StartNew();
        AudioDevice[] List(DataFlow f)
        {
            var r = new List<AudioDevice>();
            try
            {
                foreach (var d in en.EnumerateAudioEndPoints(f, DeviceState.Active))
                {
                    try { string n = d.FriendlyName; r.Add(new AudioDevice(d.ID, n, DeviceNames.IsVirtualCable(n))); }
                    catch (Exception ex) { Log.Write("Could not read device name: " + ex.Message); }
                }
            }
            catch (Exception ex) { Log.Write("Could not enumerate devices: " + ex.Message); }
            return r.ToArray();
        }
        string? Default(DataFlow f)
        {
            try { return en.HasDefaultAudioEndpoint(f, Role.Console) ? en.GetDefaultAudioEndpoint(f, Role.Console).ID : null; }
            catch { return null; }
        }
        Devices = new DeviceSnapshot(List(DataFlow.Capture), List(DataFlow.Render), Default(DataFlow.Capture), Default(DataFlow.Render));
        CableMissing = _s.OutputId == null && !Devices.HasCable;
        if (sw.ElapsedMilliseconds > 300) Log.Write($"Device enumeration took {sw.ElapsedMilliseconds} ms.");
        try { DevicesChanged?.Invoke(); } catch { }
    }

    private MMDevice? FindCable()
    {
        if (_s.OutputId != null) return Find(_s.OutputId, DataFlow.Render);
        foreach (var d in _enum!.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            if (d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    private static SampleKind KindOf(WaveFormat f) => f.Encoding switch
    {
        WaveFormatEncoding.IeeeFloat when f.BitsPerSample == 32 => SampleKind.Float32,
        WaveFormatEncoding.Pcm when f.BitsPerSample == 16 => SampleKind.Pcm16,
        WaveFormatEncoding.Pcm when f.BitsPerSample == 24 => SampleKind.Pcm24,
        WaveFormatEncoding.Pcm when f.BitsPerSample == 32 => SampleKind.Pcm32,
        _ => throw new NotSupportedException($"Unsupported audio format: {f}"),
    };

    private static string Describe(WaveFormat f) => $"{f.SampleRate} Hz, {f.Channels} kanal, {f.BitsPerSample} bit";

    private void Build()
    {
        var micDev = PickDevice(_s.MicId, DataFlow.Capture)
                     ?? throw new WaitException(L.T("Mikrofon bulunamadı — bağlanması bekleniyor", "No microphone — waiting for one"));
        var spkDev = PickDevice(_s.SpeakerId, DataFlow.Render)
                     ?? throw new WaitException(L.T("Hoparlör bulunamadı — bağlanması bekleniyor", "No speaker — waiting for one"));
        var outDev = FindCable();
        CableMissing = outDev == null;
        if (outDev == null) throw new WaitException(L.T("VB-CABLE bulunamadı — Kurulum kontrolünden kurabilirsin", "VB-CABLE not found — install it from Setup check"));
        if (spkDev.ID == outDev.ID)
            throw new WaitException(L.T("Hoparlör olarak sanal kablo seçili — gerçek hoparlörü seç", "A virtual cable is selected as speaker — pick your real speaker"));
        if (DeviceNames.IsVirtualCable(micDev.FriendlyName))
            throw new WaitException(L.T("Mikrofon olarak sanal kablo seçili — gerçek mikrofonu seç", "A virtual cable is selected as microphone — pick your real mic"));

        _micId = micDev.ID; _spkId = spkDev.ID; _outId = outDev.ID;
        _micName = micDev.FriendlyName; _spkName = spkDev.FriendlyName; _outName = outDev.FriendlyName;
        _learnKey = $"{micDev.ID}|{spkDev.ID}|{outDev.ID}";
        _s.Learned.TryGetValue(_learnKey, out var learned);

        var opt = _s.ToPipelineOptions();
        if (learned != null && _s.AutoMicDelay) { opt.MicDelayMs = Math.Max(opt.MicDelayMs, learned.MicDelayMs); opt.AlignmentKnown = true; }
        _pipe = new EchoPipeline(opt);
        _micFill = 0; Interlocked.Exchange(ref _frames, 0); _procAvgUs = 0; _procMaxUs = 0;

        // 1) Sanal kabloya çıkış
        int outRate, outCh;
        using (var ac = outDev.AudioClient) { var mf = ac.MixFormat; outRate = mf.SampleRate; outCh = mf.Channels; _outFmt = Describe(mf); }
        _outBuf = new DriftBuffer(ApmProcessor.SampleRate, outRate, _s.OutputBufferMs, 80, learned?.OutPpm ?? 0);
        _out = new WasapiOut(outDev, AudioClientShareMode.Shared, true, 20);
        _out.PlaybackStopped += OnPlaybackStopped;
        _out.Init(new CableProvider(_outBuf, outRate, outCh));
        _out.Play();

        // 2) Hoparlör loopback (referans). Önce olay güdümlü, olmazsa yoklamalı.
        if (!_forcePolling)
        {
            try { _loop = OpenLoopback(spkDev, true, learned); }
            catch (Exception ex)
            {
                Log.Write("Event-driven loopback failed (" + ex.Message + "); falling back to polling.");
                DropLoopback();
                _forcePolling = true;
            }
        }
        if (_forcePolling)
        {
            if (!_timerPeriod) { Native.timeBeginPeriod(1); _timerPeriod = true; } // 10 ms'lik uyku hassasiyeti için
            _loop = OpenLoopback(Find(_spkId, DataFlow.Render) ?? throw new WaitException(L.T("Hoparlör bulunamadı", "Speaker not found")), false, learned);
        }

        // 3) Mikrofon. Önce Windows'tan 48 kHz float iste; olmazsa aygıtın kendi biçimi + kendi dönüştürücümüz.
        if (!_mic48kFailed)
        {
            try { _mic = OpenMic(micDev, true); }
            catch (Exception ex)
            {
                Log.Write("Could not open microphone at 48 kHz (" + ex.Message + "); using device format.");
                var failed = _mic; _mic = null;
                if (failed != null) { failed.DataAvailable -= OnMic; failed.RecordingStopped -= OnRecordingStopped; Safe(failed.Dispose); }
                _mic48kFailed = true;
            }
        }
        if (_mic48kFailed)
            _mic = OpenMic(Find(_micId, DataFlow.Capture) ?? throw new WaitException(L.T("Mikrofon bulunamadı", "Microphone not found")), false);
        Log.Write($"Audio graph ready. Mic: {_micName} [{_micFmt}] | Speaker: {_spkName} [{_spkFmt}] | Output: {_outName} [{_outFmt}] | mic delay {opt.MicDelayMs} ms");
    }

    private void DropLoopback()
    {
        var failed = _loop; _loop = null;
        if (failed != null) { failed.DataAvailable -= OnLoopback; failed.RecordingStopped -= OnRecordingStopped; Safe(failed.Dispose); }
    }

    private LoopbackCapture OpenLoopback(MMDevice dev, bool eventSync, LearnedTiming? learned)
    {
        var cap = new LoopbackCapture(dev, eventSync);
        _loop = cap;
        var f = cap.WaveFormat;
        _spkFmt = Describe(f) + (eventSync ? "" : " (polling)");
        _loopConv = new MonoConverter(KindOf(f), f.Channels, f.SampleRate);
        _refBuf = new DriftBuffer(_loopConv.OutputRate, ApmProcessor.SampleRate, _s.ReferenceBufferMs, 80, learned?.RefPpm ?? 0);
        cap.DataAvailable += OnLoopback;
        cap.RecordingStopped += OnRecordingStopped;
        cap.StartRecording();
        return cap;
    }

    private WasapiCapture OpenMic(MMDevice dev, bool request48k)
    {
        var cap = new WasapiCapture(dev, true, 20);
        _mic = cap;
        if (request48k) cap.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(ApmProcessor.SampleRate, Math.Min(2, cap.WaveFormat.Channels));
        var f = cap.WaveFormat;
        _micFmt = Describe(f);
        _micConv = new MonoConverter(KindOf(f), f.Channels, f.SampleRate);
        _micRes = _micConv.OutputRate == ApmProcessor.SampleRate ? null : new StreamResampler(_micConv.OutputRate, ApmProcessor.SampleRate);
        cap.DataAvailable += OnMic;
        cap.RecordingStopped += OnRecordingStopped;
        cap.StartRecording();
        return cap;
    }

    private void Teardown()
    {
        var mic = _mic; var loop = _loop; var o = _out; var pipe = _pipe;
        if (mic == null && loop == null && o == null && pipe == null) return;

        // Öğrenilenleri sakla (en az 1 dk çalıştıysa): bir sonraki açılışta AEC daha çabuk oturur.
        try
        {
            if (pipe != null && pipe.Frames > 6000 && _refBuf != null && _outBuf != null && _learnKey.Length > 0)
            {
                if (_s.Learned.Count > 8) _s.Learned.Clear();
                _s.Learned[_learnKey] = new LearnedTiming
                {
                    MicDelayMs = _s.AutoMicDelay ? pipe.MicDelayMs : 0,
                    RefPpm = Math.Clamp(_refBuf.LearnedPpm, -300, 300),
                    OutPpm = Math.Clamp(_outBuf.LearnedPpm, -300, 300),
                };
                _s.Save();
            }
        }
        catch { }

        _mic = null; _loop = null; _out = null;
        if (mic != null) { mic.DataAvailable -= OnMic; mic.RecordingStopped -= OnRecordingStopped; Safe(mic.Dispose); }
        if (loop != null) { loop.DataAvailable -= OnLoopback; loop.RecordingStopped -= OnRecordingStopped; Safe(loop.Dispose); }
        if (o != null) { o.PlaybackStopped -= OnPlaybackStopped; Safe(o.Dispose); }
        lock (_pipeLock) _pipe = null;
        _refBuf = null; _outBuf = null;
        if (pipe != null) Safe(pipe.Dispose);
        if (_timerPeriod) { Native.timeEndPeriod(1); _timerPeriod = false; }
    }

    private static void Safe(Action a) { try { a(); } catch (Exception ex) { Log.Write("During shutdown: " + ex.Message); } }

    // ------------------------------------------------------------------ ses iş parçacıkları
    private void OnLoopback(object? sender, WaveInEventArgs e)
    {
        if (!t_mmcss) { Native.EnterProAudio(); t_mmcss = true; }
        var conv = _loopConv; var buf = _refBuf;
        if (conv == null || buf == null || e.BytesRecorded <= 0 || !ReferenceEquals(sender, _loop)) return;
        int need = conv.MaxOutput(e.BytesRecorded);
        if (_loopTmp.Length < need) _loopTmp = new float[need * 2];
        int n = conv.Convert(e.Buffer.AsSpan(0, e.BytesRecorded), _loopTmp);
        buf.Write(_loopTmp.AsSpan(0, n));
    }

    private void OnMic(object? sender, WaveInEventArgs e)
    {
        if (!t_mmcss) { Native.EnterProAudio(); t_mmcss = true; }
        var conv = _micConv;
        if (conv == null || e.BytesRecorded <= 0 || !ReferenceEquals(sender, _mic)) return;
        int need = conv.MaxOutput(e.BytesRecorded);
        if (_micTmp.Length < need) _micTmp = new float[need * 2];
        int n = conv.Convert(e.Buffer.AsSpan(0, e.BytesRecorded), _micTmp);
        float[] src = _micTmp;
        var res = _micRes;
        if (res != null)
        {
            int cap = res.MaxOutput(n);
            if (_micTmp2.Length < cap) _micTmp2 = new float[cap * 2];
            n = res.Process(_micTmp.AsSpan(0, n), _micTmp2);
            src = _micTmp2;
        }
        for (int i = 0; i < n; i++)
        {
            _micFrame[_micFill++] = src[i];
            if (_micFill == Frame) { _micFill = 0; ProcessFrame(); }
        }
    }

    private void ProcessFrame()
    {
        var rb = _refBuf; var ob = _outBuf;
        if (rb == null || ob == null) return;
        long t0 = Stopwatch.GetTimestamp();
        rb.Read(_refFrame); // hoparlör sessizse/akış yoksa sıfır döner
        lock (_pipeLock)
        {
            var pipe = _pipe;
            if (pipe == null) Array.Clear(_outFrame);
            else
            {
                try { pipe.Process(_micFrame, _refFrame, _outFrame); }
                catch (Exception ex)
                {
                    Array.Clear(_outFrame); // hata: ham mikrofonu değil sessizliği gönder
                    if (Interlocked.Read(ref _frames) % 500 == 0) Log.Write("Processing error: " + ex.Message);
                }
            }
        }
        ob.Write(_outFrame);
        double us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        _procAvgUs += 0.01 * (us - _procAvgUs);
        _procMaxUs = Math.Max(us, _procMaxUs * 0.999); // ~10 s içinde sönen tepe değeri
        Interlocked.Increment(ref _frames);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, _mic) && !ReferenceEquals(sender, _loop)) return; // eski grafikten
        Log.Write("Capture stopped: " + (e.Exception?.Message ?? "device closed"));
        // Loopback açılır açılmaz düşüyorsa olay güdümlü kip bu sürücüde çalışmıyor olabilir: yoklamalı kipe geç.
        if (ReferenceEquals(sender, _loop) && e.Exception != null && Interlocked.Read(ref _frames) < 300) _forcePolling = true;
        _restart = true; _wake.Set();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, _out)) return;
        Log.Write("Output to virtual cable stopped: " + (e.Exception?.Message ?? "device closed"));
        _restart = true; _wake.Set();
    }

    // ------------------------------------------------------------------ aygıt bildirimleri
    internal void OnDeviceEvent(string deviceId)
    {
        _devicesDirty = true;
        if (_state == EngineState.Running && (deviceId == _micId || deviceId == _spkId || deviceId == _outId)) _restart = true;
        _wake.Set();
    }

    internal void OnDefaultChanged(DataFlow flow, Role role, string? deviceId)
    {
        if (role != Role.Console) return;
        _devicesDirty = true;
        if ((flow == DataFlow.Capture && _s.MicId == null) || (flow == DataFlow.Render && _s.SpeakerId == null)) _checkFollow = true;
        _wake.Set();
    }

    public void Dispose()
    {
        _quit = true; _wake.Set();
        if (!_supervisor.Join(5000)) Log.Write("Supervisor thread did not stop in time.");
    }

    private sealed class NotificationClient(AudioEngine engine) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => engine.OnDeviceEvent(deviceId);
        public void OnDeviceAdded(string pwstrDeviceId) => engine.OnDeviceEvent(pwstrDeviceId);
        public void OnDeviceRemoved(string deviceId) => engine.OnDeviceEvent(deviceId);
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => engine.OnDefaultChanged(flow, role, defaultDeviceId);
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
