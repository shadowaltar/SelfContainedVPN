using System.Diagnostics;
using System.Globalization;
using MyVpn.Server.Core;

namespace MyVpn.Server.Services;

/// <summary>
/// Drives the WireGuard server that runs inside WSL (Ubuntu). This is the backend the WinForms
/// app uses: "Start server" brings up wg0 inside WSL and keeps the distro alive, and peer
/// management is delegated to the Linux helper script.
/// </summary>
public sealed class WslVpnServer : IDisposable
{
    private const string Distro = "Ubuntu";
    private const string Script = "/root/myvpn-server.sh";

    private readonly ConfigStore _store;
    private readonly object _gate = new();

    private Process? _keepAlive;
    private volatile bool _running;
    private volatile List<Peer> _peers = new();

    public WslVpnServer(ConfigStore store)
    {
        _store = store;
        Config = store.Load();

        // The WSL server's settings are authoritative; force the tunnel subnet and drop any
        // stale Windows-server key so the UI shows the real WSL values once refreshed.
        Config.Server.ListenPort = 51820;
        Config.Server.InterfaceAddress = "10.8.0.1";
        Config.Server.InterfacePrefixLength = 24;
        Config.Server.PublicKey = string.Empty;
        Config.Server.NatBackend = "Wsl";
    }

    public AppConfig Config { get; }

    public bool IsRunning => _running;

    /// <summary>Linux NAT via iptables works, so this is true whenever the server is up.</summary>
    public bool NatActive => _running;

    public string BackendName => "Linux/WSL";

    public IReadOnlyList<Peer> Peers => _peers;

    public event Action<string>? Log;

