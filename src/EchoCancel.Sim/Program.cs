using System.Diagnostics;
using EchoCancel.Core;

const int F = 480, Fs = 48000;

if (args.Length == 0) { Help(); return 1; }
switch (args[0])
{
    case "sim" when args.Length >= 3: RunSim(args[1], args[2], args.Length > 3 && !args[3].StartsWith("--") ? args[3] : null, args.Contains("--early"), Opt(args, "--echo-db", -6), Opt(args, "--guard", new PipelineOptions().EchoGuardDb), Opt(args, "--ppm", 150) * 1e-6); return 0;
    case "selftest": return SelfTest();
    case "bench": RunBench(args.Length > 1 ? int.Parse(args[1]) : 60); return 0;
    case "file" when args.Length >= 4: RunFile(args[1], args[2], args[3], args.Contains("--no-gate")); return 0;
    default: Help(); return 1;
}

static double Opt(string[] a, string name, double def) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? double.Parse(a[i + 1], System.Globalization.CultureInfo.InvariantCulture) : def; }

static void Help() => Console.WriteLine("""
    EchoCancel.Sim — işlem zincirinin çevrimdışı sınaması
      selftest                            Arabellek, yeniden örnekleme ve biçim dönüştürme sınamaları
      bench [saniye]                      Çerçeve başına işlem süresini ölçer (sentetik sinyal)
      file <mik.wav> <ref.wav> <cikis.wav> [--no-gate]
                                          Kayıtlı mikrofon + hoparlör referansını işler
      sim <yakin.wav> <uzak.wav> [klasor] [--early] [--echo-db -6] [--ppm 150]
                                          Sentetik oda ile dört senaryoyu çalıştırır ve ölçer
    """);

// ---------------------------------------------------------------- yardımcılar
static double Db(ReadOnlySpan<float> x) { double s = 0; foreach (float v in x) s += (double)v * v; return x.Length == 0 || s <= 0 ? -120 : 10 * Math.Log10(s / x.Length); }
static void Normalize(float[] x, double targetDb) { float g = (float)Math.Pow(10, (targetDb - Db(x)) / 20); for (int i = 0; i < x.Length; i++) x[i] *= g; }
static void Place(float[] dst, float[] src, int srcOff, double t0, double t1)
{
    int a = (int)(t0 * Fs), n = Math.Min((int)((t1 - t0) * Fs), src.Length - srcOff);
    for (int i = 0; i < n; i++)
    {
        float fade = Math.Min(1f, Math.Min(i, n - 1 - i) / 480f); // 10 ms geçiş
        dst[a + i] += src[srcOff + i] * fade;
    }
}

static float[] SyntheticSpeechLike(int n, int seed)
{
    // Hece ritminde açılıp kapanan, harmonikli + gürültülü sinyal (yalnızca yük testi için)
    var rnd = new Random(seed); var x = new float[n]; double ph = 0, f0 = 120;
    for (int i = 0; i < n; i++)
    {
        double t = (double)i / Fs;
        double env = Math.Max(0, Math.Sin(2 * Math.PI * 3.1 * t + seed)) * (Math.Sin(2 * Math.PI * 0.23 * t + seed) > -0.3 ? 1 : 0);
        if (i % 4800 == 0) f0 = 100 + rnd.NextDouble() * 80;
        ph += 2 * Math.PI * f0 / Fs;
        double s = 0; for (int h = 1; h <= 12; h++) s += Math.Sin(h * ph) / h;
        x[i] = (float)(env * (0.12 * s + 0.02 * (rnd.NextDouble() * 2 - 1)));
    }
    return x;
}

