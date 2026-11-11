

using QDNH;
using QDNH.Settings;
using System.Globalization;

Vars.Out($"Quansheng Dock Network Host {Vars.Version}\n");
AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
using var heartbeat = QDNH.Hosting.ServerHeartbeat.StartFromEnvironment(Main.ProbeLocalHealth);
bool headless = args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase));
Main.Run(args.Where(a => !a.Equals("--headless", StringComparison.OrdinalIgnoreCase)).ToArray(), headless);

static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
{
    Exception exception = e.ExceptionObject as Exception ?? new("Unknown Exception Event");
    if (exception != null)
    {
        Vars.Err($"An unhandled exception {exception} occurred:\n\n{exception.Message}");
    }
}