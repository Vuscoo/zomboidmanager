using System.Runtime.InteropServices;

namespace ZomboidManager;

/// <summary>
/// Prevents the WinForms host from exiting when a child console/server process
/// receives Ctrl+C / Ctrl+Break / console-close signals (common with redirected cmd/java).
/// </summary>
internal static class ConsoleGuard
{
    private const uint CtrlC = 0;
    private const uint CtrlBreak = 1;
    private const uint CtrlClose = 2;

    private static readonly ConsoleCtrlDelegate Handler = OnConsoleCtrl;

    private delegate bool ConsoleCtrlDelegate(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    public static void Install()
    {
        try
        {
            // Ignore console control events so child server lifecycle cannot ExitProcess us.
            SetConsoleCtrlHandler(Handler, add: true);
        }
        catch
        {
            // ignore – non-fatal on exotic hosts
        }
    }

    public static void DetachFromConsole()
    {
        try
        {
            FreeConsole();
        }
        catch
        {
            // ignore
        }
    }

    private static bool OnConsoleCtrl(uint ctrlType)
    {
        // Swallow signals that would otherwise terminate the whole manager.
        return ctrlType is CtrlC or CtrlBreak or CtrlClose;
    }
}
