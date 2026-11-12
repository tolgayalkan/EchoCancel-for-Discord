using System.Reflection;
using System.Runtime.InteropServices;

namespace EchoCancel.Core;

/// <summary>
/// WebRTC Audio Processing Module'ün C sarmalayıcısına (webrtc-apm) doğrudan P/Invoke.
/// İmzalar SoundFlow.Extensions.WebRtc.Apm 1.4.2 paketindeki yerel kütüphaneyle eşleşir.
/// </summary>
internal static unsafe class ApmNative
{
    private const string Lib = "webrtc-apm";

    static ApmNative()
    {
        // Yerel kütüphaneyi önce uygulama klasöründe, sonra runtimes/<rid>/native altında ara.
        NativeLibrary.SetDllImportResolver(typeof(ApmNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        string file = OperatingSystem.IsWindows() ? "webrtc-apm.dll"
                    : OperatingSystem.IsMacOS() ? "libwebrtc-apm.dylib" : "libwebrtc-apm.so";
        string rid = (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
                     + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string baseDir = AppContext.BaseDirectory;
        var candidates = new List<string> { Path.Combine(baseDir, file), Path.Combine(baseDir, "runtimes", rid, "native", file) };
        // Tek dosyalı yayında yerel kütüphaneler geçici bir klasöre açılır; çalışma zamanı o klasörü burada bildirir.
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string dirs)
            foreach (string d in dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(d, file));
                candidates.Add(Path.Combine(d, "runtimes", rid, "native", file));
            }
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var h)) return h;
        }
        return NativeLibrary.TryLoad(file, asm, path, out var h2) ? h2 : IntPtr.Zero;
    }

    [DllImport(Lib, EntryPoint = "webrtc_apm_create")] public static extern IntPtr Create();
    [DllImport(Lib, EntryPoint = "webrtc_apm_destroy")] public static extern void Destroy(IntPtr apm);
    [DllImport(Lib, EntryPoint = "webrtc_apm_config_create")] public static extern IntPtr ConfigCreate();
    [DllImport(Lib, EntryPoint = "webrtc_apm_config_destroy")] public static extern void ConfigDestroy(IntPtr cfg);
    [DllImport(Lib, EntryPoint = "webrtc_apm_config_set_echo_canceller")] public static extern void ConfigSetEchoCanceller(IntPtr cfg, int enabled, int mobileMode);
    [DllImport(Lib, EntryPoint = "webrtc_apm_config_set_noise_suppression")] public static extern void ConfigSetNoiseSuppression(IntPtr cfg, int enabled, int level);
    [DllImport(Lib, EntryPoint = "webrtc_apm_config_set_gain_controller1")] public static extern void ConfigSetGainController1(IntPtr cfg, int enabled, int mode, int targetLevelDbfs, int compressionGainDb, int enableLimiter);
    [DllImport(Lib, EntryPoint = "webrtc_apm_config_set_gain_controller2")] public static extern void ConfigSetGainController2(IntPtr cfg, int enabled);
    [DllImport(Lib, EntryPoint = "webrtc_apm_config_set_high_pass_filter")] public static extern void ConfigSetHighPassFilter(IntPtr cfg, int enabled);
    [DllImport(Lib, EntryPoint = "webrtc_apm_config_set_pipeline")] public static extern void ConfigSetPipeline(IntPtr cfg, int maxInternalRate, int multiChannelRender, int multiChannelCapture, int downmixMethod);
    [DllImport(Lib, EntryPoint = "webrtc_apm_apply_config")] public static extern int ApplyConfig(IntPtr apm, IntPtr cfg);
    [DllImport(Lib, EntryPoint = "webrtc_apm_initialize")] public static extern int Initialize(IntPtr apm);
    [DllImport(Lib, EntryPoint = "webrtc_apm_stream_config_create")] public static extern IntPtr StreamConfigCreate(int sampleRateHz, nuint channels);
    [DllImport(Lib, EntryPoint = "webrtc_apm_stream_config_destroy")] public static extern void StreamConfigDestroy(IntPtr cfg);
    [DllImport(Lib, EntryPoint = "webrtc_apm_process_stream")] public static extern int ProcessStream(IntPtr apm, float** src, IntPtr inCfg, IntPtr outCfg, float** dest);
    [DllImport(Lib, EntryPoint = "webrtc_apm_process_reverse_stream")] public static extern int ProcessReverseStream(IntPtr apm, float** src, IntPtr inCfg, IntPtr outCfg, float** dest);
    [DllImport(Lib, EntryPoint = "webrtc_apm_set_stream_delay_ms")] public static extern void SetStreamDelayMs(IntPtr apm, int delayMs);
}

public enum NoiseSuppression { Off = -1, Low = 0, Moderate = 1, High = 2, VeryHigh = 3 }

/// <summary>48 kHz mono, 10 ms'lik (480 örnek) çerçevelerle çalışan WebRTC APM örneği (AEC3 + NS + HPF).</summary>
public sealed unsafe class ApmProcessor : IDisposable
{
    public const int SampleRate = 48000;
    public const int FrameSize = 480;

