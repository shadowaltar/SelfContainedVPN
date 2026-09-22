using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MyVpn.Server.Native;

internal static class WireGuardNative
{
    private const string Dll = "wireguard.dll";

    // WIREGUARD_KEY_LENGTH
    internal const int KeyLength = 32;

    // WIREGUARD_INTERFACE_FLAG
    internal const uint InterfaceHasPrivateKey = 1 << 1;
    internal const uint InterfaceHasListenPort = 1 << 2;
    internal const uint InterfaceReplacePeers = 1 << 3;

    // WIREGUARD_PEER_FLAG
    internal const uint PeerHasPublicKey = 1 << 0;
    internal const uint PeerHasPresharedKey = 1 << 1;
    internal const uint PeerHasPersistentKeepalive = 1 << 2;
    internal const uint PeerReplaceAllowedIps = 1 << 5;

    // WIREGUARD_ADAPTER_STATE
    internal const int AdapterStateDown = 0;
    internal const int AdapterStateUp = 1;

    // Address families
    internal const ushort AF_INET = 2;
    internal const ushort AF_INET6 = 23;

    internal const int ErrorMoreData = 234;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void LoggerCallback(int level, ulong timestamp, [MarshalAs(UnmanagedType.LPWStr)] string message);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr WireGuardCreateAdapter(string name, string tunnelType, ref Guid requestedGuid);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr WireGuardOpenAdapter(string name);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern void WireGuardCloseAdapter(IntPtr adapter);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern bool WireGuardDeleteDriver();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern void WireGuardGetAdapterLUID(IntPtr adapter, out ulong luid);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern uint WireGuardGetRunningDriverVersion();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern void WireGuardSetLogger(LoggerCallback? newLogger);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern bool WireGuardSetAdapterState(IntPtr adapter, int state);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern bool WireGuardGetAdapterState(IntPtr adapter, out int state);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern bool WireGuardSetConfiguration(IntPtr adapter, [In] byte[] config, uint bytes);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    internal static extern bool WireGuardGetConfiguration(IntPtr adapter, [Out] byte[] config, ref uint bytes);
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 80)]
internal unsafe struct WireGuardInterface
{
    public uint Flags;
    public ushort ListenPort;
    public fixed byte PrivateKey[32];
    public fixed byte PublicKey[32];
    public uint PeersCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 28)]
internal unsafe struct SockAddrInet
{
    public ushort Family;
    public ushort Port;
    public uint FlowInfo;
    public fixed byte Address[20];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 136)]
internal unsafe struct WireGuardPeer
{
    public uint Flags;
    public uint Reserved;
    public fixed byte PublicKey[32];
    public fixed byte PresharedKey[32];
    public ushort PersistentKeepalive;
    public SockAddrInet Endpoint;
    public ulong TxBytes;
    public ulong RxBytes;
    public ulong LastHandshake;
    public uint AllowedIPsCount;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 24)]
internal unsafe struct WireGuardAllowedIp
{
    public fixed byte Address[16];
    public ushort AddressFamily;
    public byte Cidr;
    public byte Reserved;
    public uint Flags;
}

/// <summary>A peer definition to be pushed into the kernel driver.</summary>
internal sealed class NativePeerConfig
{
    public required byte[] PublicKey { get; init; }
    public byte[]? PresharedKey { get; init; }
    public ushort PersistentKeepalive { get; init; }
    public List<NativeAllowedIp> AllowedIps { get; } = new();
}

internal sealed class NativeAllowedIp
{
    public required byte[] Address { get; init; }
    public required ushort Family { get; init; }
    public required byte Cidr { get; init; }
}

/// <summary>Snapshot of a configured peer as reported back by the driver.</summary>
internal sealed class NativePeerStats
{
    public required string PublicKey { get; init; }
    public long TxBytes { get; init; }
    public long RxBytes { get; init; }
    public DateTimeOffset? LastHandshake { get; init; }
}

internal static class WireGuardConfigCodec
{
    private const int InterfaceSize = 80;
    private const int PeerSize = 136;
    private const int AllowedIpSize = 24;

