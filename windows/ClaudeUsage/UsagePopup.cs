using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClaudeUsage;

/// <summary>What the app currently knows; shared by the tray icon, the panel and the menu.</summary>
internal sealed class AppState
{
    public Bucket? Session { get; set; }
    public Bucket? Weekly { get; set; }
    public string? Status { get; set; }      // short, shown in the panel header (e.g. "Click to sign in")
    public string? Detail { get; set; }      // one line, shown at the top of the menu
    public bool NeedsSignIn { get; set; }
    public List<Org> Orgs { get; set; } = new();
    public string? CurrentOrgId { get; set; }
    public string? OrgName { get; set; }
    public DateTime? LastUpdated { get; set; }
    public string? UpdateAvailable { get; set; }
    public bool Updating { get; set; }
}

/// <summary>The small panel that opens above the taskbar when you click the tray icon.</summary>
internal sealed class UsagePopup : Form
{
    private readonly AppState _state;
    private readonly Action<Point> _showMenu;
    private readonly Action _signIn;
    public DateTime HiddenAt { get; private set; } = DateTime.MinValue;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static readonly Color Back = Color.FromArgb(32, 33, 38);
    private static readonly Color Secondary = Color.FromArgb(160, 161, 170);
    private static readonly Color Track = Color.FromArgb(62, 63, 72);
    private static readonly Color Orange = Color.FromArgb(250, 158, 51);
    private static readonly Color Blue = Color.FromArgb(96, 156, 250);

    public UsagePopup(AppState state, Action<Point> showMenu, Action signIn)
    {
        _state = state;
        _showMenu = showMenu;
        _signIn = signIn;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Back;
        ForeColor = Color.White;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "Claude Usage";

        Deactivate += (_, _) => Hide();
        VisibleChanged += (_, _) => { if (!Visible) HiddenAt = DateTime.Now; };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right || MenuButton.Contains(e.Location))
                _showMenu(PointToScreen(e.Location));
            else if (_state.NeedsSignIn)
                _signIn();
        };
    }

    private float Scale1 => DeviceDpi / 96f;
    private int Px(float v) => (int)Math.Round(v * Scale1);
    private Rectangle MenuButton => new(Width - Px(38), Px(8), Px(30), Px(24));

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: no taskbar button, no Alt+Tab
            cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND: rounded corners on Windows 11 (ignored on Windows 10)
        try { DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { /* older Windows */ }
    }

    /// <summary>Opens the panel just above the taskbar, near the clock.</summary>
    public void ShowNearTray()
    {
        Size = new Size(Px(292), Px(182));
        Screen screen = Screen.FromPoint(Cursor.Position);
        Rectangle wa = screen.WorkingArea;
        Rectangle all = screen.Bounds;
        int m = Px(12);
        int x = wa.Right - Width - m;
        int y = wa.Bottom - Height - m;
        if (wa.Top > all.Top) y = wa.Top + m;     // taskbar at the top
        if (wa.Left > all.Left) x = wa.Left + m;  // taskbar on the left
        Location = new Point(x, y);
        Show();
        Activate();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var titleFont = new Font("Segoe UI Semibold", 9f);
        using var statusFont = new Font("Segoe UI Semibold", 8.25f);
        using var labelFont = new Font("Segoe UI", 9.75f);
        using var pctFont = new Font("Segoe UI Semibold", 10.5f);
        using var smallFont = new Font("Segoe UI", 8.25f);

        int pad = Px(16);
        int w = Width - pad * 2;
        int y = Px(12);

        string title = _state.OrgName != null ? $"Claude · {_state.OrgName}" : "Claude Usage";
        string? right = _state.Status ?? (_state.Updating ? "Updating…" : _state.UpdateAvailable != null ? "Update ready" : null);
        int titleWidth = right == null ? MenuButton.Left - pad : (MenuButton.Left - pad) / 2;
        TextRenderer.DrawText(g, title, titleFont, new Rectangle(pad, y, titleWidth, Px(22)), Secondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (right != null)
        {
            var rc = new Rectangle(pad + titleWidth, y, MenuButton.Left - pad - titleWidth - Px(2), Px(22));
            TextRenderer.DrawText(g, right, statusFont, rc, _state.Status != null ? Orange : Blue,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        TextRenderer.DrawText(g, "•••", statusFont, MenuButton, Secondary,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        y += Px(34);

        DrawRow(g, "Session", _state.Session, true, ref y, pad, w, labelFont, pctFont, smallFont);
        y += Px(8);
        DrawRow(g, "Weekly", _state.Weekly, false, ref y, pad, w, labelFont, pctFont, smallFont);
    }

    private void DrawRow(Graphics g, string title, Bucket? b, bool countdown, ref int y, int pad, int w,
                         Font labelFont, Font pctFont, Font smallFont)
    {
        bool dim = _state.Status != null;
        Color textColor = dim ? Secondary : Color.White;
        Color barColor = b == null ? Secondary : Fmt.ColorFor(b.Utilization);

        TextRenderer.DrawText(g, title, labelFont, new Rectangle(pad, y, w / 2, Px(22)), textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, Fmt.Percent(b), pctFont, new Rectangle(pad + w / 2, y, w / 2, Px(22)),
            dim ? Secondary : barColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        y += Px(26);

        FillRounded(g, new Rectangle(pad, y, w, Px(6)), Track);
        if (b != null && b.Utilization > 0)
        {
            int filled = Math.Max(Px(6), (int)(w * Math.Min(b.Utilization, 100) / 100.0));
            FillRounded(g, new Rectangle(pad, y, filled, Px(6)), dim ? Secondary : barColor);
        }
        y += Px(11);

        TextRenderer.DrawText(g, Fmt.Reset(b, countdown), smallFont, new Rectangle(pad, y, w, Px(18)), Secondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        y += Px(20);
    }

    internal static void FillRounded(Graphics g, Rectangle r, Color color)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = RoundedPath(r, Math.Min(r.Height, r.Width) / 2f);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    internal static GraphicsPath RoundedPath(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Max(1f, radius * 2);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Draws the tray icon: your session percentage on a green, orange or red tile.</summary>
internal static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Render(double? percent, bool problem, int size)
    {
        size = Math.Max(16, size);
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            Color tile = problem || percent == null ? Color.FromArgb(128, 129, 138) : Fmt.ColorFor(percent.Value);
            using (GraphicsPath path = UsagePopup.RoundedPath(new RectangleF(0, 0, size - 1, size - 1), size * 0.22f))
            using (var brush = new SolidBrush(tile))
            {
                g.FillPath(brush, path);
            }

            string text = problem || percent == null ? "!" : Math.Min(999, (int)Math.Round(percent.Value)).ToString();
            float fontSize = size * 0.62f;
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            Font font = new("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            while (fontSize > 6 && g.MeasureString(text, font, size * 4, format).Width > size * 1.02f)
            {
                font.Dispose();
                fontSize -= 0.5f;
                font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            using (font)
            using (var white = new SolidBrush(Color.White))
            {
                g.DrawString(text, font, white, new RectangleF(0, size * 0.03f, size, size), format);
            }
        }
        IntPtr handle = bmp.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
