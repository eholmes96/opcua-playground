using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using OpcUaPlayground.Dialogs;
using OpcUaPlayground.Tools;

namespace OpcUaPlayground;

public partial class App : Application
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();

    /// <summary>
    /// Normally shows MainWindow. The console switches exist for verification without hardware:
    /// --selftest runs the byte-level protocol harness, --simulate hosts a fake chassis on
    /// loopback, --connect runs one connection attempt and prints what the light would show,
    /// --browse and --watch print the tag tree and live values, --play reads a recorded run back
    /// with no controller at all, and --diagnose measures what an attempt costs the module's
    /// connection counters.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        if (Has(e.Args, "--selftest"))
        {
            AttachToConsole();
            Shutdown(SelfTest.Run() ? 0 : 1);
            return;
        }

        if (Has(e.Args, "--simulate"))
        {
            AttachToConsole();
            PlcSimulator.RunAsync(Has(e.Args, "empty"), Has(e.Args, "big")).GetAwaiter().GetResult();
            Shutdown(0);
            return;
        }

        if (Has(e.Args, "--watch"))
        {
            AttachToConsole();
            Shutdown(WatchProbe.RunAsync(e.Args).GetAwaiter().GetResult() ? 0 : 1);
            return;
        }

        if (Has(e.Args, "--describe"))
        {
            AttachToConsole();
            Shutdown(DescribeProbe.RunAsync(e.Args).GetAwaiter().GetResult() ? 0 : 1);
            return;
        }

        if (Has(e.Args, "--play"))
        {
            AttachToConsole();
            Shutdown(PlaybackProbe.Run(e.Args) ? 0 : 1);
            return;
        }

        if (Has(e.Args, "--browse"))
        {
            AttachToConsole();
            Shutdown(BrowseProbe.RunAsync(e.Args).GetAwaiter().GetResult() ? 0 : 1);
            return;
        }

        if (Has(e.Args, "--diagnose"))
        {
            AttachToConsole();
            Shutdown(DiagnosticsProbe.RunAsync(e.Args).GetAwaiter().GetResult() ? 0 : 1);
            return;
        }

        if (Has(e.Args, "--connect"))
        {
            AttachToConsole();
            Shutdown(ConnectProbe.RunAsync(e.Args).GetAwaiter().GetResult() ? 0 : 1);
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }

    private static bool Has(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>WinExe has no console of its own; reuse the launching one where there is one.</summary>
    private static void AttachToConsole()
    {
        if (!AttachConsole(-1)) AllocConsole();
    }

    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "OpcUaPlayground-error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e.Exception}\r\n");
        }
        catch { }
        DarkMessageBox.Show(MainWindow, $"Unexpected error: {e.Exception.Message}");
        e.Handled = true;
    }
}
