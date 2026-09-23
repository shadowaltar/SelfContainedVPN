using System.Text.Json.Serialization;

namespace MyVpn.Server.Core;

public sealed class ServerOptions
{
    public string AdapterName { get; set; } = "MyVpn";
    public int ListenPort { get; set; } = 51820;
    public string InterfaceAddress { get; set; } = "10.8.0.1";
    public int InterfacePrefixLength { get; set; } = 24;
    public int Mtu { get; set; } = 1420;
    public string Dns { get; set; } = "1.1.1.1, 1.0.0.1";
    public string PrivateKey { get; set; } = "";
    public string PublicKey { get; set; } = "";

    /// <summary>host:port that clients use to reach this server, e.g. 203.0.113.5:51820.</summary>
    public string PublicEndpoint { get; set; } = "";

    public string NatName { get; set; } = "MyVpnNat";
    public string FirewallRuleName { get; set; } = "MyVpn WireGuard";

    /// <summary>"Auto" (detect), "WinNat" or "Ics". Resolved on first run and stored.</summary>
    public string NatBackend { get; set; } = "Auto";

    /// <summary>Block traffic between clients on the tunnel subnet.</summary>
    public bool PeerIsolation { get; set; } = true;

    /// <summary>Allow clients to reach the server's local LAN (blocked by default).</summary>
    public bool AllowLanAccess { get; set; }

    [JsonIgnore]
    public string Subnet => IpUtil.Network(InterfaceAddress, InterfacePrefixLength) + "/" + InterfacePrefixLength;
}

public sealed class Peer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string PublicKey { get; set; } = "";

    /// <summary>Stored only so the client configuration (and its private key) can be re-exported.</summary>
    public string PrivateKey { get; set; } = "";

    public string? PresharedKey { get; set; }

    /// <summary>Address assigned to the client inside the tunnel, e.g. 10.8.0.2.</summary>
    public string TunnelAddress { get; set; } = "";

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Live statistics, refreshed from the driver and never persisted.
    [JsonIgnore] public long RxBytes { get; set; }
    [JsonIgnore] public long TxBytes { get; set; }
    [JsonIgnore] public DateTimeOffset? LastHandshake { get; set; }

    /// <summary>Remote endpoint (ip:port) the peer is connecting from, as seen by the server.</summary>
    [JsonIgnore] public string Endpoint { get; set; } = "";

    /// <summary>Current transfer rate in bytes/second, computed between refreshes.</summary>
    [JsonIgnore] public double DownloadBytesPerSecond { get; set; }
    [JsonIgnore] public double UploadBytesPerSecond { get; set; }

    [JsonIgnore]
    public bool Online =>
        LastHandshake.HasValue && DateTimeOffset.UtcNow - LastHandshake.Value < TimeSpan.FromMinutes(3);
}

public sealed class AppConfig
{
    public ServerOptions Server { get; set; } = new();
    public List<Peer> Peers { get; set; } = new();
}
