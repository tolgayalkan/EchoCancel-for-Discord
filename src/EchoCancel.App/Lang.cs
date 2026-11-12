using System.Runtime.InteropServices;

namespace EchoCancel.App;

/// <summary>Arayüz dili (Türkçe / English). Günlük her zaman İngilizcedir.</summary>
internal static class L
{
    public static bool En { get; private set; } = true;

    /// <param name="pref">"tr", "en" ya da "auto" (Windows görüntüleme diline göre)</param>
    public static void Init(string? pref) => En = pref switch
    {
        "tr" => false,
        "en" => true,
        _ => !WindowsIsTurkish(),
    };

    public static string T(string tr, string en) => En ? en : tr;

    [DllImport("kernel32.dll")] private static extern ushort GetUserDefaultUILanguage();

    private static bool WindowsIsTurkish()
    {
        try { return (GetUserDefaultUILanguage() & 0x3FF) == 0x1F; } // LANG_TURKISH
        catch { return false; }
    }
}
