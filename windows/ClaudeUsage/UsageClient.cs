using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeUsage;

internal sealed record Bucket(double Utilization, DateTimeOffset? ResetsAt);

internal sealed record Org(string Id, string Name, int Score);

internal enum FetchError { Unauthorized, Blocked, Network, BadData }

internal sealed class FetchException : Exception
{
    public FetchError Kind { get; }

    public FetchException(FetchError kind, string? message = null) : base(message ?? kind.ToString())
    {
        Kind = kind;
    }
}

internal sealed class UsageResult
{
    public List<Org> Orgs { get; init; } = new();
    public required Org Org { get; init; }
    public Bucket? Session { get; init; }
    public Bucket? Weekly { get; init; }
    public string? RenewedKey { get; init; }
}

/// <summary>Reads your usage from claude.ai, the same numbers as Settings → Usage.</summary>
internal static class UsageClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36 Edg/142.0.0.0";

    private static async Task<(JsonDocument Doc, string? Renewed)> GetAsync(string path, string key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://claude.ai" + path);
        req.Headers.TryAddWithoutValidation("Cookie", "sessionKey=" + key);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            throw new FetchException(FetchError.Network, e.Message);
        }

        using (resp)
        {
            int code = (int)resp.StatusCode;
            string type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (code == 429 || ((code == 403 || code == 503) && type.Contains("html")))
                throw new FetchException(FetchError.Blocked);
            if (code == 401 || code == 403)
                throw new FetchException(FetchError.Unauthorized);
            if (code != 200)
                throw new FetchException(FetchError.BadData);

            string? renewed = null;
            if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (string c in cookies)
                {
                    if (!c.StartsWith("sessionKey=", StringComparison.Ordinal)) continue;
                    string value = c.Substring("sessionKey=".Length).Split(';')[0].Trim();
                    if (value.Length > 0 && value != key) renewed = value;
                }
            }

            string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);
            try
            {
                return (JsonDocument.Parse(text), renewed);
            }
            catch
            {
                throw new FetchException(FetchError.BadData);
            }
        }
    }

    public static async Task<UsageResult> FetchAsync(string key, string? preferredOrgId)
    {
        var (orgDoc, renewed1) = await GetAsync("/api/organizations", key).ConfigureAwait(true);
        List<Org> orgs = new();
        using (orgDoc)
        {
            if (orgDoc.RootElement.ValueKind != JsonValueKind.Array)
                throw new FetchException(FetchError.BadData);
            foreach (JsonElement o in orgDoc.RootElement.EnumerateArray())
            {
                string? id = Str(o, "uuid");
                if (id == null) continue;
                string name = Str(o, "display_name") is { Length: > 0 } display ? display : (Str(o, "name") ?? "Account");
                var caps = new List<string>();
                if (o.TryGetProperty("capabilities", out JsonElement c) && c.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement x in c.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.String) caps.Add(x.GetString()!);
                }
                int score = Str(o, "raven_type") != null ? 3       // Team / Enterprise
                          : caps.Contains("claude_max") ? 2
                          : caps.Contains("claude_pro") ? 1
                          : 0;
                orgs.Add(new Org(id, name, score));
            }
        }
        if (orgs.Count == 0) throw new FetchException(FetchError.BadData);
        Org org = orgs.FirstOrDefault(x => x.Id == preferredOrgId) ?? orgs.OrderByDescending(x => x.Score).First();

        var (usageDoc, renewed2) = await GetAsync($"/api/organizations/{org.Id}/usage", key).ConfigureAwait(true);
        using (usageDoc)
        {
            JsonElement root = usageDoc.RootElement;
            return new UsageResult
            {
                Orgs = orgs,
                Org = org,
                Session = ParseBucket(root, "five_hour"),
                Weekly = ParseBucket(root, "seven_day"),
                RenewedKey = renewed2 ?? renewed1,
            };
        }
    }

    private static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static Bucket? ParseBucket(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement b) || b.ValueKind != JsonValueKind.Object)
            return null;
        double used = b.TryGetProperty("utilization", out JsonElement u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
        DateTimeOffset? resets = null;
        if (Str(b, "resets_at") is string s &&
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset dt))
        {
            resets = dt;
        }
        return new Bucket(used, resets);
    }
}

/// <summary>Text and colors shared by the tray icon and the usage panel.</summary>
internal static class Fmt
{
    public static string Percent(Bucket? b) =>
        b == null ? "—" : $"{Math.Round(b.Utilization, MidpointRounding.AwayFromZero):0}%";

    public static string Reset(Bucket? b, bool countdown)
    {
        if (b?.ResetsAt is not DateTimeOffset r) return "Not started";
        DateTime local = r.ToLocalTime().DateTime;
        DateTime now = DateTime.Now;
        string time = local.ToString("t", CultureInfo.CurrentCulture);
        string text = local.Date == now.Date ? $"Resets {time}"
                    : local.Date == now.Date.AddDays(1) ? $"Resets tomorrow {time}"
                    : $"Resets {local.ToString("ddd", CultureInfo.CurrentCulture)} {time}";
        if (countdown)
        {
            TimeSpan left = local - now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            int h = (int)left.TotalHours;
            int m = left.Minutes;
            text += h > 0 ? $" · in {h}h {m}m" : $" · in {m}m";
        }
        return text;
    }

    /// <summary>Green while there's plenty left, orange from 50%, red from 80%.</summary>
    public static Color ColorFor(double used) =>
        used >= 80 ? Color.FromArgb(245, 84, 77)
      : used >= 50 ? Color.FromArgb(250, 158, 51)
      : Color.FromArgb(77, 204, 115);
}
