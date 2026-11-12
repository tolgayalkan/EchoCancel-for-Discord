using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace EchoCancel.App;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(22, 22, 26);
    public static readonly Color Card = Color.FromArgb(34, 34, 40);
    public static readonly Color Track = Color.FromArgb(26, 26, 31);
    public static readonly Color Hover = Color.FromArgb(48, 48, 56);
    public static readonly Color Border = Color.FromArgb(56, 56, 64);
    public static readonly Color Text = Color.FromArgb(236, 236, 241);
    public static readonly Color Sub = Color.FromArgb(146, 146, 158);
    public static readonly Color Dim = Color.FromArgb(96, 96, 108);
    public static readonly Color Accent = Color.FromArgb(56, 132, 255);
    public static readonly Color Green = Color.FromArgb(38, 201, 99);
    public static readonly Color Amber = Color.FromArgb(245, 166, 35);
    public static readonly Color Red = Color.FromArgb(240, 82, 82);

    public static readonly Font Base = new("Segoe UI", 9f);
    public static readonly Font Small = new("Segoe UI", 8f);
    public static readonly Font Bold = new("Segoe UI Semibold", 9f);
    public static readonly Font Title = new("Segoe UI Semibold", 12.5f);
    public static readonly Font Section = new("Segoe UI Semibold", 7.5f);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, RectangleF r, float radius, Color c)
    {
        using var p = Round(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Windows 10/11 koyu başlık çubuğu ve Windows 11 yuvarlak köşeler.</summary>
    public static void DarkWindow(Form f)
    {
        try
        {
            int on = 1; DwmSetWindowAttribute(f.Handle, 20, ref on, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
            int round = 2; DwmSetWindowAttribute(f.Handle, 33, ref round, 4); // DWMWA_WINDOW_CORNER_PREFERENCE
            int caption = ColorTranslator.ToWin32(Bg); DwmSetWindowAttribute(f.Handle, 35, ref caption, 4); // DWMWA_CAPTION_COLOR (Win11)
        }
        catch { }
    }

    public static ContextMenuStrip Menu()
    {
        var m = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, ShowCheckMargin = true, Font = Base, BackColor = Card, ForeColor = Text };
        return m;
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new Colors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Dim;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle; float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using var pen = new Pen(Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, new[] { new PointF(cx - 4, cy), new PointF(cx - 1, cy + 3), new PointF(cx + 4, cy - 3) });
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Sub; base.OnRenderArrow(e); }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Card;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Card;
        public override Color ImageMarginGradientMiddle => Card;
        public override Color ImageMarginGradientEnd => Card;
        public override Color CheckBackground => Card;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}

/// <summary>Titremeden çizilen, şeffaf arka planlı temel denetim.</summary>
internal class Smooth : Control
{
    public Smooth()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        ForeColor = Theme.Text;
        Font = Theme.Base;
    }

    protected static void Prep(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    protected static void DrawText(Graphics g, string text, Font font, Rectangle r, Color c, TextFormatFlags flags) =>
        TextRenderer.DrawText(g, text, font, r, c, flags | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
}

/// <summary>Yuvarlak köşeli kart; içindeki denetimler kartın rengini alır.</summary>
internal sealed class CardPanel : Panel
{
    public string Caption { get; set; } = "";
    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        Theme.Fill(g, new RectangleF(0, 0, Width - 1, Height - 1), 10 * DeviceDpi / 96f, Theme.Card);
        if (Caption.Length > 0)
            TextRenderer.DrawText(g, Caption.ToUpperInvariant(), Theme.Section, new Point(Scale(12), Scale(9)), Theme.Sub, TextFormatFlags.NoPadding);
    }
    private int Scale(int v) => v * DeviceDpi / 96;
}

/// <summary>G-Helper'daki gibi yan yana seçenek düğmeleri.</summary>
internal sealed class Segmented : Smooth
{
    private string[] _items = [];
    private int _selected = -1, _hover = -1;
    public event Action<int>? Picked;

    public string[] Items { get => _items; set { _items = value; Invalidate(); } }
    public int Selected { get => _selected; set { if (_selected != value) { _selected = value; Invalidate(); } } }

    public Segmented() { Cursor = Cursors.Hand; Height = 32; BackColor = Theme.Card; }

    private RectangleF Seg(int i)
    {
        float pad = 3 * DeviceDpi / 96f, w = (Width - pad * 2) / Math.Max(1, _items.Length);
        return new RectangleF(pad + i * w, pad, w, Height - pad * 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Prep(g); g.Clear(BackColor);
        float rad = 8 * DeviceDpi / 96f;
        Theme.Fill(g, new RectangleF(0, 0, Width - 1, Height - 1), rad, Theme.Track);
        for (int i = 0; i < _items.Length; i++)
        {
            var r = Seg(i);
            if (i == _selected) Theme.Fill(g, r, rad - 2, Theme.Accent);
            else if (i == _hover) Theme.Fill(g, r, rad - 2, Theme.Hover);
            DrawText(g, _items[i], i == _selected ? Theme.Bold : Theme.Base, Rectangle.Round(r),
                i == _selected ? Color.White : Theme.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private int Hit(Point p) { for (int i = 0; i < _items.Length; i++) if (Seg(i).Contains(p)) return i; return -1; }
    protected override void OnMouseMove(MouseEventArgs e) { int h = Hit(e.Location); if (h != _hover) { _hover = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int h = Hit(e.Location);
        if (h < 0 || h == _selected) return;
        Selected = h;
        Picked?.Invoke(h);
    }
}

/// <summary>Solda yazı, sağda açma/kapama anahtarı; satırın tamamı tıklanır.</summary>
internal sealed class ToggleRow : Smooth
{
    private bool _checked, _hover;
    private float _anim;
    private readonly System.Windows.Forms.Timer _t = new() { Interval = 15 };
    public string Hint { get; set; } = "";
    public event Action<bool>? Toggled;

    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; if (!IsHandleCreated || !Visible) _anim = value ? 1 : 0; else _t.Start(); Invalidate(); }
    }

    public ToggleRow()
    {
        Cursor = Cursors.Hand; Height = 30; BackColor = Theme.Card;
        _t.Tick += (_, _) =>
        {
            float target = _checked ? 1 : 0;
            _anim += Math.Sign(target - _anim) * 0.2f;
            if (Math.Abs(_anim - target) < 0.2f) { _anim = target; _t.Stop(); }
            Invalidate();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Prep(g); g.Clear(BackColor);
        float s = DeviceDpi / 96f;
        float tw = 36 * s, th = 20 * s;
        var track = new RectangleF(Width - tw - 1, (Height - th) / 2, tw, th);
        bool hasHint = Hint.Length > 0;
        var textR = new Rectangle(0, 0, (int)(track.X - 8 * s), hasHint ? Height / 2 + (int)(2 * s) : Height);
        DrawText(g, Text, Theme.Base, textR, Theme.Text, hasHint ? TextFormatFlags.Bottom : TextFormatFlags.VerticalCenter);
        if (hasHint) DrawText(g, Hint, Theme.Small, new Rectangle(0, Height / 2 + (int)(2 * s), textR.Width, Height / 2), Theme.Sub, TextFormatFlags.Top);
        Color off = _hover ? Color.FromArgb(84, 84, 96) : Color.FromArgb(70, 70, 80);
        Theme.Fill(g, track, th / 2, Theme.Mix(off, Theme.Accent, _anim));
        float k = th - 6 * s;
        float kx = track.X + 3 * s + (_anim * (tw - k - 6 * s));
        using var kb = new SolidBrush(Color.White);
        g.FillEllipse(kb, kx, track.Y + 3 * s, k, k);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Checked = !_checked;
        Toggled?.Invoke(_checked);
    }
    protected override void Dispose(bool disposing) { if (disposing) _t.Dispose(); base.Dispose(disposing); }
}

/// <summary>Ses seviyesi çubuğu: etiket, yumuşatılmış seviye ve tepe çizgisi.</summary>
internal sealed class LevelMeter : Smooth
{
    private float _shown = -90, _peak = -90; private int _peakHold;
    public Color Bar { get; set; } = Theme.Accent;
    public bool Muted { get; set; }

    public LevelMeter() { Height = 22; BackColor = Theme.Card; }

    public void Push(float db)
    {
        if (float.IsNaN(db)) db = -100;
        _shown = db > _shown ? db : Math.Max(db, _shown - 2.5f); // hızlı yüksel, yavaş in
        if (db >= _peak) { _peak = db; _peakHold = 20; }
        else if (--_peakHold < 0) _peak = Math.Max(-90, _peak - 1.5f);
        Invalidate();
    }

    private static float Frac(float db) => Math.Clamp((db + 66f) / 66f, 0f, 1f);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Prep(g); g.Clear(BackColor);
        float s = DeviceDpi / 96f;
        int labelW = (int)(78 * s), valueW = (int)(52 * s);
        DrawText(g, Text, Theme.Base, new Rectangle(0, 0, labelW, Height), Theme.Sub, TextFormatFlags.VerticalCenter);
        float h = 8 * s;
        var track = new RectangleF(labelW, (Height - h) / 2, Width - labelW - valueW, h);
        Theme.Fill(g, track, h / 2, Theme.Track);
        float f = Frac(_shown);
        if (f > 0.005f)
        {
            var fill = new RectangleF(track.X, track.Y, Math.Max(h, track.Width * f), h);
            Theme.Fill(g, fill, h / 2, Muted ? Theme.Dim : Bar);
        }
        float pf = Frac(_peak);
        if (pf > 0.01f)
        {
            using var pb = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            g.FillRectangle(pb, track.X + track.Width * pf - 1.5f * s, track.Y - 1 * s, 2 * s, h + 2 * s);
        }
        string v = _shown <= -89 ? "—" : $"{_shown:0} dB";
        DrawText(g, v, Theme.Small, new Rectangle((int)track.Right, 0, valueW, Height), Theme.Sub, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Aygıt seçici: solda etiket, sağda seçili aygıt adı ve ok; tıklanınca koyu menü açılır.</summary>
internal sealed class PickerRow : Smooth
{
    private bool _hover;
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Note { get; set; }
    public event Action<PickerRow>? Clicked;

    public PickerRow() { Cursor = Cursors.Hand; Height = 34; BackColor = Theme.Card; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Prep(g); g.Clear(BackColor);
        float s = DeviceDpi / 96f;
        int labelW = (int)(78 * s);
        DrawText(g, Label, Theme.Base, new Rectangle(0, 0, labelW, Height), Theme.Sub, TextFormatFlags.VerticalCenter);
        var box = new RectangleF(labelW, 2 * s, Width - labelW - 1, Height - 4 * s);
        Theme.Fill(g, box, 7 * s, _hover ? Theme.Hover : Theme.Track);
        var tr = Rectangle.Round(new RectangleF(box.X + 10 * s, box.Y, box.Width - 34 * s, box.Height));
        if (Note != null)
        {
            DrawText(g, Value, Theme.Base, new Rectangle(tr.X, tr.Y, tr.Width, tr.Height / 2 + (int)(3 * s)), Theme.Text, TextFormatFlags.Bottom);
            DrawText(g, Note, Theme.Small, new Rectangle(tr.X, tr.Y + tr.Height / 2 + (int)(2 * s), tr.Width, tr.Height / 2), Theme.Sub, TextFormatFlags.Top);
        }
        else DrawText(g, Value, Theme.Base, tr, Theme.Text, TextFormatFlags.VerticalCenter);
        float cx = box.Right - 16 * s, cy = box.Y + box.Height / 2;
        using var pen = new Pen(Theme.Sub, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[] { new PointF(cx - 4 * s, cy - 2 * s), new PointF(cx, cy + 2 * s), new PointF(cx + 4 * s, cy - 2 * s) });
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Clicked?.Invoke(this); }
}

/// <summary>Durum hapı: renkli nokta + yazı.</summary>
internal sealed class Chip : Smooth
{
    public Color Dot { get; set; } = Theme.Dim;
    public Chip() { Height = 24; BackColor = Theme.Card; }
    public void Set(string text, Color dot)
    {
        if (Text == text && Dot == dot) return;
        Text = text; Dot = dot; Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Prep(g); g.Clear(BackColor);
        float s = DeviceDpi / 96f;
        var size = TextRenderer.MeasureText(Text, Theme.Bold, Size, TextFormatFlags.NoPadding);
        float w = Math.Min(Width - 1, size.Width + 30 * s);
        var r = new RectangleF(0, 0, w, Height - 1);
        Theme.Fill(g, r, r.Height / 2, Color.FromArgb(40, Dot));
        using (var b = new SolidBrush(Dot)) g.FillEllipse(b, 10 * s, Height / 2f - 4 * s, 8 * s, 8 * s);
        DrawText(g, Text, Theme.Bold, Rectangle.Round(new RectangleF(23 * s, 0, w - 26 * s, Height)), Theme.Mix(Dot, Color.White, 0.35f), TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Düz, yuvarlak köşeli düğme.</summary>
internal sealed class FlatButton : Smooth
{
    private bool _hover, _down;
    public bool Primary { get; set; }
    public bool Danger { get; set; }
    public event Action? Pressed;
    public FlatButton() { Cursor = Cursors.Hand; Height = 30; BackColor = Theme.Bg; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Prep(g); g.Clear(BackColor);
        Color c = Primary ? Theme.Accent : Danger ? Color.FromArgb(70, 34, 38) : Theme.Card;
        if (_hover) c = Theme.Mix(c, Color.White, Primary ? 0.12f : 0.06f);
        if (_down) c = Theme.Mix(c, Color.Black, 0.15f);
        if (!Enabled) c = Theme.Track;
        Theme.Fill(g, new RectangleF(0, 0, Width - 1, Height - 1), 8 * DeviceDpi / 96f, c);
        DrawText(g, Text, Theme.Base, ClientRectangle, !Enabled ? Theme.Dim : Primary ? Color.White : Danger ? Color.FromArgb(255, 150, 150) : Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool click = _down && ClientRectangle.Contains(e.Location) && Enabled;
        _down = false; Invalidate();
        if (click) Pressed?.Invoke();
    }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
}

/// <summary>Kartın içinde küçük, açık gri yazı (açıklama satırı ya da satır etiketi).</summary>
internal sealed class HintLabel : Smooth
{
    public Color Color { get; set; } = Theme.Sub;
    public bool Center { get; set; }
    public HintLabel() { BackColor = Theme.Card; Height = 18; Font = Theme.Small; }
    public void Set(string t) { if (Text != t) { Text = t; Invalidate(); } }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor);
        DrawText(g, Text, Font, ClientRectangle, Color, Center ? TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter : TextFormatFlags.WordBreak);
    }
}
