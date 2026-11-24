using System.Diagnostics;

namespace ClaudeUsage;

internal static class Program
{
    private const string MutexName = @"Local\JoolBaby-ClaudeUsage-Instance";
    internal const string ShowEventName = @"Local\JoolBaby-ClaudeUsage-Show";

    /// <summary>Set by the GitHub build check: any error quits the app so the check fails.</summary>
    internal static readonly bool SelfTest =
        Environment.GetEnvironmentVariable("CLAUDE_USAGE_SELFTEST") == "1";

    [STAThread]
    private static void Main(string[] args)
    {
        // After an update, the new version waits for the old one to quit first.
        int waitIndex = Array.IndexOf(args, "--wait-for");
        if (waitIndex >= 0 && waitIndex + 1 < args.Length && int.TryParse(args[waitIndex + 1], out int oldPid))
        {
            try { Process.GetProcessById(oldPid).WaitForExit(15000); } catch { /* already gone */ }
        }
        Updater.CleanUpOldVersion();

        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running: ask that copy to show its usage panel, then quit.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch { /* ignore */ }
            return;
        }
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => HandleError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HandleError(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(showEvent, args.Contains("--updated")));
        GC.KeepAlive(mutex);
    }

    private static void HandleError(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path.Combine(Settings.Folder, "errors.log"),
                $"{DateTime.Now:u} {ex}{Environment.NewLine}");
        }
        catch { /* ignore */ }
        if (SelfTest) Environment.Exit(2);
        // Otherwise keep running: a single failed refresh shouldn't close the app.
    }
}
