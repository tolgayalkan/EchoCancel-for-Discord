using System.Diagnostics;

namespace EchoCancel.App;

/// <summary>Kurulum kontrolü: eksikleri listeler ve tek tıkla giderir.</summary>
internal sealed class SetupForm : Form
{
    private const int W = 460;
    private readonly AppSettings _s;
    private readonly AudioEngine _e;
    private readonly Panel _list = new() { BackColor = Theme.Bg };
    private readonly HintLabel _title = new() { BackColor = Theme.Bg, Color = Theme.Text, Font = Theme.Title, Center = true, Text = L.T("Kurulum kontrolü", "Setup check") };
    private readonly HintLabel _sub = new() { BackColor = Theme.Bg, Font = Theme.Base, Text = L.T("EchoCancel'in çalışması için gerekenler", "What EchoCancel needs to work") };
    private readonly FlatButton _refresh = new() { Text = L.T("Yeniden kontrol et", "Check again") };
    private readonly FlatButton _close = new() { Text = L.T("Kapat", "Close"), Primary = true };
    private bool _busy;

    public event Action<List<CheckResult>>? Checked;

    public SetupForm(AppSettings s, AudioEngine e)
    {
        _s = s; _e = e;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Base;
        Text = L.T("EchoCancel — kurulum kontrolü", "EchoCancel — setup check");
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        Controls.AddRange([_title, _sub, _list, _refresh, _close]);
        _refresh.Pressed += () => RunChecks();
        _close.Pressed += Hide;
        ShowRows([new CheckResult("wait", CheckState.Info, L.T("Kontrol ediliyor…", "Checking…"), L.T("Birkaç saniye sürebilir", "This can take a few seconds"), null)]);
    }

