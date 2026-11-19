using Streamy.DevTools;
using Streamy.Services;
using Streamy.UI;

namespace Streamy;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (DevSwitches.TryRun(args) is { } code) return code;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error(e.Exception, "UI");
            MessageBox.Show(e.Exception.Message, "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log.Error(ex, "fatal"); };

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        var main = new MainWindow();
        if (args is ["--mirror", var mirrorSerial, ..]) main.Shown += async (_, _) => await main.MirrorAsync(mirrorSerial);
        Application.Run(main);
        return 0;
    }
}