// ---------------------------------------------------------------- selftest
static int SelfTest()
{
    int fails = 0;
    void Check(string name, bool ok, string detail) { Console.WriteLine($"  [{(ok ? "TAMAM" : "HATA ")}] {name}: {detail}"); if (!ok) fails++; }

    // 1) DriftBuffer: 48 kHz yazıcı → 44,1 kHz okuyucu, yazıcı saati +200 ppm hızlı; 1 kHz sinüs bozulmadan çıkmalı
    foreach (var (dstRate, chunk) in new[] { (44100, 441), (48000, 480), (96000, 960) })
    {
        var db = new DriftBuffer(48000, dstRate, 30);
        var o = new float[chunk]; var w = new float[600]; double ph = 0, wAcc = 0, err = 0, sig = 0; long nOut = 0;
        int secs = 600; double maxFill = 0, minFill = 1e9;
        for (int k = 0; k < secs * 100; k++)
        {
            wAcc += 480 * (1 + 200e-6); int nw = (int)wAcc; wAcc -= nw;
            for (int i = 0; i < nw; i++) { w[i] = (float)Math.Sin(ph); ph += 2 * Math.PI * 1000 / 48000; }
            db.Write(w.AsSpan(0, nw));
            if (!db.Read(o)) continue;
            if (k < 30000) continue; // ilk 5 dk: denetleyici bilerek yavaş oturur
            double f = db.FillMs; maxFill = Math.Max(maxFill, f); minFill = Math.Min(minFill, f);
            // Çıkışın yerel frekansı ≈ 1000*(1+200ppm)/(1+düzeltme); en iyi uyan sinüse göre artık hata
            for (int i = 1; i + 1 < chunk; i++)
            {
                // sinüs için: x[n-1] + x[n+1] = 2cos(w) x[n]
                double c = 2 * Math.Cos(2 * Math.PI * 1000.0 / dstRate);
                double e = o[i - 1] + o[i + 1] - c * o[i]; err += e * e; sig += o[i] * (double)o[i]; nOut++;
            }
        }
        double snr = 10 * Math.Log10(sig / Math.Max(err, 1e-30));
        Check($"DriftBuffer 48000→{dstRate}", db.Underruns == 0 && db.Overruns == 0 && snr > 40 && Math.Abs(db.CorrectionPpm - 200) < 25,
            $"boşalma {db.Underruns}, taşma {db.Overruns}, sinüs artığı {-snr:F1} dB, öğrenilen düzeltme {db.CorrectionPpm:F0} ppm (uygulanan 200), doluluk {minFill:F1}–{maxFill:F1} ms");
    }

    // 2) StreamResampler: 16 kHz → 48 kHz, 440 Hz sinüs; uzunluk ve seviye korunmalı
    {
        var rs = new StreamResampler(16000, 48000); var inp = new float[16000];
        for (int i = 0; i < inp.Length; i++) inp[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / 16000.0);
        var o = new float[rs.MaxOutput(160)]; int total = 0; double e = 0;
        for (int k = 0; k < 100; k++) { int n = rs.Process(inp.AsSpan(k * 160, 160), o); for (int i = 0; i < n; i++) e += o[i] * (double)o[i]; total += n; }
        double rms = Math.Sqrt(e / total);
        Check("StreamResampler 16000→48000", Math.Abs(total - 48000) <= 8 && Math.Abs(rms - 0.3536) < 0.005, $"{total} örnek (beklenen ≈48000), RMS {rms:F4} (beklenen 0,3536)");
    }

    // 3) MonoConverter: PCM16 stereo, PCM24 mono, float32 96 kHz (2'ye seyreltme)
    {
        var c16 = new MonoConverter(SampleKind.Pcm16, 2, 48000); var b = new byte[8];
        BitConverter.TryWriteBytes(b.AsSpan(0), (short)16384); BitConverter.TryWriteBytes(b.AsSpan(2), (short)-8192);
        BitConverter.TryWriteBytes(b.AsSpan(4), (short)-32768); BitConverter.TryWriteBytes(b.AsSpan(6), (short)-32768);
        var d = new float[4]; int n = c16.Convert(b, d);
        Check("MonoConverter PCM16 stereo", n == 2 && Math.Abs(d[0] - 0.125f) < 1e-6 && Math.Abs(d[1] + 1f) < 1e-6, $"{d[0]}, {d[1]}");
        var c24 = new MonoConverter(SampleKind.Pcm24, 1, 48000);
        n = c24.Convert(new byte[] { 0x00, 0x00, 0x40, 0x00, 0x00, 0xC0 }, d);
        Check("MonoConverter PCM24 mono", n == 2 && Math.Abs(d[0] - 0.5f) < 1e-6 && Math.Abs(d[1] + 0.5f) < 1e-6, $"{d[0]}, {d[1]}");
        var c96 = new MonoConverter(SampleKind.Float32, 1, 96000); var fb = new byte[12];
        BitConverter.TryWriteBytes(fb.AsSpan(0), 0.2f); BitConverter.TryWriteBytes(fb.AsSpan(4), 0.4f); BitConverter.TryWriteBytes(fb.AsSpan(8), 0.6f);
        n = c96.Convert(fb, d); int n2 = c96.Convert(fb.AsSpan(0, 4), d.AsSpan(1));
        Check("MonoConverter float32 96 kHz → 48 kHz", c96.OutputRate == 48000 && n == 1 && n2 == 1 && Math.Abs(d[0] - 0.3f) < 1e-6 && Math.Abs(d[1] - 0.4f) < 1e-6, $"{d[0]}, {d[1]} (parça sınırı aşan ortalama dahil)");
    }

    // 4) Kapı: sessizlikte tam sıfır; APM'e bozuk veri gelirse çıkışa NaN sızmamalı
    {
        using var p = new EchoPipeline(new PipelineOptions());
        var z = new float[F]; var o = new float[F]; var bad = new float[F]; Array.Fill(bad, float.NaN);
        bool finite = true, zero = true;
        for (int k = 0; k < 300; k++) { p.Process(k is > 100 and < 110 ? bad : z, z, o); foreach (float v in o) { if (!float.IsFinite(v)) finite = false; if (k > 200 && v != 0) zero = false; } }
        Check("NaN girişi ve sessizlik", finite && zero, $"çıkış sonlu: {finite}, sessizlikte tam sıfır: {zero}");
    }
    Console.WriteLine(fails == 0 ? "Tüm sınamalar geçti." : $"{fails} sınama BAŞARISIZ.");
    return fails == 0 ? 0 : 2;
}

