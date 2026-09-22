using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MyVpn.Server.Core;

namespace MyVpn.Server.Services;

/// <summary>How the server masquerades client traffic onto the internet.</summary>
public enum NatBackend
{
    /// <summary>Windows NAT (WinNAT / New-NetNat). Clean, available on most editions.</summary>
    WinNat,

    /// <summary>Internet Connection Sharing. Fallback for editions without WinNAT (e.g. Windows Home).</summary>
    Ics,
}

internal sealed record NetworkApplyResult(NatBackend Backend, string OutboundInterface);

/// <summary>
/// Configures the Windows side of the VPN: assigns the tunnel address to the WireGuard
/// adapter (WinNAT mode), enables IP forwarding, sets up NAT so clients can reach the
/// internet, opens the WireGuard UDP port on the active network profile only, and applies
/// peer-isolation / LAN-isolation firewall rules.
///
/// All dynamic values are passed to PowerShell through environment variables, so nothing is
/// ever interpolated into script text (defends against command injection even if a config
/// value contains quoting characters).
/// </summary>
internal sealed class NetworkConfigurator
{
    public static readonly string IcsAddress = "192.168.137.1";
    public const int IcsPrefixLength = 24;

    private const string EnvAdapter = "MYVPN_ADAPTER";
    private const string EnvFirewall = "MYVPN_FW";
    private const string EnvSubnet = "MYVPN_SUBNET";
    private const string EnvPort = "MYVPN_PORT";
    private const string EnvMtu = "MYVPN_MTU";
    private const string EnvPeerIsolation = "MYVPN_PEERISO";
    private const string EnvBlockLan = "MYVPN_BLOCKLAN";
    private const string EnvAddress = "MYVPN_ADDR";
    private const string EnvPrefix = "MYVPN_PREFIX";
    private const string EnvNat = "MYVPN_NAT";
    private const string EnvOutbound = "MYVPN_OUT";

    public NetworkApplyResult Apply(ServerOptions options, string adapterName, NatBackend backend)
    {
        var blockLan = !options.AllowLanAccess && !IsPrivateEndpoint(options.PublicEndpoint);
        var environment = BuildEnvironment(options, adapterName, blockLan, outbound: null);
        return backend == NatBackend.WinNat ? ApplyWinNat(environment) : ApplyIcs(environment);
    }

    public void Remove(ServerOptions options, string adapterName, NatBackend backend, string? outboundInterface)
    {
        if (backend == NatBackend.Ics)
            RunSta(() => DisableIcsSharing(outboundInterface, adapterName));

        var environment = BuildEnvironment(options, adapterName, blockLan: false, outbound: outboundInterface);
        try
        {
            PowerShellRunner.Run(CleanupScript, environment);
        }
        catch
        {
            // Teardown is best-effort; adapter removal cleans up the rest.
        }
    }

