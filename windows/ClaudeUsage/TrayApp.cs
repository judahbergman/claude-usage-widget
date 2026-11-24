using System.Diagnostics;

namespace ClaudeUsage;

/// <summary>
/// The app: an icon in the taskbar's system tray (by the clock) showing your session usage.
/// Click it for the usage panel; right-click it for the menu.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly AppState _state = new();
    private readonly Settings _settings = Settings.Load();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly UsagePopup _popup;
    private readonly Control _ui = new();   // used to run work on the UI thread
    private readonly List<System.Windows.Forms.Timer> _timers = new();
    private SignInForm? _signIn;
    private Icon? _icon;
    private bool _loading;
    private bool _askedToSignIn;
    private int _promoteTries;

    public TrayApp(EventWaitHandle showEvent, bool justUpdated)
    {
        _ = _ui.Handle; // create it now, on the UI thread

        _menu.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        _tray = new NotifyIcon
        {
            Text = "Claude Usage",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };
        _tray.BalloonTipClicked += (_, _) => ShowPanel();
        _popup = new UsagePopup(_state, ShowMenuAt, ShowSignIn);
        UpdateUi();

        if (!_settings.StartWithWindowsOff) Startup.SetRunAtLogin(true); // keeps the path current

        // Opening the app again while it's running shows the panel.
        ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_, _) => { try { _ui.BeginInvoke(ShowPanel); } catch { /* closing */ } }, null, -1, false);

        Every(120_000, () => _ = RefreshAsync());             // usage every 2 minutes
        Every(30_000, Tick);                                  // countdown text, resets
        Every(60 * 60_000, () => _ = CheckForUpdatesAsync(false));
        Once(500, () =>
        {
            _ = RefreshAsync();
            if (!_settings.WelcomeShown || justUpdated)
            {
                _tray.ShowBalloonTip(10_000,
                    justUpdated ? $"Claude Usage updated to {Updater.CurrentVersion}" : "Claude Usage is running",
                    justUpdated ? "You're on the newest version."
                                : "Your Claude usage is in the taskbar, next to the clock. Click it for details.",
                    ToolTipIcon.Info);
                _settings.WelcomeShown = true;
                _settings.Save();
            }
        });
        Every(5_000, PromoteTrayIconOnce);
        Once(45_000, () => _ = CheckForUpdatesAsync(false, force: true));

        if (Program.SelfTest)
        {
            // Exercise the panel and the menu, so the GitHub check catches drawing errors.
            Once(3_000, () => { ShowPanel(); BuildMenu(); });
        }
    }

    // ----- timers -----

    private void Every(int ms, Action action)
    {
        var t = new System.Windows.Forms.Timer { Interval = ms };
        t.Tick += (_, _) => action();
        t.Start();
        _timers.Add(t);
    }

    private void Once(int ms, Action action)
    {
        var t = new System.Windows.Forms.Timer { Interval = ms };
        t.Tick += (_, _) => { t.Stop(); action(); };
        t.Start();
        _timers.Add(t);
    }

    private void Tick()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        if ((_state.Session?.ResetsAt is DateTimeOffset s && now > s.AddSeconds(15)) ||
            (_state.Weekly?.ResetsAt is DateTimeOffset w && now > w.AddSeconds(15)))
        {
            _ = RefreshAsync();
        }
        UpdateUi();
    }

    private void PromoteTrayIconOnce()
    {
        if (_settings.TrayPromotedOnce || _promoteTries > 12) return;
        _promoteTries++;
        if (Startup.TryShowTrayIconOnTaskbar())
        {
            _settings.TrayPromotedOnce = true;
            _settings.Save();
        }
    }

    // ----- usage -----

    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            string? key = SessionStore.Load();
            if (key == null)
            {
                NeedSignIn("Not signed in to claude.ai");
                return;
            }
            try
            {
                UsageResult r = await UsageClient.FetchAsync(key, _settings.OrgId);
                if (r.RenewedKey != null) SessionStore.Save(r.RenewedKey);
                _state.Orgs = r.Orgs;
                _state.CurrentOrgId = r.Org.Id;
                _state.OrgName = r.Orgs.Count > 1 ? r.Org.Name : null;
                _state.Session = r.Session;
                _state.Weekly = r.Weekly;
                _state.LastUpdated = DateTime.Now;
                _state.Status = null;
                _state.Detail = null;
                _state.NeedsSignIn = false;
            }
            catch (FetchException e)
            {
                switch (e.Kind)
                {
                    case FetchError.Unauthorized: NeedSignIn("Your claude.ai sign-in expired"); break;
                    case FetchError.Network: Problem("Offline", "Can't reach claude.ai right now"); break;
                    case FetchError.Blocked: Problem("Busy", "claude.ai is busy, trying again soon"); break;
                    default: Problem("Error", "Couldn't read usage, trying again soon"); break;
                }
            }
        }
        finally
        {
            _loading = false;
            UpdateUi();
        }
    }

    private void Problem(string status, string detail)
    {
        _state.Status = status;
        _state.Detail = detail;
    }

    private void NeedSignIn(string detail)
    {
        _state.NeedsSignIn = true;
        _state.OrgName = null;
        Problem("Click to sign in", detail);
        if (!_askedToSignIn && !Program.SelfTest)
        {
            _askedToSignIn = true;
            ShowSignIn();
        }
    }

    private void UpdateUi()
    {
        bool problem = _state.Status != null || _state.Session == null;
        int size = SystemInformation.SmallIconSize.Width;
        Icon next = TrayIconRenderer.Render(_state.Session?.Utilization, problem, size);
        _tray.Icon = next;
        _icon?.Dispose();
        _icon = next;

        string tip;
        if (_state.NeedsSignIn) tip = "Claude Usage\nClick to sign in";
        else if (_state.Status != null && _state.Session == null) tip = $"Claude Usage\n{_state.Detail}";
        else tip = $"Claude Usage\nSession {Fmt.Percent(_state.Session)} · {Fmt.Reset(_state.Session, false)}\n" +
                   $"Weekly {Fmt.Percent(_state.Weekly)} · {Fmt.Reset(_state.Weekly, false)}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;

        if (_popup.Visible) _popup.Invalidate();
    }

    // ----- panel and menu -----

    private void TogglePanel()
    {
        if (_popup.Visible) { _popup.Hide(); return; }
        // Clicking the icon while the panel is open first closes it (it loses focus); don't reopen.
        if ((DateTime.Now - _popup.HiddenAt).TotalMilliseconds < 300) return;
        ShowPanel();
    }

    private void ShowPanel()
    {
        if (_state.NeedsSignIn && !Program.SelfTest)
        {
            ShowSignIn();
            return;
        }
        _popup.ShowNearTray();
    }

    private void ShowMenuAt(Point screenPoint) => _menu.Show(screenPoint);

    private void BuildMenu()
    {
        _menu.Items.Clear();

        string? statusLine = _state.Detail ?? (_state.LastUpdated is DateTime t ? $"Updated {t:t}" : null);
        if (statusLine != null) _menu.Items.Add(new ToolStripMenuItem(statusLine) { Enabled = false });
        if (_state.NeedsSignIn) Add("Sign In to Claude…", ShowSignIn);
        if (_state.UpdateAvailable is string v && !_state.Updating) Add($"Update to Version {v} Now", () => _ = InstallUpdateAsync(true));
        if (_menu.Items.Count > 0) _menu.Items.Add(new ToolStripSeparator());

        Add("Show Usage", ShowPanel);
        Add("Refresh Now", () => _ = RefreshAsync());
        Add("Open Usage Page", () => OpenUrl("https://claude.ai/settings/usage"));

        if (_state.Orgs.Count > 1)
        {
            var account = new ToolStripMenuItem("Account");
            foreach (Org org in _state.Orgs)
            {
                var item = new ToolStripMenuItem(org.Name) { Checked = org.Id == _state.CurrentOrgId };
                string id = org.Id;
                item.Click += (_, _) => { _settings.OrgId = id; _settings.Save(); _ = RefreshAsync(); };
                account.DropDownItems.Add(item);
            }
            _menu.Items.Add(account);
        }

        _menu.Items.Add(new ToolStripSeparator());
        if (!_state.NeedsSignIn) Add("Sign In with Another Account…", ShowSignIn);
        var startup = Add("Start with Windows", () =>
        {
            bool on = !Startup.IsRunAtLogin();
            Startup.SetRunAtLogin(on);
            _settings.StartWithWindowsOff = !on;
            _settings.Save();
        });
        startup.Checked = Startup.IsRunAtLogin();

        _menu.Items.Add(new ToolStripSeparator());
        Add("Check for Updates…", () => _ = CheckForUpdatesAsync(true));
        var auto = Add("Install Updates Automatically", () => { _settings.AutoUpdateOff = !_settings.AutoUpdateOff; _settings.Save(); });
        auto.Checked = !_settings.AutoUpdateOff;
        _menu.Items.Add(new ToolStripMenuItem($"Version {Updater.CurrentVersion}") { Enabled = false });

        _menu.Items.Add(new ToolStripSeparator());
        Add("Uninstall Claude Usage…", Uninstall);
        Add("Quit Claude Usage", Quit);
    }

    private ToolStripMenuItem Add(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        _menu.Items.Add(item);
        return item;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* ignore */ }
    }

    // ----- sign in -----

    private void ShowSignIn()
    {
        if (_signIn is { IsDisposed: false })
        {
            _signIn.Activate();
            return;
        }
        _popup.Hide();
        _signIn = new SignInForm();
        _signIn.SignedIn += key =>
        {
            SessionStore.Save(key);
            _ = RefreshAsync();
        };
        _signIn.FormClosed += (_, _) => _signIn = null;
        _signIn.Show();
        _signIn.Activate();
    }

    // ----- updates -----

    private async Task CheckForUpdatesAsync(bool userAsked, bool force = false)
    {
        if (!userAsked && !force && _settings.LastUpdateCheckUtc is DateTime last &&
            DateTime.UtcNow - last < TimeSpan.FromHours(12))
        {
            return;
        }
        string? latest = await Updater.LatestVersionAsync();
        if (latest == null)
        {
            if (userAsked)
                MessageBox.Show("GitHub couldn't be reached. Try again later.", "Couldn't check for updates",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _settings.LastUpdateCheckUtc = DateTime.UtcNow;
        _settings.Save();

        if (Updater.IsNewer(latest, Updater.CurrentVersion))
        {
            _state.UpdateAvailable = latest;
            UpdateUi();
            if (userAsked || !_settings.AutoUpdateOff) await InstallUpdateAsync(userAsked);
        }
        else
        {
            _state.UpdateAvailable = null;
            if (userAsked)
                MessageBox.Show($"Claude Usage {Updater.CurrentVersion} is the newest version.", "You're up to date",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private async Task InstallUpdateAsync(bool userAsked)
    {
        if (_state.Updating) return;
        _state.Updating = true;
        UpdateUi();
        try
        {
            await Updater.DownloadAndInstallAsync();
            _tray.Visible = false;
            Updater.RelaunchAndExit();
        }
        catch (Exception)
        {
            _state.Updating = false;
            UpdateUi();
            if (userAsked)
                MessageBox.Show("The update didn't finish. It will try again later.", "Update didn't finish",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ----- quit / uninstall -----

    private void Uninstall()
    {
        DialogResult answer = MessageBox.Show(
            "This removes Claude Usage from this computer, including your sign-in and settings.",
            "Uninstall Claude Usage?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;
        _tray.Visible = false;
        Startup.UninstallAndExit();
    }

    private void Quit()
    {
        _tray.Visible = false;
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        foreach (var t in _timers) t.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _icon?.Dispose();
        base.ExitThreadCore();
    }
}
