using System.Diagnostics;
using EchoCancel.Core;

namespace EchoCancel.App;

/// <summary>Başlık: durum renginde mikrofon simgesi, ad ve durum satırı. Uyarı varken tıklanabilir.</summary>
internal sealed class Header : Smooth
{
    private bool _hover;
    public string Status { get; private set; } = "";
    public Color StatusColor { get; private set; } = Theme.Dim;
    public bool Clickable { get; private set; }
    public event Action? Clicked;

    public Header() { BackColor = Theme.Bg; Height = 48; }

    public void Set(string status, Color c, bool clickable)
    {
        if (status == Status && c == StatusColor && clickable == Clickable) return;
        Status = status; StatusColor = c; Clickable = clickable;
        Cursor = clickable ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Prep(g); g.Clear(BackColor);
        float s = DeviceDpi / 96f, d = 38 * s, y0 = (Height - d) / 2;
        Theme.Fill(g, new RectangleF(0, y0, d, d), d / 2, Theme.Mix(Theme.Card, StatusColor, 0.35f));
        using (var pen = new Pen(Color.White, 2f * s) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
        {
            float cx = d / 2, cy = y0 + d / 2;
            Theme.Fill(g, new RectangleF(cx - 4 * s, cy - 10 * s, 8 * s, 13 * s), 4 * s, Color.White);
            g.DrawArc(pen, cx - 8 * s, cy - 7 * s, 16 * s, 14 * s, 15, 150);
            g.DrawLine(pen, cx, cy + 7 * s, cx, cy + 10 * s);
        }
        int x = (int)(d + 12 * s);
        DrawText(g, "EchoCancel", Theme.Title, new Rectangle(x, 0, Width - x, Height / 2 + (int)(2 * s)), Theme.Text, TextFormatFlags.Bottom);
        Color sc = Theme.Mix(StatusColor, Color.White, _hover && Clickable ? 0.5f : 0.25f);
        string text = Clickable ? "⚠ " + Status : Status;
        DrawText(g, text, Theme.Base, new Rectangle(x, Height / 2 + (int)(2 * s), Width - x, Height / 2), sc, TextFormatFlags.Top);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { if (Clickable && e.Button == MouseButtons.Left) Clicked?.Invoke(); }
}

internal sealed class MainForm : Form
{
    private const int W = 372, Pad = 12, Inner = 12, ContentH = 412;

    private readonly AppSettings _s;
    private readonly AudioEngine _e;
    private readonly Action _openSetup, _exit;
    private readonly Action<string> _setLanguage;

    private readonly Header _header = new();
    private readonly ToggleRow _power = new() { Text = "" };
    private readonly Segmented _tabs = new() { BackColor = Theme.Bg };
    private readonly Panel _pGeneral = new() { BackColor = Theme.Bg };
    private readonly Panel _pSettings = new() { BackColor = Theme.Bg, Visible = false };

    // Genel sekmesi
    private readonly CardPanel _cLevels = new() { Caption = L.T("Ses", "Audio") };
    private readonly LevelMeter _mMic = new() { Text = L.T("Mikrofon", "Microphone"), Bar = Theme.Accent };
    private readonly LevelMeter _mSpk = new() { Text = L.T("Hoparlör", "Speakers"), Bar = Color.FromArgb(162, 112, 255) };
    private readonly LevelMeter _mOut = new() { Text = L.T("Discord'a", "To Discord"), Bar = Theme.Green };
    private readonly CardPanel _cDevices = new() { Caption = L.T("Aygıtlar", "Devices") };
    private readonly PickerRow _pMic = new() { Label = L.T("Mikrofon", "Microphone") };
    private readonly PickerRow _pSpk = new() { Label = L.T("Hoparlör", "Speakers") };
    private readonly PickerRow _pOut = new() { Label = L.T("Discord'a", "To Discord") };
    private readonly CardPanel _cSuppress = new() { Caption = L.T("Bastırma", "Suppression") };
    private readonly HintLabel _lGuard = new() { Text = L.T("Koruma", "Protection") }, _lGate = new() { Text = L.T("Ses kapısı", "Voice gate") };
    private readonly Segmented _sGuard = new() { Items = [L.T("Hafif", "Light"), L.T("Normal", "Normal"), L.T("Sıkı", "Strict")] };
    private readonly Segmented _sGate = new() { Items = [L.T("Kapalı", "Off"), L.T("Düşük", "Low"), L.T("Orta", "Medium"), L.T("Yüksek", "High")] };
    private readonly HintLabel _hSuppress = new();

    // Ayarlar sekmesi
    private readonly CardPanel _cApp = new() { Caption = L.T("Uygulama", "App") + " · v" + Application.ProductVersion.Split('+')[0] };
    private readonly HintLabel _lLang = new() { Text = L.T("Dil", "Language") };
    private readonly Segmented _sLang = new() { Items = ["Türkçe", "English"] };
    private readonly ToggleRow _tAutostart = new() { Text = L.T("Windows ile başlat", "Start with Windows"), Hint = L.T("Oturum açınca tepside sessizce başlar", "Starts quietly in the tray when you sign in") };
    private readonly CardPanel _cProc = new() { Caption = L.T("Ses işleme", "Processing") };
    private readonly HintLabel _lNs = new() { Text = L.T("Gürültü", "Noise") }, _lLook = new() { Text = L.T("Kelime başı", "Word onset") };
    private readonly Segmented _sNs = new() { Items = [L.T("Kapalı", "Off"), L.T("Düşük", "Low"), L.T("Orta", "Mid"), L.T("Yüksek", "High"), L.T("Maks", "Max")] };
    private readonly Segmented _sLook = new() { Items = ["10 ms", "20 ms", "30 ms", "50 ms"] };
    private readonly HintLabel _hProc = new();
    private readonly ToggleRow _tAgc = new() { Text = L.T("Otomatik kazanç", "Auto gain"), Hint = L.T("Mikrofonun kısık geliyorsa aç", "Turn on if your mic is too quiet") };
    private readonly ToggleRow _tWarm = new() { Text = L.T("Açılışta yankıyı öğren", "Learn echo on start"), Hint = L.T("İlk 2–3 sn sessiz kalır; o arada hoparlör sızmaz", "Stays silent for the first 2–3 s so speakers can't leak") };
    private readonly FlatButton _bSetup = new() { Text = L.T("Kurulum kontrolü", "Setup check") };
    private readonly FlatButton _bLog = new() { Text = L.T("Günlük klasörü", "Log folder") };
    private readonly FlatButton _bUninstall = new() { Text = L.T("EchoCancel'i kaldır", "Uninstall EchoCancel"), Danger = true };

    // Alt satır
    private readonly HintLabel _stats = new() { BackColor = Theme.Bg, Color = Theme.Dim, Center = true };
    private readonly FlatButton _bExit = new() { Text = L.T("Çıkış", "Quit") };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private readonly Process _proc = Process.GetCurrentProcess();
    private TimeSpan _lastCpu; private long _lastTick; private double _cpu; private int _tick;
    private string? _warning;

    private static readonly double[] GuardValues = [16, 12, 8];
    private static readonly int[] LookValues = [10, 20, 30, 50];
    private static string[] GuardHints => [
        L.T("Sesin daha az kesilir; hoparlör biraz sızabilir.", "Your voice is cut less; a little speaker sound may leak."),
        L.T("Dengeli (önerilen).", "Balanced (recommended)."),
        L.T("Hoparlör sızmaz; aynı anda konuşunca sesin kesilebilir.", "No speaker leaks; your voice may cut when both talk.")];
    private static string[] GateHints => [
        L.T("Kapı yok: sustuğunda da oda sesi gider.", "No gate: room sound is sent while you're silent."),
        L.T("Yalnızca net konuşma geçer.", "Only clear speech passes."),
        L.T("Dengeli (önerilen).", "Balanced (recommended)."),
        L.T("Kısık sesin de geçer; klavye sesi açabilir.", "Quiet speech passes too; typing may open it.")];

    public int SelectedTab => _tabs.Selected;

    public MainForm(AppSettings s, AudioEngine e, Action openSetup, Action exit, Action<string> setLanguage, int tab = 0)
    {
        _s = s; _e = e; _openSetup = openSetup; _exit = exit; _setLanguage = setLanguage;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        KeyPreview = true;
        BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Base;
        Text = "EchoCancel";
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        DoubleBuffered = true;

        _tabs.Items = [L.T("Genel", "General"), L.T("Ayarlar", "Settings")];
        _tabs.Selected = tab;
        Controls.AddRange([_header, _power, _tabs, _pGeneral, _pSettings, _stats, _bExit]);
        _pGeneral.Controls.AddRange([_cLevels, _cDevices, _cSuppress]);
        _cLevels.Controls.AddRange([_mMic, _mSpk, _mOut]);
        _cDevices.Controls.AddRange([_pMic, _pSpk, _pOut]);
        _cSuppress.Controls.AddRange([_lGuard, _sGuard, _lGate, _sGate, _hSuppress]);
        _pSettings.Controls.AddRange([_cApp, _cProc, _bSetup, _bLog, _bUninstall]);
        _cApp.Controls.AddRange([_lLang, _sLang, _tAutostart]);
        _cProc.Controls.AddRange([_lNs, _sNs, _lLook, _sLook, _hProc, _tAgc, _tWarm]);
        foreach (var l in new[] { _lGuard, _lGate, _lNs, _lLook, _lLang }) { l.Color = Theme.Sub; l.Font = Theme.Base; l.Center = true; }

        LoadValues();
        Wire();
        Layout2();
        ShowTab(tab);

        _timer.Tick += (_, _) => OnTick();
        VisibleChanged += (_, _) => { _timer.Enabled = Visible; if (Visible) { _lastTick = 0; RefreshAll(); } };
        _e.DevicesChanged += OnEngineEvent;
        _e.StateChanged += OnEngineEvent;
    }

    private void OnEngineEvent() { if (IsHandleCreated && !IsDisposed) try { BeginInvoke(RefreshPickers); } catch { } }

    private int S(int v) => v * DeviceDpi / 96;

    // ----------------------------------------------------------------- yerleşim (sabit boyut; sekme değişince pencere kımıldamaz)
    private void Layout2()
    {
        SuspendLayout();
        int w = S(W - Pad * 2), x = S(Pad), y = S(Pad), cw = w - S(Inner * 2), ix = S(Inner), lw = S(82);
        _header.SetBounds(x, y, w - S(56), S(48));
        _power.SetBounds(x + w - S(46), y + S(14), S(46), S(22));
        y += S(48 + 10);
        _tabs.SetBounds(x, y, w, S(34));
        y += S(34 + 10);
        _pGeneral.SetBounds(x, y, w, S(ContentH));
        _pSettings.SetBounds(x, y, w, S(ContentH));

        // Genel
        int cy = S(28);
        foreach (var m in new[] { _mMic, _mSpk, _mOut }) { m.SetBounds(ix, cy, cw, S(22)); cy += S(24); }
        _cLevels.SetBounds(0, 0, w, cy + S(6));
        int gy = _cLevels.Bottom + S(8);
        cy = S(28);
        foreach (var p in new[] { _pMic, _pSpk, _pOut }) { p.SetBounds(ix, cy, cw, S(36)); cy += S(38); }
        _cDevices.SetBounds(0, gy, w, cy + S(6));
        gy = _cDevices.Bottom + S(8);
        _lGuard.SetBounds(ix, S(28), lw, S(32)); _sGuard.SetBounds(ix + lw, S(28), cw - lw, S(32));
        _lGate.SetBounds(ix, S(66), lw, S(32)); _sGate.SetBounds(ix + lw, S(66), cw - lw, S(32));
        _hSuppress.SetBounds(ix, S(104), cw, S(32));
        _cSuppress.SetBounds(0, gy, w, S(142));

        // Ayarlar
        _lLang.SetBounds(ix, S(28), lw, S(32)); _sLang.SetBounds(ix + lw, S(28), cw - lw, S(32));
        _tAutostart.SetBounds(ix, S(66), cw, S(36));
        _cApp.SetBounds(0, 0, w, S(110));
        _lNs.SetBounds(ix, S(28), lw, S(32)); _sNs.SetBounds(ix + lw, S(28), cw - lw, S(32));
        _lLook.SetBounds(ix, S(66), lw, S(32)); _sLook.SetBounds(ix + lw, S(66), cw - lw, S(32));
        _hProc.SetBounds(ix, S(102), cw, S(18));
        _tAgc.SetBounds(ix, S(124), cw, S(36));
        _tWarm.SetBounds(ix, S(162), cw, S(36));
        _cProc.SetBounds(0, S(118), w, S(206));
        int half = (w - S(8)) / 2;
        _bSetup.SetBounds(0, S(332), half, S(32));
        _bLog.SetBounds(half + S(8), S(332), w - half - S(8), S(32));
        _bUninstall.SetBounds(0, S(372), w, S(32));

        y += S(ContentH + 10);
        _stats.SetBounds(x + S(2), y, w - S(96), S(30));
        _bExit.SetBounds(x + w - S(88), y, S(88), S(30));
        y += S(30) + S(Pad);
        ClientSize = new Size(S(W), y);
        ResumeLayout();
    }

    private void ShowTab(int i)
    {
        _pGeneral.Visible = i == 0;
        _pSettings.Visible = i == 1;
    }

    public void SetWarning(string? text)
    {
        _warning = text;
        RefreshHeader();
    }

    // ----------------------------------------------------------------- değerler
    private void LoadValues()
    {
        _power.Checked = _s.Enabled;
        _sGuard.Selected = Nearest(GuardValues, _s.EchoGuardDb);
        _sGate.Selected = !_s.GateEnabled ? 0 : _s.GateSensitivity switch { GateSensitivity.Low => 1, GateSensitivity.High => 3, _ => 2 };
        _sNs.Selected = _s.NoiseSuppression switch { NoiseSuppression.Off => 0, NoiseSuppression.Low => 1, NoiseSuppression.Moderate => 2, NoiseSuppression.High => 3, _ => 4 };
        _sLook.Selected = Nearest(LookValues.Select(v => (double)v).ToArray(), _s.LookaheadMs);
        _sLang.Selected = L.En ? 1 : 0;
        _tAgc.Checked = _s.AutoGain;
        _tWarm.Checked = _s.WarmupMute;
        try { _tAutostart.Checked = Autostart.IsEnabled(); } catch { }
        _bUninstall.Visible = File.Exists(UninstallerPath);
        UpdateHints();
    }

    private static string UninstallerPath => Path.Combine(AppContext.BaseDirectory, "Uninstall.exe");

    private static int Nearest(double[] values, double v)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++) if (Math.Abs(values[i] - v) < Math.Abs(values[best] - v)) best = i;
        return best;
    }

