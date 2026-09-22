using System.Net.NetworkInformation;
using MyVpn.Server.Core;
using MyVpn.Server.Native;
using MyVpn.Server.Services;

namespace MyVpn.Server;

/// <summary>
/// Headless self-test of the native WireGuardNT interop and (with --netcheck) the Windows
/// networking setup. Creates a temporary adapter, pushes a configuration with one peer,
/// reads it back, and tears everything down.
/// Run with: MyVpn.Server.exe --diagnostics [--netcheck] [--delete-driver]
/// </summary>
internal static class Diagnostics
{
    public static int Run(string[] args)
    {
        if (args.Contains("--wsl"))
            return WslCheck(args.Contains("--start") ? "start" : args.Contains("--stop") ? "stop" : "check");

        if (args.Contains("--hold-adapter"))
            return HoldAdapter();

        var deleteDriver = args.Contains("--delete-driver");
        var netCheck = args.Contains("--netcheck");
        var lines = new List<string>();
        var exitCode = 0;

        void Write(string message)
        {
            lines.Add(message);
            Console.WriteLine(message);
        }

        WireGuardAdapter? adapter = null;
        var options = new ServerOptions
        {
            AdapterName = "MyVpnDiag",
            InterfaceAddress = "10.99.0.1",
            InterfacePrefixLength = 24,
            Mtu = 1420,
            ListenPort = 51821,
            NatName = "MyVpnDiagNat",
            FirewallRuleName = "MyVpnDiag Firewall",
        };
        var net = new NetworkConfigurator();
        var backend = NetworkConfigurator.Detect();
        string? outbound = null;
        var netApplied = false;

        try
        {
            Write("MyVpn Server diagnostics");
            Write("driver version before adapter creation: " + WireGuardAdapter.GetRunningDriverVersion());

            var (privateKey, publicKey) = WgKeys.Generate();
            var (_, peerPublicKey) = WgKeys.Generate();
            Write("server public key: " + publicKey);
            Write("peer public key:   " + peerPublicKey);

            if (WgKeys.PublicFromPrivate(privateKey) != publicKey)
            {
                Write("FAIL: derived public key does not match generated public key");
                exitCode = 2;
            }

            adapter = WireGuardAdapter.Create(options.AdapterName, options.AdapterName, Guid.Parse("11112222-3333-4444-5555-666677778888"));
            Write("adapter created, LUID=" + adapter.Luid);

            var peer = new NativePeerConfig { PublicKey = Convert.FromBase64String(peerPublicKey) };
            peer.AllowedIps.Add(new NativeAllowedIp
            {
                Address = System.Net.IPAddress.Parse("10.99.0.2").GetAddressBytes(),
                Family = WireGuardNative.AF_INET,
                Cidr = 32,
            });

            adapter.SetConfiguration(Convert.FromBase64String(privateKey), (ushort)options.ListenPort, new[] { peer });
            Write("configuration applied");

            // The adapter must be up before networking: ICS only accepts a connected private adapter.
            adapter.SetUp(true);
            Write("adapter is up: " + adapter.IsUp);

            if (netCheck)
            {
                Write("NAT backend detected: " + backend);
                netApplied = true; // the prelude may have changed forwarding/firewall even if NAT then fails
                try
                {
                    var result = net.Apply(options, options.AdapterName, backend);
                    outbound = result.OutboundInterface;
                    Write("networking applied, outbound interface: " + outbound);
                }
                catch (Exception ex)
                {
                    Write("NAT WARNING (tunnel unaffected): " + ex.Message);
                }

                Write("adapter addresses: " + string.Join(", ", GetAddresses(options.AdapterName)));

                var rules = PowerShellRunner.Run($"""
                    $ProgressPreference = 'SilentlyContinue'
                    Get-NetFirewallRule -DisplayName '{options.FirewallRuleName}*' |
                        Select-Object DisplayName,Direction,Action,Profile,Enabled |
                        Format-Table -AutoSize | Out-String
                    """);
                Write("firewall rules:" + Environment.NewLine + rules);
            }

            var stats = adapter.GetPeerStats();
            Write("GetConfiguration parsed peers: " + stats.Count);
            if (stats.Count != 1)
            {
                Write("FAIL: expected exactly 1 peer");
                exitCode = 3;
            }
            else if (stats[0].PublicKey != peerPublicKey)
            {
                Write("FAIL: peer public key mismatch -> " + stats[0].PublicKey);
                exitCode = 3;
            }
            else
            {
                Write("peer round-trip OK (tx=" + stats[0].TxBytes + ", rx=" + stats[0].RxBytes + ")");
            }

            adapter.SetUp(false);
            Write("adapter is down");
        }
        catch (Exception ex)
        {
            Write("EXCEPTION: " + ex);
            exitCode = 1;
        }
        finally
        {
            // Tear networking down while the adapter still exists: ICS must unbind the private
            // adapter before it is destroyed, otherwise the stale binding blocks the next enable.
            if (netApplied)
            {
                net.Remove(options, options.AdapterName, backend, outbound);
                Write("networking removed");
            }

            try { adapter?.Dispose(); } catch { }
            adapter = null;
            Write("adapter closed");

            if (deleteDriver)
            {
                Thread.Sleep(1500);
                Write("driver deleted: " + WireGuardAdapter.DeleteDriver());
            }
        }

        Write(exitCode == 0 ? "RESULT: PASS" : "RESULT: FAIL");

        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "myvpn-diagnostics.log"), string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }
        catch
        {
            // best effort
        }

        return exitCode;
    }

    /// <summary>Headless check of the WSL-backed server backend, written to %TEMP%\myvpn-wsl.txt.</summary>
    private static int WslCheck(string action)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var server = new WslVpnServer(new ConfigStore());
            server.Log += m => sb.AppendLine("LOG  " + m);

            if (action == "start") server.Start();
            else if (action == "stop") server.Stop();

            server.RefreshStats();
            sb.AppendLine($"available = {WslVpnServer.IsAvailable()}");
            sb.AppendLine($"running   = {server.IsRunning}");
            sb.AppendLine($"publicKey = {server.Config.Server.PublicKey}");
            sb.AppendLine($"endpoint  = {server.Config.Server.PublicEndpoint}");
            sb.AppendLine($"subnet    = {server.Config.Server.Subnet}");
            sb.AppendLine($"peers     = {server.Peers.Count}");
            foreach (var p in server.Peers)
                sb.AppendLine($"  {p.Name}\t{p.TunnelAddress}\tenabled={p.Enabled}\trx={p.RxBytes}\ttx={p.TxBytes}\ths={p.LastHandshake}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "myvpn-wsl.txt"), sb.ToString());
        return 0;
    }

    /// <summary>Creates a WireGuard adapter, brings it up and holds it (for manual inspection).</summary>
    private static int HoldAdapter()
    {
        var (privateKey, _) = WgKeys.Generate();
        using var adapter = WireGuardAdapter.Create("MyVpnHold", "MyVpnHold", Guid.NewGuid());
        adapter.SetConfiguration(Convert.FromBase64String(privateKey), 51899, Array.Empty<NativePeerConfig>());
        adapter.SetUp(true);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "myvpn-hold.txt"), "holding MyVpnHold for 5 minutes");
        Console.WriteLine("holding MyVpnHold");
        Thread.Sleep(TimeSpan.FromMinutes(5));
        adapter.SetUp(false);
        return 0;
    }

    private static IEnumerable<string> GetAddresses(string adapterName)        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => string.Equals(n.Name, adapterName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address.ToString());
}
