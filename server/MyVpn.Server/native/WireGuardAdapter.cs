using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MyVpn.Server.Native;

/// <summary>
/// Thin managed wrapper over an embedded WireGuardNT adapter. The official wireguard.dll
/// self-installs its signed kernel driver on first use, so the process must be elevated.
/// </summary>
internal sealed class WireGuardAdapter : IDisposable
{
    private static WireGuardNative.LoggerCallback? _loggerCallback;

    private IntPtr _handle;
    private bool _disposed;

    private WireGuardAdapter(IntPtr handle, ulong luid, bool created)
    {
        _handle = handle;
        Luid = luid;
        Created = created;
    }

    public ulong Luid { get; }
    public bool Created { get; }

    public static WireGuardAdapter Create(string name, string tunnelType, Guid? requestedGuid = null)
    {
        var handle = IntPtr.Zero;
        var lastError = 0;

        if (requestedGuid is { } guid)
        {
            handle = WireGuardNative.WireGuardCreateAdapter(name, tunnelType, ref guid);
            lastError = Marshal.GetLastWin32Error();
        }
        else
        {
            var random = Guid.NewGuid();
            handle = WireGuardNative.WireGuardCreateAdapter(name, tunnelType, ref random);
            lastError = Marshal.GetLastWin32Error();
        }

        if (handle == IntPtr.Zero)
        {
            // A leftover adapter with the same name may exist from a previous run.
            handle = WireGuardNative.WireGuardOpenAdapter(name);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(lastError, $"Could not create or open WireGuard adapter '{name}'.");
            return Wrap(handle, created: false);
        }

        return Wrap(handle, created: true);
    }

    public static WireGuardAdapter Open(string name)
    {
        var handle = WireGuardNative.WireGuardOpenAdapter(name);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open WireGuard adapter '{name}'.");
        return Wrap(handle, created: false);
    }

    private static WireGuardAdapter Wrap(IntPtr handle, bool created)
    {
        WireGuardNative.WireGuardGetAdapterLUID(handle, out var luid);
        return new WireGuardAdapter(handle, luid, created);
    }

    public static void RegisterLogger(Action<int, string> sink)
    {
        _loggerCallback = (level, _, message) =>
        {
            try { sink(level, message); }
            catch { /* never let a logging failure cross the native boundary */ }
        };
        WireGuardNative.WireGuardSetLogger(_loggerCallback);
    }

    public static uint GetRunningDriverVersion()
        => WireGuardNative.WireGuardGetRunningDriverVersion();

    /// <summary>Uninstalls the WireGuardNT driver. Only succeeds once no adapters remain.</summary>
    public static bool DeleteDriver()
    {
        var ok = WireGuardNative.WireGuardDeleteDriver();
        return ok;
    }

    public void SetConfiguration(byte[] privateKey, ushort listenPort, IReadOnlyList<NativePeerConfig> peers)
    {
        ThrowIfDisposed();
        var buffer = WireGuardConfigCodec.Build(privateKey, listenPort, peers);
        if (!WireGuardNative.WireGuardSetConfiguration(_handle, buffer, (uint)buffer.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WireGuardSetConfiguration failed.");
    }

    public List<NativePeerStats> GetPeerStats()
    {
        ThrowIfDisposed();
        var buffer = new byte[64];
        var bytes = (uint)buffer.Length;

        while (!WireGuardNative.WireGuardGetConfiguration(_handle, buffer, ref bytes))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != WireGuardNative.ErrorMoreData)
                throw new Win32Exception(error, "WireGuardGetConfiguration failed.");
            buffer = new byte[bytes];
        }

        return WireGuardConfigCodec.Parse(buffer);
    }

    public bool IsUp
    {
        get
        {
            ThrowIfDisposed();
            if (!WireGuardNative.WireGuardGetAdapterState(_handle, out var state))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WireGuardGetAdapterState failed.");
            return state == WireGuardNative.AdapterStateUp;
        }
    }

    public void SetUp(bool up)
    {
        ThrowIfDisposed();
        var state = up ? WireGuardNative.AdapterStateUp : WireGuardNative.AdapterStateDown;
        if (!WireGuardNative.WireGuardSetAdapterState(_handle, state))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not bring adapter {(up ? "up" : "down")}.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed || _handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(WireGuardAdapter));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            WireGuardNative.WireGuardCloseAdapter(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
