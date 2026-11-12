using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace EchoCancel.App;

internal sealed class TrayContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly AudioEngine _engine;
    private readonly NotifyIcon _tray;
    private ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };
    private readonly Dictionary<string, Icon> _icons = new();
    private MainForm _main;
    private SetupForm? _setup;
    private string _iconKey = "";
    private string? _warning;
    private readonly RegisteredWaitHandle _showWait;
    private readonly Control _ui = new();
    private int _opens;

    public TrayContext(AppSettings settings, bool showWindow, EventWaitHandle showEvent)
    {
        _settings = settings;
        _ = _ui.Handle; // arayüz iş parçacığına geri çağrı için
        _icons["off"] = MakeIcon(Color.FromArgb(120, 120, 128));
        _icons["wait"] = MakeIcon(Theme.Amber);
        _icons["idle"] = MakeIcon(Theme.Accent);
        _icons["talk"] = MakeIcon(Theme.Green);

        _engine = new AudioEngine(_settings);
        _main = CreateMain(0);
        _menu = BuildMenu();

        _tray = new NotifyIcon { Icon = _icons["off"], Text = "EchoCancel", Visible = true, ContextMenuStrip = _menu };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };

        _timer.Tick += (_, _) => UpdateIcon();
        _timer.Start();

        // Masaüstü kısayoluna yeniden tıklanınca (ikinci kopya) pencereyi öne getir.
        _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
        {
            try { _ui.BeginInvoke(ShowMain); } catch { }
        }, null, Timeout.Infinite, false);

        if (showWindow) ShowMain();
        try { Log.Write($"UI ready {(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds:0} ms after process start."); } catch { }

        // Eksik varsa (ör. VB-CABLE) kurulum kontrolünü kendiliğinden aç.
        Task.Run(async () =>
        {
            await Task.Delay(1200);
            List<CheckResult> r;
            try { r = SetupChecks.Run(_settings); } catch (Exception ex) { Log.Write("Startup check: " + ex.Message); return; }
            try { _ui.BeginInvoke(() => ApplyChecks(r, autoOpen: true)); } catch { }
        });
    }

    private MainForm CreateMain(int tab)
    {
        var m = new MainForm(_settings, _engine, OpenSetup, ExitThread, SetLanguage, tab);
        _ = m.Handle; // pencere tutamacını şimdi oluştur: ilk açılış beklemesin
        m.SetWarning(_warning);
        return m;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = Theme.Menu();
        var open = new ToolStripMenuItem(L.T("EchoCancel'i aç", "Open EchoCancel")) { Font = Theme.Bold };
        open.Click += (_, _) => ShowMain();
        var enabled = new ToolStripMenuItem(L.T("Etkin", "Enabled"));
        enabled.Click += (_, _) => { _settings.Enabled = !_settings.Enabled; _settings.Save(); _engine.SetEnabled(_settings.Enabled); };
        var setup = new ToolStripMenuItem(L.T("Kurulum kontrolü", "Setup check")); setup.Click += (_, _) => OpenSetup();
        var exit = new ToolStripMenuItem(L.T("Çıkış", "Quit")); exit.Click += (_, _) => ExitThread();
        menu.Items.AddRange([open, enabled, new ToolStripSeparator(), setup, new ToolStripSeparator(), exit]);
        menu.Opening += (_, _) => enabled.Checked = _settings.Enabled;
        return menu;
    }

    /// <summary>Dil değişince pencereleri yeni dille yeniden kurar; ses akışı etkilenmez.</summary>
    private void SetLanguage(string lang)
    {
        if (_settings.Language == lang && (lang == "en") == L.En) return;
        _settings.Language = lang;
        _settings.Save();
        L.Init(lang);
        Log.Write("Language: " + lang);

        Point loc = _main.Location;
        bool visible = _main.Visible;
        var old = _main;
        _main = CreateMain(1);
        if (visible) _main.ShowNearTray(loc);
        old.Hide(); old.Dispose();

        if (_setup != null) { bool sv = _setup.Visible; _setup.Dispose(); _setup = null; if (sv) OpenSetup(); }

        var oldMenu = _menu;
        _menu = BuildMenu();
        _tray.ContextMenuStrip = _menu;
        oldMenu.Dispose();
        _iconKey = ""; UpdateIcon();
    }

    private void ApplyChecks(List<CheckResult> r, bool autoOpen)
    {
        var err = r.FirstOrDefault(c => c.State == CheckState.Error);
        _warning = err == null ? null : $"{err.Title}: {err.Detail.Split(" — ")[0]}";
        _main.SetWarning(_warning);
        if (err != null && autoOpen) OpenSetup();
    }

    private void ShowMain()
    {
        var sw = Stopwatch.StartNew();
        _main.ShowNearTray();
        _main.Update();
        if (_opens++ < 3 || sw.ElapsedMilliseconds > 300) Log.Write($"Window shown in {sw.ElapsedMilliseconds} ms.");
    }

    private void OpenSetup()
    {
        if (_setup == null)
        {
            _setup = new SetupForm(_settings, _engine);
            _setup.Checked += r => ApplyChecks(r, autoOpen: false);
        }
        _setup.Show();
        _setup.Activate();
        _setup.RunChecks();
    }

    private static Icon MakeIcon(Color fill)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(fill);
            g.FillEllipse(b, 1, 1, 30, 30);
            using var w = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using (var p = Theme.Round(new RectangleF(13, 6, 6, 14), 3)) g.FillPath(w, p);
            g.DrawArc(pen, 9, 9, 14, 14, 20, 140);
            g.DrawLine(pen, 16, 23, 16, 26);
        }
        IntPtr h = bmp.GetHicon();
        try { using var tmp = Icon.FromHandle(h); return (Icon)tmp.Clone(); }
        finally { Native.DestroyIcon(h); }
    }

    private void UpdateIcon()
    {
        string key, tip;
        switch (_engine.State)
        {
            case EngineState.Off: key = "off"; tip = L.T("EchoCancel: kapalı (Discord'a sessizlik)", "EchoCancel: off (Discord gets silence)"); break;
            case EngineState.Running:
                bool open = _engine.Snapshot().GateOpen;
                key = open ? "talk" : "idle";
                tip = open ? L.T("EchoCancel: sesin gidiyor", "EchoCancel: you're on air") : L.T("EchoCancel: hazır", "EchoCancel: ready");
                break;
            default: key = "wait"; tip = "EchoCancel: " + _engine.Status; break;
        }
        if (key != _iconKey) { _iconKey = key; _tray.Icon = _icons[key]; }
        if (tip.Length > 63) tip = tip[..63];
        if (_tray.Text != tip) _tray.Text = tip;
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _tray.Visible = false;
        _showWait.Unregister(null);
        _engine.Dispose();
        _settings.Save();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose(); _tray.Dispose(); _menu.Dispose(); _setup?.Dispose(); _main.Dispose(); _ui.Dispose();
            foreach (var i in _icons.Values) i.Dispose();
        }
        base.Dispose(disposing);
    }
}