// ---------------------------------------------------------------- bench
static void RunBench(int seconds)
{
    int n = seconds * Fs;
    float[] near = SyntheticSpeechLike(n, 1), far = SyntheticSpeechLike(n, 7);
    var mic = new float[n];
    for (int i = 0; i < n; i++) mic[i] = near[i] + (i >= 1900 ? 0.5f * far[i - 1900] : 0) + (i >= 3100 ? 0.2f * far[i - 3100] : 0);
    using var p = new EchoPipeline(new PipelineOptions());
    var o = new float[F];
    int frames = n / F; var us = new double[frames];
    for (int k = 0; k < 200; k++) p.Process(mic.AsSpan(k * F, F), far.AsSpan(k * F, F), o); // ısınma (JIT)
    long alloc0 = GC.GetAllocatedBytesForCurrentThread();
    var total = Stopwatch.StartNew();
    for (int k = 0; k < frames; k++)
    {
        long t0 = Stopwatch.GetTimestamp();
        p.Process(mic.AsSpan(k * F, F), far.AsSpan(k * F, F), o);
        us[k] = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
    }
    total.Stop();
    long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
    Array.Sort(us);
    double mean = us.Average();
    Console.WriteLine($"Çerçeve (10 ms) başına işlem süresi — {frames} çerçeve, {seconds} s ses");
    Console.WriteLine($"  ortalama {mean:F1} µs | medyan {us[frames / 2]:F1} µs | %99 {us[(int)(frames * 0.99)]:F1} µs | en uzun {us[^1]:F1} µs");
    Console.WriteLine($"  tek çekirdek yükü ≈ %{mean / 10000 * 100:F2}  (gerçek zamandan {seconds * 1000.0 / total.ElapsedMilliseconds:F0} kat hızlı)");
    Console.WriteLine($"  ses yolunda yönetilen bellek tahsisi: {alloc} bayt");
}

// ---------------------------------------------------------------- file
static void RunFile(string micPath, string refPath, string outPath, bool noGate)
{
    float[] mic = Wav.Read48kMono(micPath), rf = Wav.Read48kMono(refPath);
    int frames = Math.Min(mic.Length, rf.Length) / F;
    using var p = new EchoPipeline(new PipelineOptions { GateEnabled = !noGate });
    var o = new float[frames * F]; int open = 0;
    for (int k = 0; k < frames; k++) { p.Process(mic.AsSpan(k * F, F), rf.AsSpan(k * F, F), o.AsSpan(k * F, F)); if (p.GateOpen) open++; if (k % 200 == 199 && Environment.GetEnvironmentVariable("YK_DEBUG") != null) Console.WriteLine($"  t={k / 100.0:F0}s lag={p.EstimatedLagMs} conf={p.LagConfidence:F2}"); }
    Wav.Write48kMono(outPath, o);
    Console.WriteLine($"{frames / 100.0:F1} s işlendi → {outPath} | kapı açık: %{100.0 * open / Math.Max(1, frames):F0} | kestirilen gecikme: {p.EstimatedLagMs} ms | mikrofon gecikmesi: {p.MicDelayMs} ms");
}