    /// <summary>
    /// Packs the interface plus every peer (each followed by its allowed IPs) into the single
    /// contiguous allocation that WireGuardSetConfiguration expects.
    /// </summary>
    internal static byte[] Build(byte[] privateKey, ushort listenPort, IReadOnlyList<NativePeerConfig> peers)
    {
        if (privateKey.Length != WireGuardNative.KeyLength)
            throw new ArgumentException("Private key must be 32 bytes.", nameof(privateKey));

        var size = InterfaceSize + peers.Sum(p => PeerSize + p.AllowedIps.Count * AllowedIpSize);
        var buffer = new byte[size];

        unsafe
        {
            fixed (byte* basePtr = buffer)
            {
                var iface = (WireGuardInterface*)basePtr;
                iface->Flags = WireGuardNative.InterfaceHasPrivateKey
                             | WireGuardNative.InterfaceHasListenPort
                             | WireGuardNative.InterfaceReplacePeers;
                iface->ListenPort = listenPort;
                for (var i = 0; i < WireGuardNative.KeyLength; i++)
                    iface->PrivateKey[i] = privateKey[i];
                iface->PeersCount = (uint)peers.Count;

                var cursor = basePtr + InterfaceSize;
                foreach (var source in peers)
                {
                    var peer = (WireGuardPeer*)cursor;
                    peer->Flags = WireGuardNative.PeerHasPublicKey | WireGuardNative.PeerReplaceAllowedIps;
                    for (var i = 0; i < WireGuardNative.KeyLength; i++)
                        peer->PublicKey[i] = source.PublicKey[i];

                    if (source.PresharedKey is { Length: WireGuardNative.KeyLength })
                    {
                        peer->Flags |= WireGuardNative.PeerHasPresharedKey;
                        for (var i = 0; i < WireGuardNative.KeyLength; i++)
                            peer->PresharedKey[i] = source.PresharedKey[i];
                    }

                    if (source.PersistentKeepalive > 0)
                    {
                        peer->Flags |= WireGuardNative.PeerHasPersistentKeepalive;
                        peer->PersistentKeepalive = source.PersistentKeepalive;
                    }

                    peer->AllowedIPsCount = (uint)source.AllowedIps.Count;
                    cursor += PeerSize;

                    foreach (var allowed in source.AllowedIps)
                    {
                        var entry = (WireGuardAllowedIp*)cursor;
                        for (var i = 0; i < 16; i++)
                            entry->Address[i] = i < allowed.Address.Length ? allowed.Address[i] : (byte)0;
                        entry->AddressFamily = allowed.Family;
                        entry->Cidr = allowed.Cidr;
                        cursor += AllowedIpSize;
                    }
                }
            }
        }

        return buffer;
    }

    /// <summary>Parses the buffer returned by WireGuardGetConfiguration into per-peer statistics.</summary>
    internal static List<NativePeerStats> Parse(byte[] buffer)
    {
        var result = new List<NativePeerStats>();

        unsafe
        {
            fixed (byte* basePtr = buffer)
            {
                var iface = (WireGuardInterface*)basePtr;
                var cursor = basePtr + InterfaceSize;

                for (var p = 0; p < iface->PeersCount; p++)
                {
                    var peer = (WireGuardPeer*)cursor;
                    var publicKey = Convert.ToBase64String(new ReadOnlySpan<byte>(peer->PublicKey, WireGuardNative.KeyLength));

                    var handshake = peer->LastHandshake;
                    DateTimeOffset? lastHandshake = null;
                    if (handshake > 0)
                        lastHandshake = DateTimeOffset.FromFileTime((long)handshake);

                    result.Add(new NativePeerStats
                    {
                        PublicKey = publicKey,
                        TxBytes = (long)peer->TxBytes,
                        RxBytes = (long)peer->RxBytes,
                        LastHandshake = lastHandshake,
                    });

                    cursor += PeerSize + (long)peer->AllowedIPsCount * AllowedIpSize;
                }
            }
        }

        return result;
    }
}
