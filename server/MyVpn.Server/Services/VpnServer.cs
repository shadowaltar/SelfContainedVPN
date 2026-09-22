using System.Security.Cryptography;
using System.Text;
using MyVpn.Server.Core;
using MyVpn.Server.Native;

namespace MyVpn.Server.Services;

/// <summary>High-level orchestration of the WireGuard server: the adapter, its peers and host networking.</summary>
public sealed class VpnServer : IDisposable
{
    private readonly ConfigStore _store;
    private readonly NetworkConfigurator _network = new();

    /// <summary>
    /// Guards <see cref="_adapter"/><see cref="_outboundInterface"/>. Start/Stop run on a worker
    /// thread while the stats timer runs on the UI thread, so without this the native adapter
    /// handle can be freed mid-read (use-after-free).
    /// </summary>
    private readonly object _gate = new();

    private WireGuardAdapter? _adapter;
    private string? _outboundInterface;

    public VpnServer(ConfigStore store)
    {
        _store = store;
        Config = store.Load();
        NatBackend = ResolveNatBackend();
    }

    public AppConfig Config { get; }

    public NatBackend NatBackend { get; }

    // Lock-free reads: mutations happen under _gate, Volatile.Read gives a safe published reference
    // without making the UI thread wait for a slow Start/Stop.
    public bool IsRunning => Volatile.Read(ref _adapter) is not null;

    /// <summary>True once internet sharing (NAT) has been configured for the running tunnel.</summary>
    public bool NatActive { get; private set; }

    public string? OutboundInterface => Volatile.Read(ref _outboundInterface);

    public event Action<string>? Log;

    public void Start()
    {
        lock (_gate)
        {
            if (_adapter is not null)
                return;

            if (string.IsNullOrWhiteSpace(Config.Server.PublicEndpoint))
                throw new InvalidOperationException("Set the public endpoint (host:port) before starting the server.");

            try
            {
                Emit($"Creating WireGuard adapter '{Config.Server.AdapterName}'...");
                _adapter = WireGuardAdapter.Create(
                    Config.Server.AdapterName,
                    Config.Server.AdapterName,
                    DeterministicGuid(Config.Server.AdapterName));

                Emit("Pushing keys and peers to the driver...");
                ApplyConfiguration();

                _adapter.SetUp(true);
                Emit($"Adapter is up; configuring networking ({NatBackend})...");

                try
                {
                    var result = _network.Apply(Config.Server, Config.Server.AdapterName, NatBackend);
                    _outboundInterface = result.OutboundInterface;
                    NatActive = true;
                    Emit($"Ready. NAT backend: {result.Backend}, outbound interface: {result.OutboundInterface}.");
                }
                catch (Exception natError)
                {
                    // The tunnel itself is fine; keep it running but make the loss of internet sharing obvious.
                    NatActive = false;
                    Emit("WARNING: " + natError.Message);
                    Emit("The tunnel is up, but clients will NOT have internet access until NAT can be configured.");
                }
            }
            catch
            {
                // Do not leave a half-configured adapter, forwarding rules or NAT behind.
                Stop();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_adapter is null)
                return;

            try
            {
                _adapter.SetUp(false);
            }
            catch (Exception ex)
            {
                Emit($"Warning while bringing the adapter down: {ex.Message}");
            }

            _network.Remove(Config.Server, Config.Server.AdapterName, NatBackend, _outboundInterface);
            _adapter.Dispose();
            _adapter = null;
            _outboundInterface = null;
            NatActive = false;
            Emit("Server stopped.");
        }
    }

    /// <summary>
    /// Adds a peer. When <paramref name="clientPublicKey"/> is supplied the server never sees or
    /// stores the client's private key (the recommended flow).
    /// </summary>
    public Peer AddPeer(string name, string? clientPublicKey = null)
    {
        lock (_gate)
        {
            string publicKey;
            string privateKey;

            if (!string.IsNullOrWhiteSpace(clientPublicKey))
            {
                var normalized = clientPublicKey.Trim();
                if (!WgKeys.IsValidKey(normalized))
                    throw new InvalidOperationException("The client public key is not a valid WireGuard key.");
                if (Config.Peers.Any(p => string.Equals(p.PublicKey, normalized, StringComparison.Ordinal)))
                    throw new InvalidOperationException("A peer with this public key already exists.");

                publicKey = normalized;
                privateKey = string.Empty;
            }
            else
            {
                (privateKey, publicKey) = WgKeys.Generate();
            }

            var peer = new Peer
            {
                Name = name,
                PrivateKey = privateKey,
                PublicKey = publicKey,
                TunnelAddress = NextTunnelAddress(),
            };

            Config.Peers.Add(peer);
            SavePeerFile(peer);
            Save();

            if (_adapter is not null)
            {
                ApplyConfiguration();
                Emit($"Peer '{peer.Name}' added and applied.");
            }

            return peer;
        }
    }

    public void RemovePeer(Peer peer)
    {
        lock (_gate)
        {
            if (!Config.Peers.Remove(peer))
                return;

            Save();
            if (_adapter is not null)
            {
                ApplyConfiguration();
                Emit($"Peer '{peer.Name}' removed.");
            }
        }
    }

