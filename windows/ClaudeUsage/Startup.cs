using System.Diagnostics;
using Microsoft.Win32;

namespace ClaudeUsage;

/// <summary>Start with Windows, the taskbar icon, and uninstalling.</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "ClaudeUsage";

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsRunAtLogin()
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void SetRunAtLogin(bool on)
    {
        try
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(RunName, $"\"{ExePath}\"");
            else k.DeleteValue(RunName, false);
        }
        catch { /* not critical */ }
    }

    /// <summary>
    /// Windows 11 hides new tray icons behind the "^" arrow. This shows ours on the taskbar
    /// once; if you hide it again later, it stays hidden.
    /// </summary>
    public static bool TryShowTrayIconOnTaskbar()
    {
        try
        {
            const string root = @"Control Panel\NotifyIconSettings";
            using RegistryKey? list = Registry.CurrentUser.OpenSubKey(root);
            if (list == null) return false;
            string exeName = Path.GetFileName(ExePath);
            foreach (string sub in list.GetSubKeyNames())
            {
                using RegistryKey? k = Registry.CurrentUser.OpenSubKey($@"{root}\{sub}", writable: true);
                if (k?.GetValue("ExecutablePath") is string path &&
                    string.Equals(Path.GetFileName(path), exeName, StringComparison.OrdinalIgnoreCase))
                {
                    k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                    return true;
                }
            }
        }
        catch { /* ignore */ }
        return false;
    }

    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Claude Usage.lnk");

    /// <summary>Removes the app, its Start menu shortcut, its sign-in and its settings.</summary>
    public static void UninstallAndExit()
    {
        SetRunAtLogin(false);
        try { File.Delete(ShortcutPath); } catch { /* ignore */ }

        string exe = ExePath;
        string exeDir = Path.GetDirectoryName(exe) ?? "";
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        bool ownFolder = string.Equals(Path.GetFileName(exeDir), "ClaudeUsage", StringComparison.OrdinalIgnoreCase) &&
                         exeDir.StartsWith(programs, StringComparison.OrdinalIgnoreCase);

        // Only ever delete our own folders; if the app was run from somewhere else, delete just the .exe.
        string removeApp = ownFolder ? $"rmdir /s /q \"{exeDir}\"" : $"del /f /q \"{exe}\"";
        string removeData = $"rmdir /s /q \"{Settings.Folder}\"";
        var cleanup = new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & {removeApp} & {removeData}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try { Process.Start(cleanup); } catch { /* ignore */ }
        Environment.Exit(0);
    }
}