// ---------------------------------------------------------------- sim
static void RunSim(string nearPath, string farPath, string? outDir, bool earlyEcho, double echoDb, double guard, double Ppm)
{
    float[] nearSrc = Wav.Read48kMono(nearPath), farSrc = Wav.Read48kMono(farPath);
    Normalize(nearSrc, -26); Normalize(farSrc, -24);
    double nearLen = Math.Min(11.0, nearSrc.Length / (double)Fs);
    const double Total = 62;
    int n = (int)(Total * Fs);
    var near = new float[n]; var far = new float[n];
    // Zaman çizelgesi (s): 0-12 yalnız hoparlör (soğuk başlangıç) | 12-24 yalnız ben | 24-34 yalnız hoparlör
    //                      34-46 ikisi birden | 46-50 sessizlik | 50-62 yalnız hoparlör, 54. saniyede yeniden başlatma
    Place(far, farSrc, 0, 0, 12); Place(far, farSrc, 12 * Fs, 24, 34); Place(far, farSrc, 22 * Fs, 34, 46); Place(far, farSrc, 34 * Fs, 50, 62);
    Place(near, nearSrc, 0, 12.5, 12.5 + nearLen); Place(near, nearSrc, 0, 34.5, 34.5 + nearLen);

    // Sentetik oda: doğrudan yol + üstel sönümlü seyrek yansımalar (RT60 ≈ 0,3 s)
    var rnd = new Random(42);
    int direct = (int)((earlyEcho ? 0.008 : 0.045) * Fs);
    var taps = new List<(int d, float g)> { (direct, 1f) };
    for (int i = 0; i < 80; i++) { double t = 0.002 + rnd.NextDouble() * 0.25; taps.Add((direct + (int)(t * Fs), (float)((rnd.NextDouble() * 2 - 1) * 0.6 * Math.Pow(10, -3 * t / 0.3)))); }
    var echo = new float[n];
    foreach (var (d, g) in taps) for (int i = d; i < n; i++) echo[i] += g * far[i - d];
    { double farDb = Db(far.AsSpan(Fs, 10 * Fs)), eDb = Db(echo.AsSpan(Fs, 10 * Fs)); float g = (float)Math.Pow(10, (farDb + echoDb - eDb) / 20); for (int i = 0; i < n; i++) echo[i] *= g; }
    var mic = new float[n];
    for (int i = 0; i < n; i++) mic[i] = near[i] + echo[i] + (float)((rnd.NextDouble() * 2 - 1) * 0.001); // ≈ -65 dBFS oda gürültüsü

    // Referans akışı hoparlör saatinde: +150 ppm kayma, 10 ms'lik paketler düzensiz geliyor
    var refRes = new StreamResampler(1.0 / (1.0 + Ppm));
    var refStream = new float[refRes.MaxOutput(n)];
    int refLen = refRes.Process(far, refStream);
    var refBuf = new DriftBuffer(Fs, Fs, targetMs: 30);

    var opt = new PipelineOptions { EchoGuardDb = guard };
    var optNoGate = new PipelineOptions { GateEnabled = false };
    int falseOpen = 0, farOnlyFrames = 0;
    var pGate = new EchoPipeline(opt); var pRaw = new EchoPipeline(optNoGate);
    var outG = new float[n]; var outR = new float[n]; var rf = new float[F];
    int frames = n / F, written = 0, restartFrame = 54 * 100;
    var fill = new List<double>(); var micDelayLog = new List<(double t, int ms)>(); int lastDelay = -1;
    double lagAtEnd = double.NaN, ppmBefore = 0;
    StreamWriter? trace = null;
    if (outDir != null) { Directory.CreateDirectory(outDir); trace = new StreamWriter(Path.Combine(outDir, "iz.csv")); trace.WriteLine("t,yakin_db,yanki_db,cikis_bant_db,silinen_db,taban_db,kapi,hoparlor,aec_cikis_db"); }
    for (int k = 0; k < frames; k++)
    {
        if (k == restartFrame)
        {
            // Aygıt değişimi: uygulama zinciri sıfırdan kurar (AEC yeniden yakınsar)
            lagAtEnd = pGate.EstimatedLagMs; ppmBefore = refBuf.CorrectionPpm;
            // Uygulama gibi: bu aygıtlar için öğrenilen mikrofon gecikmesi yeni zincire aktarılır
            opt.MicDelayMs = optNoGate.MicDelayMs = pGate.MicDelayMs; opt.AlignmentKnown = optNoGate.AlignmentKnown = true;
            pGate.Dispose(); pRaw.Dispose(); pGate = new EchoPipeline(opt); pRaw = new EchoPipeline(optNoGate); refBuf.Reset(); lastDelay = -1;
        }
        int avail = Math.Min(refLen, (int)((k + 1) * F * (1 + Ppm)));
        if (k % 3 != 1) { refBuf.Write(refStream.AsSpan(written, avail - written)); written = avail; } // her üç paketten biri gecikir
        refBuf.Read(rf);
        if (k > 500 && k < restartFrame) fill.Add(refBuf.FillMs);
        pGate.Process(mic.AsSpan(k * F, F), rf, outG.AsSpan(k * F, F));
        pRaw.Process(mic.AsSpan(k * F, F), rf, outR.AsSpan(k * F, F));
        trace?.WriteLine(FormattableString.Invariant($"{k / 100.0:F2},{Db(near.AsSpan(k * F, F)):F1},{Db(echo.AsSpan(k * F, F)):F1},{pGate.OutBandDb:F1},{pGate.RemovedDb:F1},{pGate.GateFloorDb:F1},{(pGate.GateOpen ? 1 : 0)},{(pGate.RenderActive ? 1 : 0)},{Db(outR.AsSpan(k * F, F)):F1}"));
        { double t = k / 100.0; if ((t >= 0 && t < 12) || (t >= 24.5 && t < 34) || (t >= 50.5 && t < 62)) { farOnlyFrames++; if (pGate.GateOpen) falseOpen++; } }
        if (k % 200 == 199 && Environment.GetEnvironmentVariable("YK_DEBUG") != null) Console.WriteLine($"  t={k / 100.0:F0}s lag={pGate.EstimatedLagMs} conf={pGate.LagConfidence:F2}");
        if (pGate.MicDelayMs != lastDelay) { lastDelay = pGate.MicDelayMs; micDelayLog.Add((k / 100.0, lastDelay)); }
    }

    trace?.Dispose();
    // Çıkışın temiz konuşmaya göre gecikmesi (yalnız-ben bölümünde çapraz korelasyon)
    int D = FindDelay(near, outR, (int)(14 * Fs), 6 * Fs);
    Span<float> G(double a, double b) => outG.AsSpan((int)(a * Fs), (int)((b - a) * Fs));
    Span<float> R(double a, double b) => outR.AsSpan((int)(a * Fs), (int)((b - a) * Fs));
    Span<float> M(double a, double b) => mic.AsSpan((int)(a * Fs), (int)((b - a) * Fs));
    static double NonZero(ReadOnlySpan<float> x) { int c = 0; foreach (float v in x) if (v != 0) c++; return 100.0 * c / x.Length; }

    Console.WriteLine($"=== Sentetik oda simülasyonu ({(earlyEcho ? "erken yankı: 8 ms" : "yankı gecikmesi: 45 ms")}) ===");
    Console.WriteLine($"Referans arabelleği: doluluk ort {fill.Average():F1} ms (en az {fill.Min():F1}, en çok {fill.Max():F1}), boşalma {refBuf.Underruns}, taşma {refBuf.Overruns}, saat düzeltmesi {ppmBefore:F0} ppm (uygulanan kayma {Ppm * 1e6:F0} ppm)");
    Console.WriteLine($"Mikrofon gecikmesi geçmişi: {string.Join(", ", micDelayLog.Select(e => $"{e.t:F1}s→{e.ms}ms"))} | kestirilen ham gecikme {lagAtEnd:F0} ms | çıkışın konuşmaya göre toplam gecikmesi {D * 1000.0 / Fs:F1} ms");

    Console.WriteLine("\n[2] Yalnızca hoparlör çalıyor");
    Console.WriteLine($"  soğuk başlangıç 0-3 s : mikrofonda yankı {Db(M(0.2, 3)):F1} dBFS | AEC sonrası {Db(R(0.2, 3)):F1} | kapı sonrası {Db(G(0.2, 3)):F1} dBFS, sıfır olmayan örnek %{NonZero(G(0.2, 3)):F1}");
    Console.WriteLine($"  yakınsama süresi (AEC+NS bastırması ≥ 20 dB): {ConvergenceTime(mic, outR, 0, 12):F1} s");
    Console.WriteLine($"  yakınsamış 26-34 s    : mikrofonda yankı {Db(M(26, 34)):F1} dBFS | AEC+NS sonrası {Db(R(26, 34)):F1} dBFS (bastırma {Db(M(26, 34)) - Db(R(26, 34)):F1} dB) | kapı sonrası sıfır olmayan örnek %{NonZero(G(26, 34)):F2}, tepe {Peak(G(26, 34)):E1}");

    Console.WriteLine($"  tüm yalnız-hoparlör bölümleri (soğuk başlangıç ve yeniden başlatma dahil, {farOnlyFrames / 100.0:F0} s): kapının yanlışlıkla açık kaldığı süre {falseOpen / 100.0:F2} s (%{100.0 * falseOpen / farOnlyFrames:F1}), yankı koruma eşiği {guard:F0} dB");
    Console.WriteLine("\n[1] Yalnızca ben konuşuyorum (12-24 s)");
    ReportSpeech(near, outG, outR, D, 12.5, 12.5 + nearLen);

    Console.WriteLine("\n[3] İkisi aynı anda (34-46 s)");
    double a3 = 34.5, b3 = 34.5 + nearLen;
    Console.WriteLine($"  mikrofonda konuşma/yankı oranı: {Db(near.AsSpan((int)(a3 * Fs), (int)(nearLen * Fs))) - Db(echo.AsSpan((int)(a3 * Fs), (int)(nearLen * Fs))):F1} dB");
    ReportSpeech(near, outG, outR, D, a3, b3);
    {
        // Aynı cümle [1]'de yankısız işlendi: fark = kalan yankı + çift konuşma bozulması
        int o1 = (int)(12.5 * Fs) + D, o3 = (int)(34.5 * Fs) + D, len = (int)(nearLen * Fs);
        double eRef = 0, eDiff = 0;
        for (int i = 0; i < len; i++) { double r = outR[o1 + i], d = outR[o3 + i] - r; eRef += r * r; eDiff += d * d; }
        Console.WriteLine($"  yankısız işlenmiş aynı cümleye göre: seviye farkı {Db(outR.AsSpan(o3, len)) - Db(outR.AsSpan(o1, len)):+0.0;-0.0} dB | konuşma / (kalan yankı + bozulma) = {10 * Math.Log10(eRef / eDiff):F1} dB");
    }
    Console.WriteLine($"  konuşma bittikten sonraki 0,5 s (hoparlör sürüyor; kapı kapanma payı): kapı sonrası {Db(G(34.5 + nearLen + 0.06, 34.5 + nearLen + 0.56)):F1} dBFS | mikrofondaki yankı {Db(M(34.5 + nearLen, 34.5 + nearLen + 0.5)):F1} dBFS");

    Console.WriteLine("\n[sessizlik] 47-50 s");
    Console.WriteLine($"  kapı sonrası sıfır olmayan örnek %{NonZero(G(47, 50)):F2} | kapısız çıkış {Db(R(47, 50)):F1} dBFS");

    Console.WriteLine("\n[4] Aygıt değişimi benzetimi: 54. saniyede zincir sıfırdan kuruldu (hoparlör çalmaya devam ediyor)");
    Console.WriteLine($"  yeniden yakınsama süresi: {ConvergenceTime(mic, outR, 54, 62):F1} s");
    Console.WriteLine($"  54-57 s: mikrofonda yankı {Db(M(54, 57)):F1} dBFS | kapı sonrası {Db(G(54, 57)):F1} dBFS, sıfır olmayan örnek %{NonZero(G(54, 57)):F1}");
    Console.WriteLine($"  58-62 s: kapı sonrası sıfır olmayan örnek %{NonZero(G(58, 62)):F2}");

    if (outDir != null)
    {
        Directory.CreateDirectory(outDir);
        Wav.Write48kMono(Path.Combine(outDir, "mikrofon_ham.wav"), mic);
        Wav.Write48kMono(Path.Combine(outDir, "cikis_kapili.wav"), outG);
        Wav.Write48kMono(Path.Combine(outDir, "cikis_kapisiz.wav"), outR);
    }
    pGate.Dispose(); pRaw.Dispose();
}

