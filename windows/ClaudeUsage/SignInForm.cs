using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeUsage;

/// <summary>A small built-in browser window showing claude.ai, used to sign in once.</summary>
internal sealed class SignInForm : Form
{
    private readonly WebView2 _web = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private bool _finished;

    /// <summary>Raised with the new session key when sign-in completes.</summary>
    public event Action<string>? SignedIn;

    public SignInForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Sign in to Claude";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(500, 780);
        MinimumSize = new Size(420, 560);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { /* default icon */ }

        // Bar on top for signing in by email: paste the link from the email here, so it
        // opens inside this window instead of in your web browser.
        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 76,
            Padding = new Padding(14, 10, 14, 10),
            BackColor = Color.FromArgb(255, 244, 206),
        };
        var label = new Label
        {
            Text = "Signing in with email?  Don't click the link in the email.\n" +
                   "Right-click it, choose \"Copy link\", then click Paste Link.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(60, 45, 10),
            Font = new Font("Segoe UI Semibold", 9.75f),
        };
        var paste = new Button
        {
            Text = "Paste Link",
            Dock = DockStyle.Right,
            Width = 110,
            BackColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.75f),
        };
        paste.Click += (_, _) => PasteLink();
        bar.Controls.Add(label);   // docked last: fills what's left
        bar.Controls.Add(paste);   // docked first: right edge

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);        // fills below the bar
        Controls.Add(bar);
        ResumeLayout(false);

        Load += async (_, _) => await InitAsync();
        _timer.Tick += async (_, _) => await CheckSignedInAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            CoreWebView2Environment env = await WebFetcher.SharedEnvironment();
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception)
        {
            WebFetcher.ResetEnvironment();
            MessageBox.Show(this,
                "Signing in needs Microsoft Edge WebView2, a free part of Windows that's missing on this PC.\n\n" +
                "Click OK to open Microsoft's download page. Install it, then choose Sign In to Claude again.",
                "One more thing is needed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true }); }
            catch { /* ignore */ }
            Close();
            return;
        }

        CoreWebView2 core = _web.CoreWebView2;
        // Start from a clean slate so an old, expired sign-in can't be mistaken for a new one.
        foreach (CoreWebView2Cookie c in await core.CookieManager.GetCookiesAsync("https://claude.ai"))
        {
            if (c.Name == "sessionKey") core.CookieManager.DeleteCookie(c);
        }
        core.NavigationCompleted += async (_, _) => await CheckSignedInAsync();
        core.Navigate("https://claude.ai/login");
        _timer.Start();
    }

    private async Task CheckSignedInAsync()
    {
        if (_finished || IsDisposed || _web.CoreWebView2 == null) return;
        if (!Uri.TryCreate(_web.CoreWebView2.Source, UriKind.Absolute, out Uri? uri)) return;
        if (!uri.Host.EndsWith("claude.ai", StringComparison.OrdinalIgnoreCase)) return;
        string path = uri.AbsolutePath;
        if (path.StartsWith("/login") || path.StartsWith("/magic-link") || path.StartsWith("/logout")) return;

        List<CoreWebView2Cookie> cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://claude.ai");
        string? key = cookies.FirstOrDefault(c => c.Name == "sessionKey")?.Value;
        if (string.IsNullOrEmpty(key) || _finished) return;

        _finished = true;
        _timer.Stop();
        SignedIn?.Invoke(key);
        Close();
    }

    private void PasteLink()
    {
        string text = "";
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText().Trim(); } catch { /* ignore */ }

        string[] allowed = { "claude.ai", "claude.com", "anthropic.com" };
        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps &&
            allowed.Any(d => uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                             uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))
        {
            _web.CoreWebView2?.Navigate(uri.ToString());
            return;
        }
        MessageBox.Show(this,
            "In the sign-in email from Claude, right-click the sign-in button (or link) and choose Copy link. " +
            "Then come back here and click Paste Link.\n\nDon't click the link itself, because that opens it in your web browser instead.",
            "Copy the sign-in link first", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _web.Dispose();
        base.OnFormClosed(e);
    }
}
