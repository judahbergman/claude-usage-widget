using System.Diagnostics;
using Microsoft.Win32;

namespace ClaudeUsage;

/// <summary>
/// Double-click install: when the app is opened from anywhere other than its install folder
/// (for example straight from Downloads), it installs itself and starts the installed copy.
/// </summary>
internal static class Installer
{
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ClaudeUsage");

    public static string InstallPath => Path.Combine(InstallDir, "ClaudeUsage.exe");

    public static bool ShouldInstall()
    {
        string? me = Environment.ProcessPath;
        if (me == null) return false;
        if (string.Equals(Path.GetFileName(me), "ClaudeUsage.old.exe", StringComparison.OrdinalIgnoreCase)) return false;
        return !string.Equals(Path.GetFullPath(me), Path.GetFullPath(InstallPath), StringComparison.OrdinalIgnoreCase);
    }

    public static void Run()
    {
        ApplicationConfiguration.Initialize();

        bool reinstall = File.Exists(Path.Combine(Settings.Folder, "session.dat"));
        string message = reinstall
            ? "This updates Claude Usage on this computer to the newest version.\n\nClick OK to continue."
            : "Claude Usage shows your Claude session and weekly usage in the taskbar, next to the clock. " +
              "It starts with Windows and keeps itself up to date.\n\n" +
              "NEXT: a \"Sign in to Claude\" window opens. Sign in with your own Claude account.\n\n" +
              "If you sign in with EMAIL:\n" +
              "   1. Type your email and continue.\n" +
              "   2. Open the email from Claude. DON'T click the link in it.\n" +
              "   3. Right-click the link and choose \"Copy link\".\n" +
              "   4. In the sign-in window, click \"Paste Link\" at the top.\n\n" +
              "Click OK to install.";
        if (MessageBox.Show(message, "Install Claude Usage", MessageBoxButtons.OKCancel, MessageBoxIcon.Information)
            != DialogResult.OK)
        {
            return;
        }

        try
        {
            // Close a copy that's already running, so it can be replaced.
            foreach (Process p in Process.GetProcessesByName("ClaudeUsage"))
            {
                if (p.Id == Environment.ProcessId) continue;
                try { p.Kill(); p.WaitForExit(5000); } catch { /* already closing */ }
            }

            Directory.CreateDirectory(InstallDir);
            if (File.Exists(InstallPath))
            {
                try { File.Delete(InstallPath); }
                catch { File.Move(InstallPath, Path.Combine(InstallDir, "ClaudeUsage.old.exe"), overwrite: true); }
            }
            File.Copy(Environment.ProcessPath!, InstallPath, overwrite: true);
            try { File.Delete(InstallPath + ":Zone.Identifier"); } catch { /* not marked as downloaded */ }

            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                run.SetValue("ClaudeUsage", $"\"{InstallPath}\"");
            }
            CreateStartMenuShortcut();

            Process.Start(new ProcessStartInfo(InstallPath) { UseShellExecute = false, WorkingDirectory = InstallDir });
        }
        catch (Exception e)
        {
            MessageBox.Show($"Claude Usage couldn't be installed:\n\n{e.Message}\n\nPlease try again, or ask for help.",
                "Install Claude Usage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void CreateStartMenuShortcut()
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Claude Usage.lnk");
            dynamic shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = InstallPath;
            shortcut.WorkingDirectory = InstallDir;
            shortcut.Description = "Claude Usage";
            shortcut.Save();
        }
        catch { /* the app still works without it */ }
    }
}