static float Peak(ReadOnlySpan<float> x) { float p = 0; foreach (float v in x) p = Math.Max(p, Math.Abs(v)); return p; }

static double ConvergenceTime(float[] mic, float[] outRaw, double t0, double t1)
{
    // Yankının duyulur olduğu (> -45 dBFS) 0,5 s'lik pencerelerde bastırma kalıcı olarak ≥ 20 dB olana kadar geçen süre
    double last = t0;
    for (double t = t0; t + 0.5 <= t1; t += 0.1)
    {
        int a = (int)(t * Fs);
        double m = Db(mic.AsSpan(a, Fs / 2));
        if (m > -45 && m - Db(outRaw.AsSpan(a, Fs / 2)) < 20) last = t + 0.5;
    }
    return last - t0;
}

static int FindDelay(float[] clean, float[] processed, int start, int len)
{
    double best = double.MinValue; int bestLag = 0;
    for (int lag = 0; lag < Fs / 5; lag += 6) { double c = 0; for (int i = 0; i < len; i += 6) c += clean[start + i] * processed[start + i + lag]; if (c > best) { best = c; bestLag = lag; } }
    int coarse = bestLag; best = double.MinValue;
    for (int lag = Math.Max(0, coarse - 8); lag <= coarse + 8; lag++) { double c = 0; for (int i = 0; i < len; i++) c += clean[start + i] * processed[start + i + lag]; if (c > best) { best = c; bestLag = lag; } }
    return bestLag;
}

