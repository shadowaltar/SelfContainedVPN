using System.Diagnostics;
using System.Text;

namespace MyVpn.Server.Services;

internal static class PowerShellRunner
{
    /// <summary>
    /// Runs a PowerShell script elevated (the host process is already elevated) and returns stdout.
    /// The script is passed as an encoded command and all dynamic values are passed as environment
    /// variables, so nothing is ever interpolated into the script text.
    /// </summary>
    public static string Run(string script, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
        };

        if (environment is not null)
        {
            foreach (var pair in environment)
                psi.Environment[pair.Key] = pair.Value ?? string.Empty;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start powershell.exe.");

        // Read both pipes concurrently; a GUI-less PowerShell serializes progress records to
        // stderr, and reading one stream to completion first can deadlock on a full pipe.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"Network configuration failed: {detail.Trim()}");
        }

        return stdout.Trim();
    }
}