    public void SetPeerEnabled(Peer peer, bool enabled)
    {
        lock (_gate)
        {
            if (peer.Enabled == enabled)
                return;

            peer.Enabled = enabled;
            Save();
            if (_adapter is not null)
            {
                ApplyConfiguration();
                Emit($"Peer '{peer.Name}' {(enabled ? "enabled" : "disabled")}.");
            }
        }
    }

    public void RefreshStats()
    {
        lock (_gate)
        {
            if (_adapter is null)
                return;

            List<NativePeerStats> stats;
            try
            {
                stats = _adapter.GetPeerStats();
            }
            catch
            {
                return;
            }

            var byKey = stats.ToDictionary(s => s.PublicKey, StringComparer.Ordinal);
            foreach (var peer in Config.Peers)
            {
                if (byKey.TryGetValue(peer.PublicKey, out var s))
                {
                    // Driver TxBytes are bytes sent to the peer (their download), RxBytes are received (their upload).
                    peer.TxBytes = s.TxBytes;
                    peer.RxBytes = s.RxBytes;
                    peer.LastHandshake = s.LastHandshake;
                }
                else
                {
                    peer.TxBytes = 0;
                    peer.RxBytes = 0;
                    peer.LastHandshake = null;
                }
            }
        }
    }

    public void UpdatePublicEndpoint(string endpoint)
    {
        lock (_gate)
        {
            Config.Server.PublicEndpoint = endpoint;
            Save();
        }
    }

    public void Save() => _store.Save(Config);

    public void Dispose() => Stop();

    private void ApplyConfiguration()
    {
        if (_adapter is null)
            return;

        var privateKey = Convert.FromBase64String(Config.Server.PrivateKey);
        _adapter.SetConfiguration(privateKey, (ushort)Config.Server.ListenPort, BuildNativePeers());
    }

    private IReadOnlyList<NativePeerConfig> BuildNativePeers()
    {
        var result = new List<NativePeerConfig>();

        foreach (var peer in Config.Peers)
        {
            if (!peer.Enabled || string.IsNullOrWhiteSpace(peer.TunnelAddress) || !WgKeys.IsValidKey(peer.PublicKey))
                continue;

            var native = new NativePeerConfig
            {
                PublicKey = Convert.FromBase64String(peer.PublicKey),
                PresharedKey = WgKeys.IsValidKey(peer.PresharedKey) ? Convert.FromBase64String(peer.PresharedKey!) : null,
            };

            native.AllowedIps.Add(new NativeAllowedIp
            {
                Address = System.Net.IPAddress.Parse(peer.TunnelAddress).GetAddressBytes(),
                Family = WireGuardNative.AF_INET,
                Cidr = 32,
            });

            result.Add(native);
        }

        return result;
    }

    private string NextTunnelAddress()
    {
        var mask = IpUtil.Mask(Config.Server.InterfacePrefixLength);
        var network = IpUtil.ToUint(Config.Server.InterfaceAddress) & mask;
        var used = Config.Peers
            .Select(p => p.TunnelAddress)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToHashSet(StringComparer.Ordinal);

        var hostCount = 1u << (32 - Config.Server.InterfacePrefixLength);
        for (uint host = 2; host < hostCount - 1; host++)
        {
            var candidate = IpUtil.FromUint(network + host);
            if (candidate == Config.Server.InterfaceAddress)
                continue;
            if (!used.Contains(candidate))
                return candidate;
        }

        throw new InvalidOperationException("No free tunnel addresses remain in the configured subnet.");
    }

    private void SavePeerFile(Peer peer)
    {
        try
        {
            _store.SavePeerConfig(peer.Name, ClientConfig.Build(Config.Server, peer));
        }
        catch (Exception ex)
        {
            Emit($"Could not write peer file: {ex.Message}");
        }
    }

    private NatBackend ResolveNatBackend()
    {
        if (Enum.TryParse<NatBackend>(Config.Server.NatBackend, ignoreCase: true, out var configured))
        {
            ApplyAddressForBackend(configured);
            return configured;
        }

        var detected = NetworkConfigurator.Detect();
        Config.Server.NatBackend = detected.ToString();
        ApplyAddressForBackend(detected);
        _store.Save(Config);
        return detected;
    }

    /// <summary>
    /// Internet Connection Sharing always uses 192.168.137.1/24 on the private adapter, so when
    /// that backend is in use the tunnel network must match it.
    /// </summary>
    private void ApplyAddressForBackend(NatBackend backend)
    {
        if (backend != NatBackend.Ics)
            return;

        if (IpUtil.IsInSubnet(Config.Server.InterfaceAddress, NetworkConfigurator.IcsAddress, NetworkConfigurator.IcsPrefixLength))
            return;

        Config.Server.InterfaceAddress = NetworkConfigurator.IcsAddress;
        Config.Server.InterfacePrefixLength = NetworkConfigurator.IcsPrefixLength;

        var host = 2;
        foreach (var peer in Config.Peers)
        {
            peer.TunnelAddress = $"192.168.137.{host++}";
            SavePeerFile(peer);
        }

        Emit("Internet Connection Sharing detected: tunnel network set to 192.168.137.0/24. Re-export peer configs.");
    }

    private static Guid DeterministicGuid(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("MyVpn:" + name));
        return new Guid(hash.AsSpan(0, 16));
    }

    private void Emit(string message) => Log?.Invoke(message);
}