static void ReportSpeech(float[] near, float[] outG, float[] outR, int D, double t0, double t1)
{
    int a = (int)(t0 * Fs), b = (int)(t1 * Fs);
    // Konuşma etkin çerçeveler ve kelime başlangıçları (temiz sinyalden)
    int active = 0, passed = 0, quiet = 100; var onsets = new List<int>();
    double eG = 0, eR = 0;
    for (int p = a; p + F <= b; p += F)
    {
        double db = Db(near.AsSpan(p, F));
        if (db > -42)
        {
            active++;
            double g = 0, r = 0;
            for (int i = 0; i < F; i++) { g += (double)outG[p + D + i] * outG[p + D + i]; r += (double)outR[p + D + i] * outR[p + D + i]; }
            eG += g; eR += r;
            if (g > 0.5 * r) passed++;
            if (quiet >= 20) onsets.Add(p);
            quiet = 0;
        }
        else if (db < -50) quiet++;
    }
    double worstG = 0, sumG = 0, worstA = 0, sumA = 0;
    foreach (int p in onsets)
    {
        int len = 12 * F; // başlangıçtan sonraki ilk 120 ms
        double dG = Db(outG.AsSpan(p + D, len)) - Db(outR.AsSpan(p + D, len));
        double dA = Db(outR.AsSpan(p + D, len)) - Db(near.AsSpan(p, len));
        worstG = Math.Min(worstG, dG); sumG += dG; worstA = Math.Min(worstA, dA); sumA += dA;
    }
    int c = Math.Max(1, onsets.Count);
    Console.WriteLine($"  konuşma etkin çerçevelerin %{100.0 * passed / Math.Max(1, active):F1}'i kapıdan geçti ({active} çerçeve) | kapının konuşma enerjisine etkisi {10 * Math.Log10(eG / eR):F2} dB");
    Console.WriteLine($"  cümle başlangıcı ({onsets.Count} adet), ilk 120 ms: AEC+NS kaynaklı seviye farkı ort {sumA / c:F1} dB (en kötü {worstA:F1}) | kapının eklediği kayıp ort {sumG / c:F2} dB (en kötü {worstG:F2})");
    Console.WriteLine($"  AEC+NS'in konuşma seviyesine etkisi (çıkış − temiz konuşma): {Db(outR.AsSpan(a + D, b - a)) - Db(near.AsSpan(a, b - a)):+0.0;-0.0} dB");
}
