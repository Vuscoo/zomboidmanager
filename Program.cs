using System.Globalization;

namespace ZomboidManager;

static class Program
{
    [STAThread]
    static void Main()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Any(a => string.Equals(a, "--selftest-mod-updates", StringComparison.OrdinalIgnoreCase)))
        {
            ModUpdateChecker.RunSelfTestAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Any(a => string.Equals(a, "--selftest-mod-restart-flow", StringComparison.OrdinalIgnoreCase)))
        {
            ModUpdateRestartFlow.RunSelfTestAsync().GetAwaiter().GetResult();
            return;
        }

        // UI text is English; keep German culture for dates, times, and numbers.
        var de = CultureInfo.GetCultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = de;
        CultureInfo.CurrentCulture = de;

        // Must stay open when the embedded server console starts/stops.
        ConsoleGuard.Install();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            DebugLog("UI thread exception: " + e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            DebugLog("Unhandled exception: " + e.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DebugLog("Unobserved task exception: " + e.Exception);
            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void DebugLog(string message)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine(message);
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZomboidManager");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "error.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore logging failures
        }
    }
}
