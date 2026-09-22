# MyVpn

A self-hosted WireGuard VPN: a Windows server that turns this PC into a VPN gateway, and a
native Android client that routes **all** of its traffic through it.

```
  Android phone (MyVpn client)                Windows PC (MyVpn server)
  +---------------------------+               +-----------------------------------+
  |  your apps                |               |  WireGuardNT kernel adapter       |
  |      |  all traffic       |   encrypted   |  (10.8.0.1 / 192.168.137.1)       |
  |      v                    |     UDP       |        |                          |
  |  VpnService (wg-go)  ------------51820--------->  WireGuard driver           |
  |  tunnel 10.8.0.2          |               |        | IP forwarding + NAT      |
  +---------------------------+               |        v                          |
                                              |  physical NIC (WLAN) -> internet  |
                                              +-----------------------------------+
```

Both sides speak the standard WireGuard protocol.

## Repository layout

```
MyVpn/
├── MyVpn.slnx                     Visual Studio / dotnet solution
├── server/
│   └── MyVpn.Server/              C# .NET 8 WinForms server (x64, runs elevated)
│       ├── Core/                  keys, models, client-config + QR generation
│       ├── Native/                P/Invoke over the official wireguard.dll
│       ├── Services/              config store, NAT/forwarding, server orchestration
│       ├── UI/                    WinForms UI
│       ├── native/wireguard.dll   official WireGuardNT driver API (see Licensing)
│       └── Diagnostics.cs         headless self-test (--diagnostics)
└── android/                       Kotlin Android client (Gradle)
    └── app/                       VpnService powered by com.wireguard.android:tunnel
```

## How it works

**Server**
1. Generates a Curve25519 key pair (BouncyCastle) and stores it in
   `%ProgramData%\MyVpn\config.json`.
2. Creates a WireGuardNT adapter by calling the official `wireguard.dll` embeddable API.
   The DLL installs its own signed kernel driver on first use — **no separate WireGuard
   install is required**.
3. Pushes the interface private key, listen port and all enabled peers into the driver, then
   brings the adapter up and assigns it a tunnel address.
4. Enables IP forwarding and sets up NAT so clients reach the internet, and opens the UDP
   port in Windows Firewall.
5. For every peer it can export a full-tunnel `.conf` and a QR code.

**Android**
1. Imports a configuration by scanning the QR code or pasting it.
2. Hands the config to the official WireGuard engine (`GoBackend`), which runs
   `wireguard-go` on top of Android's `VpnService`.
3. Requests the system VPN consent once, then establishes a full-tunnel (0.0.0.0/0, ::/0)
   connection and shows live transfer counters.

## Requirements

- Windows 10/11 (x64). Creating the adapter/driver and configuring NAT require **administrator**.
- .NET 8 SDK to build the server.
- JDK 17, Android SDK (platform 35, build-tools 35) and Gradle 9 (wrapper included) to build
  the client.

## Building

Server:
```powershell
dotnet build server/MyVpn.Server/MyVpn.Server.csproj -c Release
# output: server/MyVpn.Server/bin/Release/net8.0-windows/MyVpn.Server.exe
```

Android:
```powershell
$env:JAVA_HOME = "C:\Programs\.jdks\azul-17.0.15"   # any JDK 17
cd android
.\gradlew.bat :app:assembleDebug
# output: android/app/build/outputs/apk/debug/app-debug.apk
```

### Running from Visual Studio

Open `MyVpn.slnx`, then press **F5**. The server needs administrator rights (to create the VPN
adapter and configure NAT), and it elevates itself at startup, so a UAC prompt appears and the app
runs. To actually **debug** (breakpoints, stepping), start Visual Studio **as administrator** — the
process is then already elevated, self-elevation is skipped, and the debugger attaches normally.

## Running the server

1. Launch `MyVpn.Server.exe` (accept the UAC prompt).
2. Set the **Public endpoint** — the `host:port` clients should dial. Click **Detect** to fill
   in your public IP, or type a dynamic-DNS name. If the PC is behind a router, forward UDP
   51820 to it.
3. Click **Start server**.
4. **On the phone**, open the app and tap **Generate key**, then **Copy public key** (see below).
5. Click **Add peer** on the server, paste the phone's public key, and confirm. The server shows
   a QR code / `.conf` for the device.
6. Scan or paste that config into the app and tap **Connect**.

The server never needs (or stores) a client's private key: the app generates it locally and the
server-issued config contains a `__CLIENT_PRIVATE_KEY__` placeholder that the app fills in on
import. (An "generate the key on the server" checkbox is still offered for convenience, but it
means the server knows the private key.)