    private void UpdateHints()
    {
        _hSuppress.Set(L.T("Koruma: ", "Protection: ") + GuardHints[Math.Max(0, _sGuard.Selected)] + "\n" +
                       L.T("Kapı: ", "Gate: ") + GateHints[Math.Max(0, _sGate.Selected)]);
        _hProc.Set(L.T($"Kelime başı payı Discord'a giden sese {LookValues[Math.Max(0, _sLook.Selected)]} ms gecikme ekler.",
                       $"Word-onset margin adds {LookValues[Math.Max(0, _sLook.Selected)]} ms of delay to your voice."));
    }

    private void Wire()
    {
        _tabs.Picked += ShowTab;
        _header.Clicked += () => _openSetup();
        _power.Toggled += on => { _s.Enabled = on; _s.Save(); _e.SetEnabled(on); RefreshHeader(); };
        _sGuard.Picked += i => { _s.EchoGuardDb = GuardValues[i]; Live(); };
        _sGate.Picked += i =>
        {
            _s.GateEnabled = i != 0;
            if (i > 0) _s.GateSensitivity = i switch { 1 => GateSensitivity.Low, 3 => GateSensitivity.High, _ => GateSensitivity.Medium };
            Live();
        };
        _sNs.Picked += i => { _s.NoiseSuppression = i switch { 0 => NoiseSuppression.Off, 1 => NoiseSuppression.Low, 2 => NoiseSuppression.Moderate, 3 => NoiseSuppression.High, _ => NoiseSuppression.VeryHigh }; Rebuild(); };
        _sLook.Picked += i => { _s.LookaheadMs = LookValues[i]; Rebuild(); };
        _tAgc.Toggled += on => { _s.AutoGain = on; Rebuild(); };
        _tWarm.Toggled += on => { _s.WarmupMute = on; Live(); };
        _sLang.Picked += i => BeginInvoke(() => _setLanguage(i == 1 ? "en" : "tr"));
        _tAutostart.Toggled += on =>
        {
            try { Autostart.Set(on); }
            catch (Exception ex) { _tAutostart.Checked = !on; Log.Write("Could not change autostart: " + ex.Message); }
        };
        _pMic.Clicked += p => ShowDevicePicker(p, DeviceKind.Mic);
        _pSpk.Clicked += p => ShowDevicePicker(p, DeviceKind.Speaker);
        _pOut.Clicked += p => ShowDevicePicker(p, DeviceKind.Output);
        _bSetup.Pressed += () => _openSetup();
        _bLog.Pressed += () =>
        {
            try { Directory.CreateDirectory(AppSettings.Dir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.Dir}\"") { UseShellExecute = true }); } catch { }
        };
        _bUninstall.Pressed += Uninstall;
        _bExit.Pressed += () => _exit();
    }

    private void Live() { UpdateHints(); _s.Save(); _e.ApplyLive(); }
    private void Rebuild() { UpdateHints(); _s.Save(); _e.RebuildPipeline(); }

    private void Uninstall()
    {
        var answer = MessageBox.Show(this, L.T(
                "EchoCancel ve tüm ayarları bu bilgisayardan kaldırılsın mı?\n\n" +
                "• VB-CABLE sürücüsü kurulu kalır (başka uygulamalar da kullanabilir).\n" +
                "• Discord'da Giriş Aygıtı'nı mikrofonuna geri almayı unutma.",
                "Remove EchoCancel and all its settings from this PC?\n\n" +
                "• The VB-CABLE driver stays installed (other apps may use it).\n" +
                "• Remember to set Discord's Input Device back to your microphone."),
            L.T("EchoCancel'i kaldır", "Uninstall EchoCancel"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        try
        {
            // Sessiz kaldırma; Program Files'a yazabilmek için Windows yönetici onayı ister.
            Process.Start(new ProcessStartInfo(UninstallerPath, "/S") { UseShellExecute = true, Verb = "runas" });
            Log.Write("Uninstall started from settings.");
            _exit();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* kullanıcı UAC'yi reddetti */ }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("Kaldırma başlatılamadı: ", "Could not start uninstall: ") + ex.Message, "EchoCancel", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ----------------------------------------------------------------- aygıt seçimi (önbellekten; ses API'sine gitmez)
    private enum DeviceKind { Mic, Speaker, Output }

    private ContextMenuStrip? _menu;

    private void ShowDevicePicker(PickerRow row, DeviceKind kind)
    {
        var devs = _e.Devices;
        _menu?.Dispose();
        var m = _menu = Theme.Menu();
        string? current = kind switch { DeviceKind.Mic => _s.MicId, DeviceKind.Speaker => _s.SpeakerId, _ => _s.OutputId };
        IEnumerable<AudioDevice> list = kind switch
        {
            DeviceKind.Mic => devs.Captures.Where(d => !d.Virtual),
            DeviceKind.Speaker => devs.Renders.Where(d => !d.Virtual),
            _ => devs.Renders.Where(d => d.Virtual),
        };
        void Add(string text, string? id)
        {
            var it = new ToolStripMenuItem(text) { Checked = current == id };
            it.Click += (_, _) => Choose(kind, id);
            m.Items.Add(it);
        }
        Add(kind == DeviceKind.Output ? L.T("Otomatik (CABLE Input)", "Automatic (CABLE Input)") : L.T("Otomatik (Windows varsayılanı)", "Automatic (Windows default)"), null);
        m.Items.Add(new ToolStripSeparator());
        int n = 0;
        foreach (var d in list) { Add(d.Name, d.Id); n++; }
        if (n == 0)
            m.Items.Add(new ToolStripMenuItem(kind == DeviceKind.Output
                ? L.T("VB-CABLE bulunamadı — Kurulum kontrolüne bak", "VB-CABLE not found — see Setup check")
                : L.T("Aygıt bulunamadı", "No devices found")) { Enabled = false });
        m.MinimumSize = new Size(row.Width - S(78), 0);
        m.Show(row, new Point(S(78), row.Height));
    }

    private void Choose(DeviceKind kind, string? id)
    {
        switch (kind)
        {
            case DeviceKind.Mic: _s.MicId = id; break;
            case DeviceKind.Speaker: _s.SpeakerId = id; break;
            default: _s.OutputId = id; break;
        }
        _s.Save();
        _e.Restart();
        RefreshPickers();
    }

    private void RefreshPickers()
    {
        var devs = _e.Devices;
        string Name(string? id) => devs.Find(id)?.Name ?? "—";
        void Set(PickerRow p, string? chosen, string? active, string autoText)
        {
            if (chosen == null) { p.Value = autoText; p.Note = active != null ? Name(active) : null; }
            else { p.Value = devs.Find(chosen)?.Name ?? L.T("(bağlı değil)", "(not connected)"); p.Note = null; }
            p.Invalidate();
        }
        string auto = L.T("Otomatik", "Automatic");
        Set(_pMic, _s.MicId, _e.ActiveMicId, auto);
        Set(_pSpk, _s.SpeakerId, _e.ActiveSpeakerId, auto);
        Set(_pOut, _s.OutputId, _e.ActiveOutputId, L.T("Otomatik (CABLE Input)", "Automatic (CABLE Input)"));
        if (_s.OutputId == null && !devs.HasCable && devs.Renders.Length > 0)
        {
            _pOut.Value = L.T("VB-CABLE yok", "VB-CABLE missing");
            _pOut.Note = L.T("Ayarlar → Kurulum kontrolünden kurabilirsin", "Install it from Settings → Setup check");
            _pOut.Invalidate();
        }
        RefreshHeader();
    }

    // ----------------------------------------------------------------- canlı güncelleme
    private void RefreshAll() { RefreshPickers(); RefreshStats(); _power.Checked = _s.Enabled; }

    private void RefreshHeader()
    {
        var snap = _e.Snapshot();
        (string text, Color c) = _e.State switch
        {
            EngineState.Off => (L.T("Kapalı — Discord'a sessizlik gidiyor", "Off — Discord receives silence"), Theme.Dim),
            EngineState.Starting => (L.T("Başlatılıyor…", "Starting…"), Theme.Amber),
            EngineState.Waiting => (_warning ?? _e.Status, Theme.Amber),
            _ when _warning != null => (_warning, Theme.Amber),
            _ when snap.WarmingUp && snap.RenderActive => (L.T("Yankı öğreniliyor — sessiz", "Learning echo — muted"), Theme.Amber),
            _ when snap.GateOpen => (L.T("Sesin gidiyor", "You're on air"), Theme.Green),
            _ => (L.T("Hazır — sen konuşunca açılır", "Ready — opens when you speak"), Theme.Accent),
        };
        bool clickable = _warning != null || _e.State == EngineState.Waiting;
        _header.Set(text, c, clickable);
    }

    private void RefreshStats()
    {
        _proc.Refresh();
        long now = Stopwatch.GetTimestamp();
        if (_lastTick != 0)
        {
            double wall = (now - _lastTick) / (double)Stopwatch.Frequency;
            double cpu = (_proc.TotalProcessorTime - _lastCpu).TotalSeconds;
            if (wall > 0) _cpu += 0.4 * (cpu / wall * 100 / Environment.ProcessorCount - _cpu);
        }
        _lastTick = now; _lastCpu = _proc.TotalProcessorTime;
        var snap = _e.Snapshot();
        string lat = _e.State == EngineState.Running ? $" · {L.T("Gecikme", "Latency")} ≈ {snap.MicDelayMs + 15 + snap.LookaheadMs + snap.OutFillMs + 15:0} ms" : "";
        _stats.Set($"CPU {_cpu:0.0}% · RAM {_proc.PrivateMemorySize64 / 1048576.0:0} MB{lat}");
    }

    private void OnTick()
    {
        bool running = _e.State == EngineState.Running;
        if (_pGeneral.Visible)
        {
            var snap = _e.Snapshot();
            _mMic.Push(running ? snap.MicDb : -100);
            _mSpk.Push(running ? snap.RefDb : -100);
            _mOut.Push(running ? snap.OutDb : -100);
        }
        if (++_tick % 5 == 0) RefreshHeader();
        if (_tick % 20 == 0) RefreshStats();
    }

    // ----------------------------------------------------------------- pencere davranışı
    public void ShowNearTray(Point? at = null)
    {
        if (!Visible)
        {
            if (at is Point p) Location = p;
            else
            {
                var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(wa.Right - Width - S(12), wa.Bottom - Height - S(12));
            }
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkWindow(this); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) { Hide(); WindowState = FormWindowState.Normal; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _e.DevicesChanged -= OnEngineEvent;
            _e.StateChanged -= OnEngineEvent;
            _timer.Dispose(); _proc.Dispose(); _menu?.Dispose();
        }
        base.Dispose(disposing);
    }
}
