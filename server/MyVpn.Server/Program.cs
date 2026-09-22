using System.Diagnostics;
using System.Security.Principal;
using MyVpn.Server.Native;

namespace MyVpn.Server;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // The VPN adapter/driver and the NAT/firewall setup require administrator rights. Rather
        // than forcing elevation through the manifest (which stops Visual Studio from launching
        // the app), the app elevates itself on startup. If the host already runs elevated - e.g.
        // when Visual Studio is started as administrator - this is a no-op and debugging works.
        if (!IsElevated())
        {
            if (TryRelaunchElevated(args))
                return 0;

            MessageBox.Show(
                "MyVpn needs administrator rights to create the VPN adapter and configure networking.\n\n" +
                "Run MyVpn.Server.exe as administrator, or start Visual Studio as administrator to debug.",
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
