using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ClaudeUsage;

/// <summary>
/// Reads claude.ai through a hidden built-in browser (Microsoft Edge WebView2), using the
/// same sign-in as the "Sign in to Claude" window. claude.ai's security screening treats
/// it as a normal browser visit, which plain app requests on Windows don't pass.
/// </summary>
internal sealed class WebFetcher : IDisposable
{
    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>One browser profile, shared with the sign-in window.</summary>
    public static Task<CoreWebView2Environment> SharedEnvironment() =>
        _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Folder, "WebView2"));

    public static void ResetEnvironment() => _environment = null;

    private Form? _host;
    private CoreWebView2Controller? _controller;
    private readonly Dictionary<string, TaskCompletionSource<(int, string)>> _pending = new();

    private async Task<CoreWebView2> CoreAsync()
    {
        if (_controller != null) return _controller.CoreWebView2;

        CoreWebView2Environment env;
        try
        {
            env = await SharedEnvironment();
        }
        catch (Exception e)
        {
            ResetEnvironment();
            throw new FetchException(FetchError.NoBrowser, e.Message);
        }

        // An invisible window to host the hidden browser; it is never shown.
        _host ??= new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
        };
        IntPtr hwnd = _host.Handle;

        CoreWebView2Controller controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
        controller.IsVisible = false;
        CoreWebView2 core = controller.CoreWebView2;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.WebMessageReceived += OnMessage;
        _controller = controller;
        return core;
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? message;
        try { message = e.TryGetWebMessageAsString(); } catch { return; }
        if (message == null) return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(message);
            string id = doc.RootElement.GetProperty("id").GetString() ?? "";
            int status = doc.RootElement.GetProperty("status").GetInt32();
            string body = doc.RootElement.GetProperty("body").GetString() ?? "";
            if (_pending.Remove(id, out var waiting)) waiting.TrySetResult((status, body));
        }
        catch { /* not ours */ }
    }

    private static async Task<bool> NavigateAsync(CoreWebView2 core, string url)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) =>
            done.TrySetResult(e.IsSuccess || e.HttpStatusCode != 0);
        core.NavigationCompleted += OnCompleted;
        try
        {
            core.Navigate(url);
            Task finished = await Task.WhenAny(done.Task, Task.Delay(25_000));
            return finished == done.Task && done.Task.Result;
        }
        finally
        {
            core.NavigationCompleted -= OnCompleted;
        }
    }

    private static async Task<bool> OnClaudePageAsync(CoreWebView2 core)
    {
        try
        {
            string raw = await core.ExecuteScriptAsync("location.host + '|' + document.title");
            string page = JsonSerializer.Deserialize<string>(raw) ?? "";
            return page.StartsWith("claude.ai|", StringComparison.OrdinalIgnoreCase) &&
                   !page.Contains("Just a moment", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens a small claude.ai page, waiting out claude.ai's quick browser check if it shows one.</summary>
    private static async Task EnsureOnClaudeAsync(CoreWebView2 core, bool reload)
    {
        if (!reload && await OnClaudePageAsync(core)) return;
        if (!await NavigateAsync(core, "https://claude.ai/robots.txt"))
            throw new FetchException(FetchError.Network, "claude.ai didn't load");
        for (int i = 0; i < 10 && !await OnClaudePageAsync(core); i++)
            await Task.Delay(1500);
        if (!await OnClaudePageAsync(core))
            throw new FetchException(FetchError.Blocked, "claude.ai's browser check didn't finish");
    }

    /// <summary>GETs a claude.ai path (e.g. "/api/organizations"); returns the HTTP status and body.</summary>
    public async Task<(int Status, string Body)> FetchAsync(string path)
    {
        CoreWebView2 core = await CoreAsync();
        await EnsureOnClaudeAsync(core, reload: false);
        (int Status, string Body) result = await RunFetchAsync(core, path);
        if ((result.Status == 403 || result.Status == 503) && result.Body.TrimStart().StartsWith("<"))
        {
            // claude.ai asked for its browser check again: let it run, then retry once.
            await EnsureOnClaudeAsync(core, reload: true);
            result = await RunFetchAsync(core, path);
        }
        return result;
    }

    private async Task<(int, string)> RunFetchAsync(CoreWebView2 core, string path)
    {
        string id = Guid.NewGuid().ToString("N");
        var waiting = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiting;
        string script =
            "fetch(" + JsonSerializer.Serialize(path) + ", {credentials: 'include', headers: {'Accept': 'application/json'}})" +
            ".then(async r => window.chrome.webview.postMessage(JSON.stringify({id: '" + id + "', status: r.status, body: await r.text()})))" +
            ".catch(e => window.chrome.webview.postMessage(JSON.stringify({id: '" + id + "', status: -1, body: String(e)})));" +
            "0";
        await core.ExecuteScriptAsync(script);
        Task finished = await Task.WhenAny(waiting.Task, Task.Delay(25_000));
        if (finished != waiting.Task)
        {
            _pending.Remove(id);
            throw new FetchException(FetchError.Network, "claude.ai didn't answer in time");
        }
        return waiting.Task.Result;
    }

    /// <summary>True if the browser profile holds a claude.ai sign-in.</summary>
    public async Task<bool> HasSessionAsync()
    {
        CoreWebView2 core = await CoreAsync();
        List<CoreWebView2Cookie> cookies = await core.CookieManager.GetCookiesAsync("https://claude.ai");
        return cookies.Any(c => c.Name == "sessionKey" && !string.IsNullOrEmpty(c.Value));
    }

    /// <summary>Puts a saved sign-in back into the browser profile.</summary>
    public async Task RestoreSessionAsync(string key)
    {
        CoreWebView2 core = await CoreAsync();
        CoreWebView2Cookie cookie = core.CookieManager.CreateCookie("sessionKey", key, ".claude.ai", "/");
        cookie.IsSecure = true;
        cookie.Expires = DateTime.Now.AddYears(1);
        core.CookieManager.AddOrUpdateCookie(cookie);
    }

    public void Dispose()
    {
        try { _controller?.Close(); } catch { /* closing */ }
        _host?.Dispose();
    }
}