    private int S(int v) => v * DeviceDpi / 96;

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkWindow(this); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Hide(); base.OnKeyDown(e); }

    public void RunChecks()
    {
        if (_busy) return;
        _busy = true; _refresh.Enabled = false;
        Task.Run(() =>
        {
            List<CheckResult> r;
            try { r = SetupChecks.Run(_s); }
            catch (Exception ex) { r = [new CheckResult("err", CheckState.Error, L.T("Kontrol yapılamadı", "Check failed"), ex.Message, null)]; }
            return r;
        }).ContinueWith(t =>
        {
            _busy = false; _refresh.Enabled = true;
            ShowRows(t.Result);
            Checked?.Invoke(t.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ShowRows(List<CheckResult> rows)
    {
        SuspendLayout();
        foreach (Control c in _list.Controls) c.Dispose();
        _list.Controls.Clear();
        int w = S(W - 24), y = 0;
        foreach (var r in rows)
        {
            var row = new CheckRow(r) { Width = w };
            row.Action += () => DoAction(r, row);
            row.SetBounds(0, y, w, row.PreferredHeight(w));
            _list.Controls.Add(row);
            y += row.Height + S(8);
        }
        int x = S(12), top = S(12);
        _title.SetBounds(x, top, w, S(26));
        _sub.SetBounds(x, top + S(26), w, S(20));
        _list.SetBounds(x, top + S(56), w, y);
        int by = _list.Bottom + S(6);
        _refresh.SetBounds(x, by, S(150), S(32));
        _close.SetBounds(x + w - S(110), by, S(110), S(32));
        ClientSize = new Size(S(W), by + S(32 + 12));
        ResumeLayout();
    }

    private async void DoAction(CheckResult r, CheckRow row)
    {
        switch (r.Key)
        {
            case "cable":
                row.Busy(L.T("VB-CABLE indiriliyor…", "Downloading VB-CABLE…"));
                var progress = new Progress<string>(m => row.Busy(m));
                var (ok, msg) = await CableInstaller.InstallAsync(progress);
                if (!ok && MessageBox.Show(this, msg + L.T("\n\nİndirme sayfası açılsın mı?", "\n\nOpen the download page?"), "EchoCancel", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    try { Process.Start(new ProcessStartInfo(CableInstaller.HomePage) { UseShellExecute = true }); } catch { }
                _e.RefreshDevicesSoon(); _e.Restart();
                RunChecks();
                break;
            case "defout":
            case "defin":
                row.Busy(L.T("Düzeltiliyor…", "Fixing…"));
                await Task.Run(() => DefaultDevices.FixVirtualDefaults(_s.SpeakerId, _s.MicId));
                _e.RefreshDevicesSoon();
                RunChecks();
                break;
            case "discord":
                MessageBox.Show(this, L.T(
                    "Discord'da:\n\n" +
                    "1. Sol alttaki dişli (Kullanıcı Ayarları) → Ses ve Görüntü\n" +
                    "2. Giriş Aygıtı: CABLE Output (VB-Audio Virtual Cable)\n" +
                    "3. Çıkış Aygıtı: hoparlörün (EchoCancel'de seçili olanla aynı)\n" +
                    "4. Yankı Engelleme: KAPALI\n" +
                    "5. Giriş Hassasiyeti: otomatiği kapat, kaydırıcıyı en sola yakın bırak\n\n" +
                    "EchoCancel kapalıyken Discord'a sessizlik gider. EchoCancel olmadan konuşmak istersen Giriş Aygıtını mikrofonuna geri al.",
                    "In Discord:\n\n" +
                    "1. Gear icon at the bottom left (User Settings) → Voice & Video\n" +
                    "2. Input Device: CABLE Output (VB-Audio Virtual Cable)\n" +
                    "3. Output Device: your speakers (the same one selected in EchoCancel)\n" +
                    "4. Echo Cancellation: OFF\n" +
                    "5. Input Sensitivity: turn off automatic, move the slider almost all the way left\n\n" +
                    "While EchoCancel is off, Discord receives silence. To talk without EchoCancel, set the Input Device back to your microphone."),
                    L.T("Discord ayarı", "Discord setup"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
        }
    }

    private sealed class CheckRow : Smooth
    {
        private readonly CheckResult _r;
        private readonly FlatButton? _btn;
        private string _detail;
        public event Action? Action;

        public CheckRow(CheckResult r)
        {
            _r = r; _detail = r.Detail; BackColor = Theme.Bg;
            if (r.Action != null)
            {
                _btn = new FlatButton { Text = r.Action, Primary = r.State == CheckState.Error, BackColor = Theme.Card };
                _btn.Pressed += () => Action?.Invoke();
                Controls.Add(_btn);
            }
        }

        public void Busy(string text) { _detail = text; if (_btn != null) _btn.Enabled = false; Invalidate(); }

        private int S(int v) => v * DeviceDpi / 96;
        private int TextWidth(int w) => w - S(56) - (_btn != null ? S(96) : 0);

        public int PreferredHeight(int w)
        {
            var sz = TextRenderer.MeasureText(_detail, Theme.Small, new Size(TextWidth(w), 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            return Math.Max(S(56), S(34) + sz.Height + S(10));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            _btn?.SetBounds(Width - S(96), (Height - S(30)) / 2, S(84), S(30));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Prep(g); g.Clear(BackColor);
            Theme.Fill(g, new RectangleF(0, 0, Width - 1, Height - 1), S(10), Theme.Card);
            (Color c, string sym) = _r.State switch
            {
                CheckState.Ok => (Theme.Green, "✓"),
                CheckState.Warn => (Theme.Amber, "!"),
                CheckState.Error => (Theme.Red, "✕"),
                _ => (Theme.Accent, "i"),
            };
            float d = S(26);
            var circle = new RectangleF(S(14), S(14), d, d);
            Theme.Fill(g, circle, d / 2, Color.FromArgb(55, c));
            DrawText(g, sym, Theme.Bold, Rectangle.Round(circle), Theme.Mix(c, Color.White, 0.3f), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            int tx = S(52), tw = TextWidth(Width);
            DrawText(g, _r.Title, Theme.Bold, new Rectangle(tx, S(10), tw, S(20)), Theme.Text, TextFormatFlags.VerticalCenter);
            DrawText(g, _detail, Theme.Small, new Rectangle(tx, S(32), tw, Height - S(36)), Theme.Sub, TextFormatFlags.WordBreak);
        }
    }
}
