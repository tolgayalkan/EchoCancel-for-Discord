using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EchoCancel.App;

internal static class Native
{
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Çağıran iş parçacığını Windows'un "Pro Audio" çoklu ortam sınıfına alır (oyun yükü altında ses kesilmesin).</summary>
    public static void EnterProAudio()
    {
        try { uint idx = 0; AvSetMmThreadCharacteristicsW("Pro Audio", ref idx); }
        catch { /* avrt yoksa normal öncelikle devam */ }
    }
}

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "EchoCancel";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string;
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue(Name, false);
    }
}