    /// <summary>Detects the machine's public IPv4 address (used as the default client endpoint).</summary>
    public static string? TryGetPublicIp()
    {
        string[] providers =
        {
            "https://api.ipify.org",
            "https://ifconfig.me/ip",
            "https://icanhazip.com",
        };

        foreach (var provider in providers)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                var value = client.GetStringAsync(provider).GetAwaiter().GetResult().Trim();
                if (System.Net.IPAddress.TryParse(value, out _))
                    return value;
            }
            catch
            {
                // Try the next provider.
            }
        }

        return null;
    }

    private static NetworkApplyResult ApplyWinNat(IReadOnlyDictionary<string, string?> environment)
    {
        const string body = """
            $addr   = $env:MYVPN_ADDR
            $prefix = [int]$env:MYVPN_PREFIX
            $nat    = $env:MYVPN_NAT

            $existing = Get-NetIPAddress -InterfaceAlias $adapter -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                        Where-Object { $_.IPAddress -eq $addr }
            if (-not $existing) {
                New-NetIPAddress -InterfaceAlias $adapter -IPAddress $addr -PrefixLength $prefix -ErrorAction Stop | Out-Null
            }

            Get-NetNat -Name $nat -ErrorAction SilentlyContinue | Remove-NetNat -Confirm:$false -ErrorAction SilentlyContinue
            New-NetNat -Name $nat -InternalIPInterfaceAddressPrefix $subnet -ErrorAction Stop | Out-Null

            Write-Output $out
            """;

        var outbound = PowerShellRunner.Run(StopPreference + CommonPrelude + Environment.NewLine + body, environment);
        return new NetworkApplyResult(NatBackend.WinNat, outbound);
    }

    private static NetworkApplyResult ApplyIcs(IReadOnlyDictionary<string, string?> environment)
    {
        const string body = """
            # Make ICS survive reboots and make sure the service is running (per Windows OS Hub).
            try { New-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\SharedAccess' -Name EnableRebootPersistConnection -Value 1 -PropertyType DWord -Force | Out-Null } catch { }
            try { Set-Service SharedAccess -StartupType Automatic -ErrorAction SilentlyContinue } catch { }
            try { if ((Get-Service SharedAccess -ErrorAction SilentlyContinue).Status -ne 'Running') { Start-Service SharedAccess } } catch { }

            Write-Output $out
            """;

        var outbound = PowerShellRunner.Run(StopPreference + CommonPrelude + Environment.NewLine + body, environment);

        // ICS assigns 192.168.137.1/24 to the private (VPN) adapter and enables NAT.
        var adapter = environment[EnvAdapter] ?? string.Empty;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                RunSta(() => EnableIcsSharing(outbound, adapter));
                lastError = null;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                // Windows can leave ICS wedged with stale PublicIndex/PrivateIndex state (0x80040201).
                // Clearing that state and retrying recovers it without a reboot.
                ResetIcsState();
                Thread.Sleep(1500);
            }
        }

        if (lastError is not null)
        {
            throw new InvalidOperationException(
                "Internet Connection Sharing could not be enabled, even after resetting its state. " +
                "A reboot may be required. Details: " + lastError.Message, lastError);
        }

        return new NetworkApplyResult(NatBackend.Ics, outbound);
    }

    /// <summary>
    /// Clears the stale ICS bookkeeping (PublicIndex/PrivateIndex) that Windows leaves behind after
    /// an interrupted share and that blocks enabling sharing again.
    /// </summary>
    private static void ResetIcsState()
    {
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            $ProgressPreference = 'SilentlyContinue'
            Stop-Service SharedAccess -Force
            Start-Sleep -Milliseconds 800
            Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedAccess' -Name PrivateIndex -ErrorAction SilentlyContinue
            Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedAccess' -Name PublicIndex -ErrorAction SilentlyContinue
            Start-Service SharedAccess
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Service SharedAccess -ErrorAction SilentlyContinue).Status -ne 'Running') {
                if ((Get-Date) -gt $deadline) { break }
                Start-Sleep -Milliseconds 250
            }
            """;
        PowerShellRunner.Run(script);
    }

    private const string StopPreference = "$ErrorActionPreference = 'Stop'" + "\n";

    /// <summary>Shared setup: outbound detection, adapter wait, forwarding/MTU and firewall rules.</summary>
    private const string CommonPrelude = """
        $ProgressPreference = 'SilentlyContinue'
        $adapter       = $env:MYVPN_ADAPTER
        $fw            = $env:MYVPN_FW
        $subnet        = $env:MYVPN_SUBNET
        $port          = [int]$env:MYVPN_PORT
        $mtu           = [int]$env:MYVPN_MTU
        $peerIsolation = $env:MYVPN_PEERISO -eq 'true'
        $blockLan      = $env:MYVPN_BLOCKLAN -eq 'true'

        $out = (Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                Sort-Object { $_.RouteMetric + $_.InterfaceMetric } | Select-Object -First 1).InterfaceAlias
        if (-not $out) { throw 'Could not determine the outbound network interface.' }

        $deadline = (Get-Date).AddSeconds(15)
        while (-not (Get-NetAdapter -Name $adapter -ErrorAction SilentlyContinue)) {
            if ((Get-Date) -gt $deadline) { throw "WireGuard adapter '$adapter' did not appear." }
            Start-Sleep -Milliseconds 250
        }

        $category = (Get-NetConnectionProfile -InterfaceAlias $out -ErrorAction SilentlyContinue |
                     Select-Object -First 1).NetworkCategory
        $profiles = @('Domain','Private','Public')
        switch ("$category") {
            'DomainAuthenticated' { $profiles = @('Domain') }
            'Private'             { $profiles = @('Private') }
            'Public'              { $profiles = @('Public') }
        }

        Set-NetIPInterface -InterfaceAlias $adapter -Forwarding Enabled -ErrorAction Stop
        Set-NetIPInterface -InterfaceAlias $out     -Forwarding Enabled -ErrorAction Stop
        try { Set-NetIPInterface -InterfaceAlias $adapter -NlMtuBytes $mtu -ErrorAction Stop } catch { }

        Get-NetFirewallRule -DisplayName "$fw*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        New-NetFirewallRule -DisplayName $fw -Direction Inbound -Action Allow -Protocol UDP -LocalPort $port -Profile $profiles | Out-Null
        if ($peerIsolation) {
            New-NetFirewallRule -DisplayName "$fw (peer isolation)" -Direction Inbound -Action Block -Protocol Any -InterfaceAlias $adapter -RemoteAddress $subnet -LocalAddress $subnet | Out-Null
        }
        if ($blockLan) {
            New-NetFirewallRule -DisplayName "$fw (block LAN)" -Direction Inbound -Action Block -Protocol Any -InterfaceAlias $adapter -RemoteAddress $subnet -LocalAddress @('10.0.0.0/8','172.16.0.0/12','192.168.0.0/16','169.254.0.0/16') | Out-Null
        }
        """;

    private const string CleanupScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        $adapter = $env:MYVPN_ADAPTER
        $nat     = $env:MYVPN_NAT
        $fw      = $env:MYVPN_FW
        $out     = $env:MYVPN_OUT

        Get-NetNat -Name $nat -ErrorAction SilentlyContinue | Remove-NetNat -Confirm:$false -ErrorAction SilentlyContinue
        Get-NetFirewallRule -DisplayName "$fw*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        Set-NetIPInterface -InterfaceAlias $adapter -Forwarding Disabled -ErrorAction SilentlyContinue
        if ($out) { Set-NetIPInterface -InterfaceAlias $out -Forwarding Disabled -ErrorAction SilentlyContinue }
        """;

    private static Dictionary<string, string?> BuildEnvironment(ServerOptions options, string adapterName, bool blockLan, string? outbound) => new()
    {
        [EnvAdapter] = adapterName,
        [EnvFirewall] = options.FirewallRuleName,
        [EnvSubnet] = options.Subnet,
        [EnvPort] = options.ListenPort.ToString(CultureInfo.InvariantCulture),
        [EnvMtu] = options.Mtu.ToString(CultureInfo.InvariantCulture),
        [EnvPeerIsolation] = options.PeerIsolation ? "true" : "false",
        [EnvBlockLan] = blockLan ? "true" : "false",
        [EnvAddress] = options.InterfaceAddress,
        [EnvPrefix] = options.InterfacePrefixLength.ToString(CultureInfo.InvariantCulture),
        [EnvNat] = options.NatName,
        [EnvOutbound] = outbound ?? string.Empty,
    };

    private static void EnableIcsSharing(string publicName, string privateName)
    {
        RegisterHnetcfg();

        var type = Type.GetTypeFromProgID("HNetCfg.HNetShare")
            ?? throw new InvalidOperationException("Internet Connection Sharing (HNetCfg.HNetShare) is not available.");

        dynamic manager = Activator.CreateInstance(type)!;
        try
        {
            // Always start from a clean slate. Disable any existing sharing first and then
            // RE-ENUMERATE: the config objects captured before DisableSharing are not reliable to
            // re-enable (that was causing EnableSharing to fail with 0x80040201).
            DisableAllConnections(manager);
            Thread.Sleep(500);

            dynamic? publicConfig = null;
            dynamic? privateConfig = null;

            foreach (var connection in (System.Collections.IEnumerable)manager.EnumEveryConnection)
            {
                string name = (string)manager.NetConnectionProps(connection).Name;
                dynamic config = manager.INetSharingConfigurationForINetConnection(connection);

                if (string.Equals(name, publicName, StringComparison.OrdinalIgnoreCase))
                    publicConfig = config;
                else if (string.Equals(name, privateName, StringComparison.OrdinalIgnoreCase))
                    privateConfig = config;
            }

            if (publicConfig is null)
                throw new InvalidOperationException($"Could not find the outbound adapter '{publicName}' for ICS.");
            if (privateConfig is null)
                throw new InvalidOperationException($"Could not find the VPN adapter '{privateName}' for ICS.");

            try
            {
                publicConfig.EnableSharing(0);  // 0 = public
            }
            catch (Exception ex)
            {
                DisableAllConnections(manager);
                throw new InvalidOperationException($"EnableSharing(public='{publicName}') failed: {ex.Message}", ex);
            }

            // Give the service a moment to settle the public side before binding the private adapter.
            Thread.Sleep(1000);

            Exception? privateError = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    privateConfig.EnableSharing(1); // 1 = private
                    privateError = null;
                    break;
                }
                catch (Exception ex)
                {
                    privateError = ex;
                    Thread.Sleep(1000);
                }
            }

            if (privateError is not null)
            {
                DisableAllConnections(manager);
                throw new InvalidOperationException($"EnableSharing(private='{privateName}') failed: {privateError.Message}", privateError);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    private static void DisableIcsSharing(string? publicName, string privateName)
    {
        RegisterHnetcfg();

        var type = Type.GetTypeFromProgID("HNetCfg.HNetShare");
        if (type is null)
            return;

        dynamic manager = Activator.CreateInstance(type)!;
        try
        {
            dynamic? publicConfig = null;
            dynamic? privateConfig = null;

            foreach (var connection in (System.Collections.IEnumerable)manager.EnumEveryConnection)
            {
                string name = (string)manager.NetConnectionProps(connection).Name;
                dynamic config = manager.INetSharingConfigurationForINetConnection(connection);

                if (publicName is not null && string.Equals(name, publicName, StringComparison.OrdinalIgnoreCase))
                    publicConfig = config;
                else if (string.Equals(name, privateName, StringComparison.OrdinalIgnoreCase))
                    privateConfig = config;
            }

            // Only undo our own sharing; do not disturb unrelated ICS setups.
            TryDisable(publicConfig);
            TryDisable(privateConfig);
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    private static void DisableAllConnections(dynamic manager)
    {
        foreach (var connection in (System.Collections.IEnumerable)manager.EnumEveryConnection)
        {
            try
            {
                dynamic config = manager.INetSharingConfigurationForINetConnection(connection);
                if ((bool)config.SharingEnabled)
                    config.DisableSharing();
            }
            catch
            {
                // Best effort; one broken connection should not block the rest.
            }
        }
    }

    private static void TryDisable(dynamic? config)
    {
        if (config is null)
            return;

        try
        {
            if ((bool)config.SharingEnabled)
                config.DisableSharing();
        }
        catch
        {
            // Best effort; one broken connection should not block the rest.
        }
    }

    /// <summary>
    /// Re-registers hnetcfg.dll. Without this the ICS COM connection points can be unavailable,
    /// which surfaces as HRESULT 0x80040201 (EVENT_E_ALL_SUBSCRIBERS_FAILED) on EnableSharing.
    /// </summary>
    private static void RegisterHnetcfg()
    {
        try
        {
            var dll = Path.Combine(Environment.SystemDirectory, "hnetcfg.dll");
            using var process = Process.Start(new ProcessStartInfo("regsvr32.exe", $"/s \"{dll}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(10000);
        }
        catch
        {
            // Non-fatal: EnableSharing will report a clearer error if it still fails.
        }
    }

    /// <summary>Runs a COM call on a pumping STA thread (HNetCfg requires a single-threaded apartment with a message queue).</summary>
    private static void RunSta(Action action) => StaPump.Instance.Invoke(action);

    /// <summary>Probes whether WinNAT (the MSFT_NetNat WMI class) is present.</summary>
    public static NatBackend Detect()
    {
        try
        {
            var output = PowerShellRunner.Run(
                "$ProgressPreference='SilentlyContinue'; if (Get-CimClass -Namespace root/StandardCimv2 -ClassName MSFT_NetNat -ErrorAction SilentlyContinue) { 'WinNat' } else { 'Ics' }");
            return output.Contains("WinNat", StringComparison.OrdinalIgnoreCase) ? NatBackend.WinNat : NatBackend.Ics;
        }
        catch
        {
            return NatBackend.Ics;
        }
    }

    /// <summary>True when the configured endpoint is a private/link-local address (LAN-only setup).</summary>
    private static bool IsPrivateEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var host = endpoint.Trim();
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            if (end > 1) host = host[1..end];
        }
        else
        {
            var colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon)
                host = host[..colon];
        }

        if (!System.Net.IPAddress.TryParse(host, out var address))
            return false;

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            return false;

        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)
            || bytes[0] == 127;
    }
}
