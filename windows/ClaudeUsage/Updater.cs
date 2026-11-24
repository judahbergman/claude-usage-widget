using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace ClaudeUsage;

/// <summary>New versions are published on GitHub; this checks for them and installs them.</summary>
internal static class Updater
{
    private const string Repo = "JoolBaby/claude-usage-widget";
    private const string AssetName = "ClaudeUsage-Windows.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ClaudeUsageWidget-Windows/" + CurrentVersion);
        return c;
    }

    public static string CurrentVersion
    {
        get
        {
            Version? v = Assembly.GetEntryAssembly()?.GetName().Version;
            return v == null ? "0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    private static string ExeDir => Path.GetDirectoryName(ExePath) ?? ".";
    private static string OldPath => Path.Combine(ExeDir, "ClaudeUsage.old.exe");
    private static string NewPath => Path.Combine(ExeDir, "ClaudeUsage.new.exe");

    public static bool IsNewer(string a, string b)
    {
        int[] pa = a.Split('.').Select(x => int.TryParse(x, out int n) ? n : 0).ToArray();
        int[] pb = b.Split('.').Select(x => int.TryParse(x, out int n) ? n : 0).ToArray();
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0;
            int y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    /// <summary>The newest published version, or null if GitHub couldn't be reached.</summary>
    public static async Task<string?> LatestVersionAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using HttpResponseMessage resp = await Http.SendAsync(req).ConfigureAwait(true);
            if (!resp.IsSuccessStatusCode) return null;
            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(true));
            if (!doc.RootElement.TryGetProperty("tag_name", out JsonElement tag) || tag.ValueKind != JsonValueKind.String)
                return null;
            string t = tag.GetString() ?? "";
            return t.StartsWith('v') ? t[1..] : t;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the newest version and puts it in place of this one. Windows lets a running
    /// program be renamed (not deleted), so the old one is renamed aside and removed next start.
    /// </summary>
    public static async Task DownloadAndInstallAsync()
    {
        string url = $"https://github.com/{Repo}/releases/latest/download/{AssetName}";
        using (HttpResponseMessage resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(true))
        {
            resp.EnsureSuccessStatusCode();
            await using FileStream fs = File.Create(NewPath);
            await resp.Content.CopyToAsync(fs).ConfigureAwait(true);
        }
        if (new FileInfo(NewPath).Length < 1_000_000)
        {
            try { File.Delete(NewPath); } catch { /* ignore */ }
            throw new InvalidOperationException("The download didn't look right.");
        }

        try { if (File.Exists(OldPath)) File.Delete(OldPath); } catch { /* still in use; fine */ }
        File.Move(ExePath, OldPath, overwrite: true);
        try
        {
            File.Move(NewPath, ExePath);
        }
        catch
        {
            File.Move(OldPath, ExePath, overwrite: true);
            throw;
        }
    }

    /// <summary>Starts the new version (which waits for this one to quit), then quits.</summary>
    public static void RelaunchAndExit()
    {
        var psi = new ProcessStartInfo(ExePath, $"--wait-for {Environment.ProcessId} --updated")
        {
            UseShellExecute = false,
            WorkingDirectory = ExeDir,
        };
        try { Process.Start(psi); } catch { /* the Run key starts it at next sign-in anyway */ }
        Environment.Exit(0);
    }

    public static void CleanUpOldVersion()
    {
        try { if (File.Exists(OldPath)) File.Delete(OldPath); } catch { /* try again next time */ }
        try { if (File.Exists(NewPath)) File.Delete(NewPath); } catch { /* ignore */ }
    }
}
