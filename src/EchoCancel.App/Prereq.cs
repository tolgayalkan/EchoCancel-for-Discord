using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace EchoCancel.App;

/// <summary>
/// Windows'un varsayılan ses aygıtını değiştirmek için belgelenmemiş ama Windows 7'den beri kararlı
/// IPolicyConfig arayüzü (Ses Denetim Masası ve SoundSwitch gibi araçlar da bunu kullanır).
/// </summary>
internal static class DefaultDevices
{
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int isDefault, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    /// <summary>Aygıtı üç rolün hepsinde (konsol, çoklu ortam, iletişim) varsayılan yapar.</summary>
    public static bool Set(string deviceId)
    {
        try
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                int hr = 0;
                for (int role = 0; role < 3; role++) hr |= pc.SetDefaultEndpoint(deviceId, role);
                if (hr != 0) Log.Write($"Could not set default device (0x{hr:X8}).");
                return hr == 0;
            }
            finally { Marshal.ReleaseComObject(pc); }
        }
        catch (Exception ex) { Log.Write("Could not set default device: " + ex.Message); return false; }
    }

    private sealed class Saved { public string? Render { get; set; } public string? Capture { get; set; } }
    private static string SavedPath => Path.Combine(AppSettings.Dir, "varsayilanlar.json");

    /// <summary>Şu anki gerçek (sanal olmayan) varsayılan aygıtları kaydeder. VB-CABLE kurulmadan önce çağrılır.</summary>
    public static void Save()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            string? Get(DataFlow f)
            {
                if (!en.HasDefaultAudioEndpoint(f, Role.Console)) return null;
                var d = en.GetDefaultAudioEndpoint(f, Role.Console);
                return DeviceNames.IsVirtualCable(d.FriendlyName) ? null : d.ID;
            }
            var data = new Saved { Render = Get(DataFlow.Render), Capture = Get(DataFlow.Capture) };
            if (data.Render == null && data.Capture == null) return;
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(SavedPath, JsonSerializer.Serialize(data));
            Log.Write("Default devices saved.");
        }
        catch (Exception ex) { Log.Write("Could not save default devices: " + ex.Message); }
    }

    /// <summary>
    /// Varsayılan hoparlör ya da mikrofon bir sanal kabloysa, gerçek bir aygıta geri alır.
    /// Önce kaydedilmiş aygıtı, o yoksa <paramref name="preferRender"/>/<paramref name="preferCapture"/>, o da yoksa ilk gerçek aygıtı seçer.
    /// </summary>
    public static (bool render, bool capture) FixVirtualDefaults(string? preferRender = null, string? preferCapture = null)
    {
        bool r = false, c = false;
        try
        {
            Saved? saved = null;
            try { if (File.Exists(SavedPath)) saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavedPath)); } catch { }
            using var en = new MMDeviceEnumerator();
            bool Fix(DataFlow f, string? savedId, string? preferId)
            {
                if (!en.HasDefaultAudioEndpoint(f, Role.Console)) return false;
                var cur = en.GetDefaultAudioEndpoint(f, Role.Console);
                if (!DeviceNames.IsVirtualCable(cur.FriendlyName)) return false;
                var active = en.EnumerateAudioEndPoints(f, DeviceState.Active).Where(d => !DeviceNames.IsVirtualCable(d.FriendlyName)).ToList();
                var target = active.FirstOrDefault(d => d.ID == savedId) ?? active.FirstOrDefault(d => d.ID == preferId) ?? active.FirstOrDefault();
                if (target == null) return false;
                bool ok = Set(target.ID);
                if (ok) Log.Write($"Default {(f == DataFlow.Render ? "speaker" : "microphone")} restored: {target.FriendlyName}");
                return ok;
            }
            r = Fix(DataFlow.Render, saved?.Render, preferRender);
            c = Fix(DataFlow.Capture, saved?.Capture, preferCapture);
        }
        catch (Exception ex) { Log.Write("Could not fix default devices: " + ex.Message); }
        return (r, c);
    }
}

/// <summary>VB-CABLE sürücüsünü üreticinin sitesinden indirir ve kurar.</summary>
internal static class CableInstaller
{
    private const string Url = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip";
    private const string FallbackUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
    public const string HomePage = "https://vb-audio.com/Cable/";

