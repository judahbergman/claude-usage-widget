using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeUsage;

/// <summary>Small preferences file in %LOCALAPPDATA%\ClaudeUsage.</summary>
internal sealed class Settings
{
    public string? OrgId { get; set; }
    public bool AutoUpdateOff { get; set; }
    public bool StartWithWindowsOff { get; set; }
    public bool TrayPromotedOnce { get; set; }
    public bool WelcomeShown { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsage");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* start fresh */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not critical */ }
    }
}

/// <summary>
/// Your claude.ai sign-in, stored on this PC only and encrypted with your Windows account
/// (Windows Data Protection), so other users and other computers can't read it.
/// </summary>
internal static class SessionStore
{
    private static string FilePath => Path.Combine(Settings.Folder, "session.dat");

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            string key = Encoding.UTF8.GetString(plain).Trim();
            return key.Length == 0 ? null : key;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string key)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            byte[] sealedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, sealedBytes);
        }
        catch { /* not critical */ }
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { /* ignore */ }
    }
}
