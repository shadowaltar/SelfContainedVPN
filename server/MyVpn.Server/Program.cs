using System.Diagnostics;
using System.Security.Principal;
using MyVpn.Server.Native;

namespace MyVpn.Server;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--relay"))
            return RunRelay(args);

        // Only the native-WireGuard diagnostics need Windows administrator rights. The GUI (which
        // drives the WSL server) and the WSL diagnostics run unelevated (least privilege).
        var needsAdmin = args.Contains("--netcheck")
                         || args.Contains("--hold-adapter")
                         || args.Contains("--delete-driver")
                         || (args.Any(a => a is "--diagnostics" or "--selftest") && !args.Contains("--wsl"));

        if (needsAdmin && !IsElevated())
        {
            if (TryRelaunchElevated(args))
                return 0;

            MessageBox.Show(
                "This diagnostic needs administrator rights to create the WireGuard adapter and configure networking.\n\n" +
                "Re-run it as administrator.",
                "Administrator rights required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return 1;
        }

        if (args.Contains("--diagnostics") || args.Contains("--selftest"))
            return Diagnostics.Run(args);

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowError(e.ExceptionObject as Exception);

        // Surface driver-level messages through the debugger only; going UP/DOWN failures are
        // reported through Win32Exception at the call sites.
        WireGuardAdapter.RegisterLogger((level, message) => Debug.WriteLine($"[wireguard:{level}] {message}"));

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }

        return 0;
    }

    private static int RunRelay(string[] args)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "myvpn-relay.log");
        void Log(string message)
        {
            try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}"); } catch { /* ignore */ }
        }

        int Get(string name, int fallback)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var value) ? value : fallback;
        }

        Log("relay start: " + string.Join(' ', args));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            Services.UdpRelay.RunAsync(Get("--listen", 51820), Get("--target-port", 51820), cts.Token, Log)
                .GetAwaiter().GetResult();
            Log("relay exited normally");
            return 0;
        }
        catch (Exception ex)
        {
            Log("relay error: " + ex);
            return 1;
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Relaunches this executable with the "runas" verb. Returns false if the user declines.</summary>
    private static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
                return false;

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            if (args.Length > 0)
                startInfo.Arguments = string.Join(' ', args.Select(QuoteArgument));

            Process.Start(startInfo);
            return true;
        }
        catch
        {
            // Typically the user dismissed the UAC prompt.
            return false;
        }
    }

    private static string QuoteArgument(string value)
        => value.Contains(' ') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private static void ShowError(Exception? exception)
    {
        MessageBox.Show(
            exception?.ToString() ?? "Unknown error.",
            "MyVpn Server error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
