using System.Globalization;
using System.Text.Json;

namespace ClaudeUsage;

internal sealed record Bucket(double Utilization, DateTimeOffset? ResetsAt);

internal sealed record Org(string Id, string Name, int Score);

internal enum FetchError { Unauthorized, Blocked, Network, BadData, NoBrowser }

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
}

/// <summary>Reads your usage from claude.ai, the same numbers as Settings → Usage.</summary>
internal static class UsageClient
{
    private static async Task<JsonDocument> GetJsonAsync(WebFetcher web, string path)
    {
        (int status, string body) = await web.FetchAsync(path);
        bool html = body.TrimStart().StartsWith("<");
        if (status < 0) throw new FetchException(FetchError.Network, body);
        if (status == 429 || ((status == 403 || status == 503) && html)) throw new FetchException(FetchError.Blocked);
        if (status == 401 || status == 403) throw new FetchException(FetchError.Unauthorized);
        if (status != 200) throw new FetchException(FetchError.BadData, $"HTTP {status}");
        try
        {
            return JsonDocument.Parse(body);
        }
        catch
        {
            throw new FetchException(FetchError.BadData);
        }
    }

    public static async Task<UsageResult> FetchAsync(WebFetcher web, string? preferredOrgId)
    {
        JsonDocument orgDoc = await GetJsonAsync(web, "/api/organizations");
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

        JsonDocument usageDoc = await GetJsonAsync(web, $"/api/organizations/{org.Id}/usage");
        using (usageDoc)
        {
            JsonElement root = usageDoc.RootElement;
            return new UsageResult
            {
                Orgs = orgs,
                Org = org,
                Session = ParseBucket(root, "five_hour"),
                Weekly = ParseBucket(root, "seven_day"),
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