    public static bool IsInstalled()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged)
                     .Any(d => d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>İndirir, kurar, Windows varsayılanlarını geri alır. Yönetici değilse UAC sorulur.</summary>
    public static async Task<(bool ok, string message)> InstallAsync(IProgress<string>? progress = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "EchoCancel-vbcable");
        try
        {
            DefaultDevices.Save();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, "vbcable.zip");
            progress?.Report(L.T("VB-CABLE indiriliyor…", "Downloading VB-CABLE…"));
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("EchoCancel/0.2");
                HttpResponseMessage resp = await http.GetAsync(Url);
                if (!resp.IsSuccessStatusCode) { resp.Dispose(); resp = await http.GetAsync(FallbackUrl); }
                using (resp)
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(zip);
                    await resp.Content.CopyToAsync(fs);
                }
            }
            ZipFile.ExtractToDirectory(zip, dir, true);
            string name = Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe";
            string? setup = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
            if (setup == null) return (false, L.T("İndirilen pakette kurulum dosyası bulunamadı.", "Setup file not found in the downloaded package."));

            progress?.Report(L.T("VB-CABLE kuruluyor… (Windows sürücü onayı isterse 'Yükle'ye bas)", "Installing VB-CABLE… (if Windows asks to install the driver, click Install)"));
            Log.Write("Starting VB-CABLE setup: " + setup);
            // -i: kur, -h: kurulum penceresini gösterme. Windows sürücü onayını yine de sorabilir.
            using var p = Process.Start(new ProcessStartInfo(setup, "-i -h") { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(setup)! });
            if (p == null) return (false, L.T("Kurulum başlatılamadı.", "Could not start setup."));
            await p.WaitForExitAsync();
            Log.Write("VB-CABLE setup finished, exit code " + p.ExitCode);

            // Yeni uç noktaların görünmesini bekle (en çok 20 sn), sonra Windows varsayılanlarını geri al.
            progress?.Report(L.T("Ses aygıtları yenileniyor…", "Refreshing audio devices…"));
            for (int i = 0; i < 40 && !IsInstalled(); i++) await Task.Delay(500);
            await Task.Delay(1500);
            DefaultDevices.FixVirtualDefaults();
            return IsInstalled()
                ? (true, L.T("VB-CABLE kuruldu.", "VB-CABLE installed."))
                : (true, L.T("Kurulum çalıştı ama kablo henüz görünmüyor. Bilgisayarı yeniden başlatman gerekebilir.", "Setup ran but the cable is not visible yet. You may need to restart your PC."));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, L.T("Yönetici izni verilmedi; kurulum iptal edildi.", "Administrator permission was denied; setup cancelled."));
        }
        catch (Exception ex)
        {
            Log.Write("VB-CABLE install failed: " + ex);
            return (false, L.T("İndirme ya da kurulum başarısız: ", "Download or install failed: ") + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}

public enum CheckState { Ok, Warn, Error, Info }

public sealed record CheckResult(string Key, CheckState State, string Title, string Detail, string? Action);

/// <summary>Kurulum kontrolü: neyin eksik olduğunu bulur. Arka planda (MTA iş parçacığında) çağrılmalı.</summary>
internal static class SetupChecks
{
    public static List<CheckResult> Run(AppSettings s)
    {
        var list = new List<CheckResult>();

        // 1) Ses işleme bileşeni (webrtc-apm.dll)
        try
        {
            using var apm = new EchoCancel.Core.ApmProcessor(true, EchoCancel.Core.NoiseSuppression.High, true, false);
            list.Add(new("apm", CheckState.Ok, L.T("Yankı giderme bileşeni", "Echo cancellation engine"), L.T("Yüklü ve çalışıyor", "Loaded and working"), null));
        }
        catch (Exception ex)
        {
            list.Add(new("apm", CheckState.Error, L.T("Yankı giderme bileşeni", "Echo cancellation engine"), L.T("Yüklenemedi: ", "Failed to load: ") + ex.Message + L.T(" — EchoCancel'i yeniden kur.", " — reinstall EchoCancel."), null));
        }

        using var en = new MMDeviceEnumerator();
        var renders = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var captures = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

        // 2) VB-CABLE
        bool cable = renders.Any(d => d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        list.Add(cable
            ? new("cable", CheckState.Ok, L.T("VB-CABLE sanal kablo", "VB-CABLE virtual cable"), L.T("Kurulu (CABLE Input / CABLE Output)", "Installed (CABLE Input / CABLE Output)"), null)
            : new("cable", CheckState.Error, L.T("VB-CABLE sanal kablo", "VB-CABLE virtual cable"), L.T("Kurulu değil — Discord'a ses gönderebilmek için gerekli", "Not installed — required to send audio to Discord"), L.T("Kur", "Install")));

        // 3) Gerçek mikrofon var mı
        var realMics = captures.Where(d => !DeviceNames.IsVirtualCable(d.FriendlyName)).ToList();
        list.Add(realMics.Count > 0
            ? new("mic", CheckState.Ok, L.T("Mikrofon", "Microphone"), string.Join(", ", realMics.Select(d => d.FriendlyName).Take(2)), null)
            : new("mic", CheckState.Error, L.T("Mikrofon", "Microphone"), L.T("Bağlı mikrofon bulunamadı", "No microphone connected"), null));

        // 4) Windows varsayılan hoparlörü sanal kablo mu
        string? defR = en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console) ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).FriendlyName : null;
        if (defR == null) list.Add(new("defout", CheckState.Warn, L.T("Windows varsayılan hoparlörü", "Windows default speaker"), L.T("Varsayılan çıkış yok", "No default output"), null));
        else if (DeviceNames.IsVirtualCable(defR))
            list.Add(new("defout", CheckState.Error, L.T("Windows varsayılan hoparlörü", "Windows default speaker"), $"'{defR}' " + L.T("seçili — oyun ve video sesi hoparlörden gelmez", "is selected — game and video sound won't play on your speakers"), L.T("Düzelt", "Fix")));
        else list.Add(new("defout", CheckState.Ok, L.T("Windows varsayılan hoparlörü", "Windows default speaker"), defR, null));

        // 5) Windows varsayılan mikrofonu sanal kablo mu (diğer uygulamalar için)
        string? defC = en.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console) ? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).FriendlyName : null;
        if (defC != null && DeviceNames.IsVirtualCable(defC))
            list.Add(new("defin", CheckState.Warn, L.T("Windows varsayılan mikrofonu", "Windows default microphone"), $"'{defC}' " + L.T("seçili — diğer uygulamalar mikrofonunu duymaz", "is selected — other apps won't hear your mic"), L.T("Düzelt", "Fix")));
        else if (defC != null) list.Add(new("defin", CheckState.Ok, L.T("Windows varsayılan mikrofonu", "Windows default microphone"), defC, null));

        // 6) Discord CABLE Output'u kullanıyor mu (ses oturumlarından)
        if (cable)
        {
            bool? discord = DiscordUsesCable(captures);
            bool discordRunning = Process.GetProcessesByName("Discord").Length > 0;
            list.Add(discord == true
                ? new("discord", CheckState.Ok, "Discord", L.T("CABLE Output'u kullanıyor", "Using CABLE Output"), null)
                : new("discord", CheckState.Info, L.T("Discord ayarı (elle)", "Discord setting (manual)"),
                    discordRunning
                        ? L.T("Ayarlar → Ses ve Görüntü → Giriş Aygıtı: CABLE Output. Yankı Engelleme: kapalı. (Sesli kanala girince burada ✓ görünür.)", "Settings → Voice & Video → Input Device: CABLE Output. Echo Cancellation: off. (Shows ✓ here once you join a voice channel.)")
                        : L.T("Discord açık değil. Açınca: Ayarlar → Ses ve Görüntü → Giriş Aygıtı: CABLE Output, Yankı Engelleme: kapalı.", "Discord is not running. When it is: Settings → Voice & Video → Input Device: CABLE Output, Echo Cancellation: off."),
                    L.T("Nasıl?", "How?")));
        }
        return list;
    }

    private static bool? DiscordUsesCable(List<MMDevice> captures)
    {
        try
        {
            var dev = captures.FirstOrDefault(d => d.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
            if (dev == null) return null;
            var sessions = dev.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var ss = sessions[i];
                if (ss.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                try
                {
                    using var p = Process.GetProcessById((int)ss.GetProcessID);
                    if (p.ProcessName.StartsWith("Discord", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
            return false;
        }
        catch { return null; }
    }
}
