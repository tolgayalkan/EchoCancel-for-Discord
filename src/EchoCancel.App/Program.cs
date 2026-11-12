using System.Runtime;

namespace EchoCancel.App;

internal static class Program
{
    private const string ShowEventName = @"Local\EchoCancel.ShowWindow";

    [STAThread]
    private static int Main(string[] args)
    {
        // Kurulum programının çağırdığı başsız modlar (pencere açmaz)
        if (args.Contains("--ensure-cable")) { L.Init("en"); return EnsureCable(); }
        if (args.Contains("--save-defaults")) { DefaultDevices.Save(); return 0; }
        if (args.Contains("--fix-defaults")) { DefaultDevices.FixVirtualDefaults(); return 0; }

        bool tray = args.Contains("--tray");
        using var mutex = new Mutex(true, @"Local\EchoCancel.SingleInstance", out bool first);
        if (!first)
        {
            // Zaten çalışıyor: penceresini öne getir ve çık.
            if (!tray) try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            return 0;
        }

        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled error: " + e.ExceptionObject);

        var settings = AppSettings.Load();
        L.Init(settings.Language);
        ApplicationConfiguration.Initialize();
        Log.Write($"EchoCancel {Application.ProductVersion.Split('+')[0]} started{(tray ? " (tray)" : "")}.");
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var ctx = new TrayContext(settings, showWindow: !tray, showEvent);
        Application.Run(ctx);
        Log.Write("EchoCancel exited.");
        return 0;
    }

    private static int EnsureCable()
    {
        try
        {
            if (CableInstaller.IsInstalled())
            {
                DefaultDevices.FixVirtualDefaults();
                Log.Write("Setup: VB-CABLE already installed.");
                return 0;
            }
            var (ok, msg) = CableInstaller.InstallAsync().GetAwaiter().GetResult();
            Log.Write("Setup: " + msg);
            return ok ? 0 : 2;
        }
        catch (Exception ex) { Log.Write("Setup: VB-CABLE step failed: " + ex); return 3; }
    }
}
