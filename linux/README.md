# MyVpn — Linux server (fallback for when Windows NAT is unavailable)

A self-contained WireGuard server for Linux. It replaces the Windows server when Windows can't do
NAT (missing WinNAT / broken ICS).

Design notes:
- **Standard WireGuard** — the Android client works unchanged.
- **Client keeps its own private key.** You generate the key pair in the MyVpn app and give the
  server only the **public** key. The emitted client config uses the same
  `__CLIENT_PRIVATE_KEY__` placeholder the app fills in on import.
- **NAT** is done with `iptables MASQUERADE`, which works reliably on Linux.

Requires Debian/Ubuntu (uses `apt`). Tested script; `bash -n` clean.

---

## 1. Get a Linux host

Pick one:

- **Local VM (VirtualBox / VMware / Hyper-V).** Give it a network adapter set to **Bridged**
  (so it gets an IP on your home LAN), 1 vCPU / 1 GB RAM / 10 GB disk is plenty.
  Install Ubuntu Server (or Debian).
- **A cheap cloud VPS.** Simplest if you want access from anywhere — it has a real public IP, so no
  router port-forwarding is needed.

> Avoid WSL2 for this: it shares Windows' (broken) NAT and can't easily accept inbound WireGuard.

**Full step-by-step for both options: [`HOST-SETUP.md`](HOST-SETUP.md).**

## 2. Install and configure the server

Copy `myvpn-server.sh` to the Linux box, then:

```bash
sudo apt-get update && sudo apt-get install -y wireguard qrencode   # if not already
chmod +x myvpn-server.sh

# PUBLIC_ENDPOINT = the host/IP your phone will dial.
#   - same Wi-Fi as a bridged VM: the VM's LAN IP, e.g. 192.168.10.50
#   - remote: your public IP or DDNS name, e.g. vpn.example.com
sudo ./myvpn-server.sh setup 192.168.10.50
```

This installs WireGuard, generates the server key, enables IP forwarding, writes
`/etc/wireguard/wg0.conf` (with the NAT rules), starts `wg-quick@wg0`, and enables it on boot.

## 3. Register your phone

1. In the **MyVpn app**, tap **Generate key**, then **Copy public key**.
2. On the server, add the peer using that public key:

```bash
sudo ./myvpn-server.sh add-client phone 'PASTE_THE_PUBLIC_KEY_HERE'
```

(If you'd rather let the server generate the key — less secure, the server then knows the private
key — use `add-client phone --generate`.)

3. Print the client config + QR code:

```bash
sudo ./myvpn-server.sh show-client phone
```

4. In the app, tap **Scan QR code** (or **Paste from clipboard**) and scan/copy it.
5. Tap **Connect**. Done.

## 4. Networking / reachability

| Where the phone is | Endpoint to use | What's needed |
|---|---|---|
| Same Wi-Fi as the server | Server's LAN IP (`192.168.10.50`) | nothing extra |
| Mobile data / other network | Your **public IP** or DDNS name | forward **UDP 51820** on your router to the server's IP; the server's own `ufw` must allow it (the script adds the rule) |

Check the tunnel on the server any time:

```bash
sudo wg show
```

## 5. Managing peers

```bash
sudo ./myvpn-server.sh list
sudo ./myvpn-server.sh show-client NAME
sudo ./myvpn-server.sh remove-client NAME
```

## 6. Notes / troubleshooting

- **Config files:** `/etc/wireguard/wg0.conf`, server keys in `/etc/wireguard/keys/`, per-client
  metadata in `/etc/wireguard/clients/`, endpoint in `/etc/wireguard/endpoint`.
- **IPv6:** the client config advertises `::/0` so IPv6 never leaks in the clear; without server
  IPv6/NAT66 IPv6 is simply dropped. Add `ip6tables -t nat -A POSTROUTING -o <if> -j MASQUERADE`
  to `PostUp` if your server has IPv6 and you want it.
- **`wg-quick` fails to start:** check `journalctl -u wg-quick@wg0 -e` and confirm the outbound
  interface name in the `PostUp/PostDown` lines matches `ip route`.
- **No handshake in `wg show`:** the phone can't reach the endpoint — check the router port-forward
  / firewall, and that the endpoint is what the phone is actually dialing.
- **Ubuntu firewall:** if `ufw` is active, ensure `51820/udp` is allowed (the script does this).