    public static bool IsAvailable()
    {
        try
        {
            var result = RunHost("wsl.exe", "-d", Distro, "-u", "root", "--", "true");
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(Config.Server.PublicEndpoint))
                TryRunInWsl(Script, "set-endpoint", Config.Server.PublicEndpoint);

            Emit("Starting the Linux (WSL) WireGuard server...");
            TryRunInWsl("pkill", "-f", "sleep 2147483647");

            var start = RunInWsl("systemctl", "start", "wg-quick@wg0");
            if (start.ExitCode != 0)
                throw new InvalidOperationException("Could not start wg0: " + (FirstError(start) ?? "unknown error"));

            StartKeepAlive();

            for (var i = 0; i < 30; i++)
            {
                Thread.Sleep(500);
                var status = GetStatus();
                if (status.Up)
                {
                    _running = true;
                    ApplyStatus(status);
                    RefreshStats();
                    _store.Save(Config);
                    Emit($"Server is up (Linux/WSL), UDP {Config.Server.ListenPort}, public key {Config.Server.PublicKey}");
                    return;
                }
            }

            throw new InvalidOperationException(
                "The Linux WireGuard server did not come up. Open Ubuntu once and run: " +
                "sudo wg-quick up wg0");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            TryRunInWsl("systemctl", "stop", "wg-quick@wg0");
            TryRunInWsl("pkill", "-f", "sleep 2147483647");
            try { _keepAlive?.Kill(); } catch { /* ignore */ }
            _keepAlive = null;
            _running = false;
            Emit("Server stopped.");
        }
    }

    public Peer AddPeer(string name, string? clientPublicKey)
    {
        lock (_gate)
        {
            var argument = string.IsNullOrWhiteSpace(clientPublicKey) ? "--generate" : clientPublicKey.Trim();
            var result = RunInWsl(Script, "add-client", name, argument);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(FirstError(result) ?? "Could not add the peer.");

            RefreshStats();
            var peer = _peers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The peer was added but could not be read back.");
            Emit($"Peer '{name}' added -> {peer.TunnelAddress}.");
            return peer;
        }
    }

    public void RemovePeer(Peer peer)
    {
        lock (_gate)
        {
            var result = RunInWsl(Script, "remove-client", peer.Name);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(FirstError(result) ?? "Could not remove the peer.");
            RefreshStats();
            Emit($"Peer '{peer.Name}' removed.");
        }
    }

    public void SetPeerEnabled(Peer peer, bool enabled)
    {
        lock (_gate)
        {
            if (enabled)
                RunInWsl("wg", "set", "wg0", "peer", peer.PublicKey, "allowed-ips", peer.TunnelAddress + "/32");
            else
                RunInWsl("wg", "set", "wg0", "peer", peer.PublicKey, "remove");
            RefreshStats();
        }
    }

    public void UpdatePublicEndpoint(string endpoint)
    {
        Config.Server.PublicEndpoint = endpoint;
        if (_running)
            TryRunInWsl(Script, "set-endpoint", endpoint);
        _store.Save(Config);
    }

    /// <summary>Loads the private key for a generated client so its config can be re-exported.</summary>
    public void PopulatePrivateKey(Peer peer)
    {
        if (!string.IsNullOrWhiteSpace(peer.PrivateKey))
            return;
        var result = RunInWsl("cat", $"/etc/wireguard/clients/{peer.Name}.key");
        if (result.ExitCode == 0)
            peer.PrivateKey = result.StdOut.Trim();
    }

    public void RefreshStats()
    {
        var status = GetStatus();
        _running = status.Up;
        ApplyStatus(status);

        var peers = new List<Peer>();
        var dump = RunInWsl(Script, "dump");
        foreach (var line in dump.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("PEER\t", StringComparison.Ordinal))
                continue;
            var f = line.Split('\t');
            if (f.Length < 8)
                continue;

            var handshakeSeconds = long.TryParse(f[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hs) ? hs : 0;
            peers.Add(new Peer
            {
                Name = f[1],
                PublicKey = f[2],
                TunnelAddress = f[3],
                Enabled = string.Equals(f[4], "yes", StringComparison.OrdinalIgnoreCase),
                LastHandshake = handshakeSeconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(handshakeSeconds) : null,
                TxBytes = long.TryParse(f[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tx) ? tx : 0,
                RxBytes = long.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rx) ? rx : 0,
            });
        }

        _peers = peers;
    }

    public void Save() => _store.Save(Config);

    public void Dispose()
    {
        // Intentionally do NOT stop the server: it keeps running inside WSL after the app closes.
    }

    public void LogLines(string prefix) => Emit(prefix);

    // ----- internals -----

    private void ApplyStatus(ServerStatus status)
    {
        if (!string.IsNullOrWhiteSpace(status.PublicKey))
            Config.Server.PublicKey = status.PublicKey;
        if (!string.IsNullOrWhiteSpace(status.Endpoint))
            Config.Server.PublicEndpoint = status.Endpoint;
        if (status.Port > 0)
            Config.Server.ListenPort = status.Port;
    }

    private ServerStatus GetStatus()
    {
        var result = RunInWsl(Script, "status");
        if (result.ExitCode != 0)
            return new ServerStatus(false, "", "", 51820);
        return ServerStatus.Parse(result.StdOut);
    }

    private void StartKeepAlive()
    {
        // Clear any keep-alive from a previous run (the sleep value acts as a unique marker).
        TryRunInWsl("pkill", "-f", "sleep 2147483647");

        var psi = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Redirect so the child does not inherit our console handles.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(Distro);
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("root");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add("exec sleep 2147483647");
        _keepAlive = Process.Start(psi);
    }

    private void TryRunInWsl(params string[] command)
    {
        try { RunInWsl(command); }
        catch { /* ignore */ }
    }

    private static CommandResult RunInWsl(params string[] command)
        => RunHost("wsl.exe", new[] { "-d", Distro, "-u", "root", "--" }.Concat(command).ToArray());

    private static CommandResult RunHost(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {file}.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdout, stderr);

        return new CommandResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    private static string? FirstError(CommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0);
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    private void Emit(string message) => Log?.Invoke(message);

    private readonly record struct CommandResult(int ExitCode, string StdOut, string StdErr);

    private readonly record struct ServerStatus(bool Up, string PublicKey, string Endpoint, int Port)
    {
        public static ServerStatus Parse(string output)
        {
            var up = false;
            var pub = "";
            var endpoint = "";
            var port = 51820;

            foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line == "STATE=UP") up = true;
                else if (line.StartsWith("PUB=", StringComparison.Ordinal)) pub = line[4..].Trim();
                else if (line.StartsWith("ENDPOINT=", StringComparison.Ordinal)) endpoint = line[9..].Trim();
                else if (line.StartsWith("PORT=", StringComparison.Ordinal) &&
                         int.TryParse(line[5..].Trim(), out var p)) port = p;
            }

            return new ServerStatus(up, pub, endpoint, port);
        }
    }
}
