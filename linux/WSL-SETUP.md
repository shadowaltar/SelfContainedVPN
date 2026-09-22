# Running the MyVpn server in WSL2 (what was set up)

This machine's Windows NAT is broken (missing WinNAT, wedged ICS), so the server runs in the
existing **WSL2 Ubuntu** instead. No VM, no extra downloads. The Android client is unchanged.

## What was done

1. **Mirrored networking for WSL** — `C:\Users\marsw\.wslconfig`:
   ```ini
   [wsl2]
   networkingMode=mirrored
   ```
   This makes WSL share the Windows host's IPs, so it has your LAN address
   (`192.168.10.243`). Applied with `wsl --shutdown`.

2. **Installed WireGuard and configured the server** (inside WSL, as root):
   ```bash
   wsl -d Ubuntu -u root -- /root/myvpn-server.sh setup 192.168.10.243
   ```
   - `wg0`: `10.8.0.1/24`, listen **UDP 51820**
   - NAT: `iptables -t nat -A POSTROUTING -o eth3 -j MASQUERADE`
   - `wg-quick@wg0` enabled (starts with WSL via systemd)
   - Server public key: `X4L9KqSFXDz7Vt2zFPOIzprMVuzqy73kY//yPmLmxW8=`

3. **Opened both firewalls** for inbound UDP 51820:
   - Windows Firewall rule `MyVpn WSL WireGuard`
   - **Hyper-V firewall** rule `MyVpnWSL-In` (mirrored mode blocks inbound by default):
     ```powershell
     New-NetFirewallHyperVRule -Name "MyVpnWSL-In" -DisplayName "MyVpn WSL WireGuard (UDP 51820)" `
       -Direction Inbound -VMCreatorId "{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}" `
       -Protocol UDP -LocalPorts 51820 -Action Allow
     ```

4. **Registered a client** `phone` (`10.8.0.2`) and exported:
   - `C:\Users\marsw\Downloads\myvpn-phone.png` (QR) and `myvpn-phone.conf`

5. **The WinForms app now drives this server.** `WslVpnServer` (server-side backend) runs the WSL
   commands, so the app's **Start server** / **Stop server** buttons control the Linux WireGuard
   server, and **Add peer / Delete peer / Enable-disable / Show config+QR** manage the WSL peers.
   - Start = `systemctl start wg-quick@wg0` + keep WSL alive.
   - Stop = `systemctl stop wg-quick@wg0`.
   - `wg-quick@wg0` is deliberately **disabled** for boot so a WSL restart never silently starts
     the server; the app (or the shortcut) starts it explicitly.

6. **Desktop shortcuts** (via `install-shortcut.ps1`):
   - **MyVpn Server** — one-click alternative: starts WSL + wg0 and keeps WSL alive.
   - **MyVpn App** — opens the WinForms app (whose Start/Stop buttons now drive the Linux server).
   - **MyVpn Stop** — `wsl --terminate Ubuntu`.

## Daily use

- Open **MyVpn App** and click **Start server** (set the Public endpoint first). Or double-click
  **MyVpn Server**.
- Connect the phone with the QR/conf.
- Click **Stop server** (or **MyVpn Stop**) to stop.

The Windows app no longer runs a WireGuard endpoint of its own — it is the front-end for the Linux
server. Everything (tunnel, NAT, peers) happens inside WSL.

## Manage clients (from Windows)

```powershell
wsl -d Ubuntu -u root -- /root/myvpn-server.sh list
wsl -d Ubuntu -u root -- /root/myvpn-server.sh add-client NAME 'PUBKEY'      # secure: key made on the phone
wsl -d Ubuntu -u root -- /root/myvpn-server.sh show-client NAME              # prints conf + QR
wsl -d Ubuntu -u root -- /root/myvpn-server.sh remove-client NAME
```

## Reaching it from outside the LAN

Forward **UDP 51820** on your router (`192.168.10.1`) to **192.168.10.243**, then set the client
endpoint to your public IP/DDNS (currently `165.84.139.112`):
```bash
wsl -d Ubuntu -u root -- /root/myvpn-server.sh setup 165.84.139.112
```
and re-export the client config.

## Caveats

- **WSL must be running** for the server to be up. The **MyVpn** shortcut starts it; after a reboot
  WSL does not auto-start by itself.
- **Port conflict:** don't also start the WireGuard server in the WinForms app (it binds UDP 51820
  too). Use the WinForms app only for what it's good at; the tunnel is served from WSL.
- Mirrored mode is required; if you remove `networkingMode=mirrored`, inbound to WSL stops working.
- These WSL changes affect only your user profile (`.wslconfig`) plus two firewall rules.

## Build the WinForms app (if the exe is missing)

```powershell
dotnet build "D:\Projects\MyVpn\MyVpn.slnx" -c Release
```
