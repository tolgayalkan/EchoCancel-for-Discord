using System.Text.Json;
using EchoCancel.Core;

namespace EchoCancel.App;

public sealed class LearnedTiming
{
    public int MicDelayMs { get; set; }
    public double RefPpm { get; set; }
    public double OutPpm { get; set; }
}

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>"auto" (Windows diline göre), "tr" ya da "en".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>null = Windows varsayılan aygıtını izle.</summary>
    public string? MicId { get; set; }
    public string? SpeakerId { get; set; }
    /// <summary>null = adında "CABLE Input" geçen ilk aygıt (VB-CABLE).</summary>
    public string? OutputId { get; set; }

    public NoiseSuppression NoiseSuppression { get; set; } = NoiseSuppression.High;
    public bool AutoGain { get; set; }
    public bool GateEnabled { get; set; } = true;
    public GateSensitivity GateSensitivity { get; set; } = GateSensitivity.Medium;
    public int LookaheadMs { get; set; } = 30;
    public int HangoverMs { get; set; } = 350;
    public double EchoGuardDb { get; set; } = 12;
    public bool WarmupMute { get; set; } = true;
    public int MicDelayMs { get; set; } = 10;
    public bool AutoMicDelay { get; set; } = true;
    /// <summary>Referans ve çıkış arabelleklerinin hedef doluluğu (ms). Kesinti (boşalma) görürseniz artırın.</summary>
    public int ReferenceBufferMs { get; set; } = 30;
    public int OutputBufferMs { get; set; } = 30;
    /// <summary>Aygıt çiftine göre öğrenilen gecikme / saat farkı (sonraki açılışta hızlı başlamak için).</summary>
    public Dictionary<string, LearnedTiming> Learned { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EchoCancel");
    private static string FilePath => Path.Combine(Dir, "settings.json");
    private static readonly object IoLock = new();

    public static AppSettings Load()
    {
        MigrateFromYankiKes();
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) { Log.Write("Could not read settings, using defaults: " + ex.Message); }
        return new AppSettings();
    }

    /// <summary>Eski adı YankıKes olan sürümün ayarlarını bir kez taşır.</summary>
    private static void MigrateFromYankiKes()
    {
        try
        {
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YankiKes", "settings.json");
            if (File.Exists(FilePath) || !File.Exists(old)) return;
            Directory.CreateDirectory(Dir);
            File.Copy(old, FilePath);
            Log.Write("Settings migrated from YankiKes.");
        }
        catch { }
    }

    public void Save()
    {
        lock (IoLock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex) { Log.Write("Could not save settings: " + ex.Message); }
        }
    }

    public PipelineOptions ToPipelineOptions() => new()
    {
        NoiseSuppression = NoiseSuppression, AutoGain = AutoGain, GateEnabled = GateEnabled,
        GateSensitivity = GateSensitivity, LookaheadMs = LookaheadMs, HangoverMs = HangoverMs,
        EchoGuardDb = EchoGuardDb, WarmupMute = WarmupMute, MicDelayMs = MicDelayMs, AutoMicDelay = AutoMicDelay,
    };
}

public static class Log
{
    private static readonly object Lock = new();
    private static string PathName => Path.Combine(AppSettings.Dir, "log.txt");

    public static void Write(string message)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Dir);
                var fi = new FileInfo(PathName);
                if (fi.Exists && fi.Length > 512 * 1024) File.Move(PathName, PathName + ".old", true);
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch { /* günlük hiçbir zaman uygulamayı düşürmemeli */ }
        }
    }
}