The grid shows each peer's state, live download/upload and last-handshake time. Peer configs are
also written to `%ProgramData%\MyVpn\peers\<name>.conf`.

### NAT backends

The server needs NAT to put client traffic onto the internet. It picks a backend automatically
on first run and remembers it:

| Backend | Used when | Tunnel network |
|---|---|---|
| `WinNat` | `New-NetNat` (WinNAT) is available | `10.8.0.1/24` (configurable) |
| `Ics` | WinNAT is missing (e.g. Windows Home) | `192.168.137.1/24` (fixed by ICS) |

The chosen backend is shown in the status line and the log.

### Diagnostics

A headless self-test of the native driver and networking (creates a temporary adapter, then
removes it):

```powershell
MyVpn.Server.exe --diagnostics            # driver + config round-trip
MyVpn.Server.exe --diagnostics --netcheck # also exercises NAT / forwarding / firewall
MyVpn.Server.exe --diagnostics --delete-driver
```

Results are written to `%TEMP%\myvpn-diagnostics.log`.

### Troubleshooting: "Internet sharing unavailable"

The server needs Windows NAT (WinNAT) or Internet Connection Sharing (ICS) to give clients
internet access. If the status line says **"Running WITHOUT internet sharing"**, the tunnel
itself works but NAT could not be configured. Common causes and fixes:

1. **Stale ICS state** — the server clears Windows' stale ICS bookkeeping (`PublicIndex`/
   `PrivateIndex`) and retries automatically. If that still fails, **reboot**; ICS commonly
   recovers after a restart.
2. **WinNAT missing** — some custom/"debloated" Windows images ship without the WinNAT WMI
   provider, so `New-NetNat` fails with "无效类"/"Invalid class". Repair the OS image as
   administrator, then reboot:
   ```powershell
   sfc /scannow
   DISM /Online /Cleanup-Image /RestoreHealth
   ```
3. **Fallback** — if WinNAT is unavailable the server uses ICS, which is IPv4-only and less
   reliable; Windows Pro/Server editions with WinNAT are the smoothest host.

If NAT cannot be enabled, the server still starts and clients can connect to it, but they will
not reach the internet through it.

### Linux server (fallback)

If this Windows host cannot do NAT (missing WinNAT / broken ICS), run the same VPN from a small
Linux machine or VM instead — see [`linux/README.md`](linux/README.md) and
[`linux/myvpn-server.sh`](linux/myvpn-server.sh). It uses standard WireGuard (the Android client
is unchanged), keeps the client-generated-key model, and NATs with `iptables MASQUERADE`.

## Using the Android client

1. Install `app-debug.apk`.
2. Tap **Generate key**, then **Copy public key**, and paste it into the server's **Add peer** dialog.
3. Back in the app, tap **Scan QR code** and scan the code from the server (or **Paste from clipboard**).
4. Accept Android's VPN connection request.
5. Tap **Connect**. The status line and live traffic counters update while connected.

Before connecting, the app shows the endpoint and routes it is about to use. The configuration and
the device private key are stored encrypted with the Android Keystore, and app backups are disabled.
For leak protection, tap **Open VPN settings** and enable Always-on VPN + "Block connections without VPN".

## Security notes

See [`SECURITY.md`](SECURITY.md) for the full security review and remediation status. Highlights:

- Private keys are protected at rest: server and peer keys in `config.json` are encrypted with
  Windows DPAPI, `%ProgramData%\MyVpn` is restricted to `SYSTEM` and `Administrators`, and the
  client key is encrypted with the Android Keystore.
- The server does not need a client's private key. The app generates the key pair locally and only
  the public key is given to the server; at import time the app fills in the
  `__CLIENT_PRIVATE_KEY__` placeholder in the server-issued config.
- Exported `.conf` files and their QR codes contain the server public key, the assigned address and
  the endpoint - not the client's private key (in the recommended flow).
- The protocol itself is WireGuard (Noise_IKpsk2); this project only wires up the official
  implementations.

## Licensing

- `server/MyVpn.Server/native/wireguard.dll` is the official prebuilt from
  <https://download.wireguard.com/wireguard-nt/> (WireGuardNT). It is redistributed under its
  prebuilt-binaries license; it may only be used through the API declared in `wireguard.h`.
  The header and license text are kept next to it.
- The Android client depends on `com.wireguard.android:tunnel` (GPLv2).
- The rest of this project is your own code.