    private IntPtr _apm, _streamCfg;
    private readonly float* _capIn, _capOut, _revIn, _revOut;
    private readonly float** _pCapIn, _pCapOut, _pRevIn, _pRevOut;
    private bool _disposed;

    public ApmProcessor(bool echoCancellation, NoiseSuppression ns, bool highPass, bool autoGain, int maxInternalRate = 48000)
    {
        _apm = ApmNative.Create();
        if (_apm == IntPtr.Zero) throw new InvalidOperationException("WebRTC APM oluşturulamadı.");
        IntPtr cfg = ApmNative.ConfigCreate();
        try
        {
            ApmNative.ConfigSetPipeline(cfg, maxInternalRate, 0, 0, 0);
            ApmNative.ConfigSetEchoCanceller(cfg, echoCancellation ? 1 : 0, 0); // 0 = masaüstü AEC3
            ApmNative.ConfigSetNoiseSuppression(cfg, ns == NoiseSuppression.Off ? 0 : 1, Math.Max(0, (int)ns));
            ApmNative.ConfigSetHighPassFilter(cfg, highPass ? 1 : 0);
            // AdaptiveDigital (1), hedef -3 dBFS, 9 dB sıkıştırma kazancı, sınırlayıcı açık
            ApmNative.ConfigSetGainController1(cfg, autoGain ? 1 : 0, 1, 3, 9, 1);
            ApmNative.ConfigSetGainController2(cfg, 0);
            int err = ApmNative.ApplyConfig(_apm, cfg);
            if (err != 0) throw new InvalidOperationException($"APM yapılandırması reddedildi (hata {err}).");
        }
        finally { ApmNative.ConfigDestroy(cfg); }

        int init = ApmNative.Initialize(_apm);
        if (init != 0) throw new InvalidOperationException($"APM başlatılamadı (hata {init}).");
        _streamCfg = ApmNative.StreamConfigCreate(SampleRate, 1);

        _capIn = Alloc(); _capOut = Alloc(); _revIn = Alloc(); _revOut = Alloc();
        _pCapIn = AllocPtr(_capIn); _pCapOut = AllocPtr(_capOut); _pRevIn = AllocPtr(_revIn); _pRevOut = AllocPtr(_revOut);
    }

    private uint _rng = 0x9E3779B9;
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private float NextDither()
    {
        _rng = _rng * 1664525u + 1013904223u;
        return ((int)(_rng >> 16) - 32768) * (1e-6f / 32768f);
    }

    private static float* Alloc() => (float*)NativeMemory.AllocZeroed(FrameSize, sizeof(float));
    private static float** AllocPtr(float* p) { var pp = (float**)NativeMemory.Alloc((nuint)sizeof(float*)); *pp = p; return pp; }

    /// <summary>Hoparlöre giden (uzak uç / referans) 10 ms'lik çerçeveyi AEC'ye bildirir.</summary>
    public int ProcessRender(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSize) throw new ArgumentException("Çerçeve 480 örnek olmalı.");
        // Tam sayısal sessizlik APM'in iç süzgeçlerinde denormal sayılara ve ~4 kat CPU'ya yol açıyor
        // (ölçüldü); bu yüzden duyulamaz (≈ -126 dBFS) bir titreşim eklenir.
        for (int i = 0; i < FrameSize; i++) _revIn[i] = frame[i] + NextDither();
        return ApmNative.ProcessReverseStream(_apm, _pRevIn, _streamCfg, _streamCfg, _pRevOut);
    }

    /// <summary>Mikrofon çerçevesini yerinde işler (yankı giderme + gürültü bastırma).</summary>
    public int ProcessCapture(Span<float> frame)
    {
        if (frame.Length != FrameSize) throw new ArgumentException("Çerçeve 480 örnek olmalı.");
        for (int i = 0; i < FrameSize; i++) _capIn[i] = frame[i] + NextDither();
        int err = ApmNative.ProcessStream(_apm, _pCapIn, _streamCfg, _streamCfg, _pCapOut);
        if (err == 0) new Span<float>(_capOut, FrameSize).CopyTo(frame);
        else frame.Clear(); // hata durumunda ham mikrofonu asla geçirme
        return err;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_streamCfg != IntPtr.Zero) ApmNative.StreamConfigDestroy(_streamCfg);
        if (_apm != IntPtr.Zero) ApmNative.Destroy(_apm);
        _apm = _streamCfg = IntPtr.Zero;
        NativeMemory.Free(_capIn); NativeMemory.Free(_capOut); NativeMemory.Free(_revIn); NativeMemory.Free(_revOut);
        NativeMemory.Free(_pCapIn); NativeMemory.Free(_pCapOut); NativeMemory.Free(_pRevIn); NativeMemory.Free(_pRevOut);
        GC.SuppressFinalize(this);
    }

    ~ApmProcessor() { Dispose(); }
}
