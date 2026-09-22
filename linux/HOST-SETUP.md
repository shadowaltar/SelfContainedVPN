# Getting a Linux host for the MyVpn server

Your PC already has the Windows hypervisor running (`HypervisorPresent = True`) and WSL2 with an
Ubuntu distro. That shapes the options below.

After you have Linux, come back to [`README.md`](README.md) and run `myvpn-server.sh`.

---

## Option 1 — A cheap VPS (recommended for access from anywhere)

Easiest and most reliable: it has a real public IP, so no router port-forwarding and no fighting
with the Windows hypervisor.

1. Pick a provider, e.g. **Hetzner**, **Vultr**, **DigitalOcean**, **Linode** (or a regional one).
   The smallest instance (~1 vCPU / 1 GB) is plenty.
2. Create a server with **Ubuntu 24.04 LTS (or Debian 12)** and add your SSH key.
3. Note the **public IP** it gives you.
4. In the provider's **firewall / security group**, allow inbound **UDP 51820** (and TCP 22 for SSH).
5. SSH in:
   ```bash
   ssh root@YOUR_VPS_IP
   ```
6. Copy the server script over (from your PC):
   ```powershell
   scp D:\Projects\MyVpn\linux\myvpn-server.sh root@YOUR_VPS_IP:/root/
   ```
7. On the VPS:
   ```bash
   chmod +x myvpn-server.sh
   sudo ./myvpn-server.sh setup YOUR_VPS_IP
   sudo ./myvpn-server.sh add-client phone 'PASTE_PUBLIC_KEY_FROM_APP'
   sudo ./myvpn-server.sh show-client phone
   ```
8. Scan the QR with the MyVpn app. Works from any network.

> Caveat: the VPN's internet egress will be the VPS's country, not your home.

---

## Option 2 — Local VirtualBox VM (egress through your home internet)

Free, keeps traffic coming out of your home connection. Works on Windows Home.

### 2a. Install VirtualBox
1. Download **VirtualBox for Windows hosts** from <https://www.virtualbox.org/wiki/Downloads> and
   install it.
2. It will run alongside the Windows hypervisor (slower than bare metal, fine for WireGuard). For
   best speed you *can* disable the Windows hypervisor, but that turns off WSL2/BlueStacks/VBS —
   only do that if you don't need them.

### 2b. Get an Ubuntu Server ISO
Download **Ubuntu Server 24.04 LTS** (64-bit) from <https://ubuntu.com/download/server> (~2.5 GB).

### 2c. Create the VM
1. VirtualBox → **New**:
   - Name: `myvpn`, Type: **Linux**, Version: **Ubuntu (64-bit)**.
   - Memory: **2048 MB**, Processors: 1–2.
   - Disk: **10 GB**, VDI, dynamically allocated.
2. **Settings → Network → Adapter 1**:
   - **Attached to: Bridged Adapter**, Name: your **Wi-Fi** adapter (`MediaTek Wi-Fi 6E …`).
   - This gives the VM its own IP on your home LAN (e.g. `192.168.10.50`).
3. Start the VM and install Ubuntu Server (minimal; create a user, enable **Install OpenSSH server**).
4. Log in and find the IP:
   ```bash
   ip -4 addr show
   ```
   Note the `192.168.10.x` address.

### 2d. (Fallback) If bridged Wi-Fi doesn't work
Bridging over Wi-Fi is occasionally flaky. If the VM never gets a LAN IP, switch Adapter 1 to
**NAT** and add a port-forward (VirtualBox NAT supports UDP):
- **Settings → Network → Adapter 1 → Advanced → Port Forwarding** → add:
  - Name: `wg`, Protocol: **UDP**, Host IP: (blank), Host Port: **51820**,
    Guest IP: (blank), Guest Port: **51820**.
- Now the phone dials your **Windows PC's** LAN IP (`192.168.10.x`):51820, and VirtualBox forwards
  it to the VM. (The router still needs to forward UDP 51820 to the Windows PC for remote access.)

### 2e. Copy the script in and run it
From your PC:
```powershell
scp D:\Projects\MyVpn\linux\myvpn-server.sh ubuntu@192.168.10.50:/home/ubuntu/
```
On the VM:
```bash
chmod +x myvpn-server.sh
sudo ./myvpn-server.sh setup 192.168.10.50     # the VM's own LAN IP (or your public IP for remote)
sudo ./myvpn-server.sh add-client phone 'PASTE_PUBLIC_KEY_FROM_APP'
sudo ./myvpn-server.sh show-client phone
```

### 2f. Router (only for access from mobile data)
Forward **UDP 51820** on your router (`192.168.10.1`) to:
- the VM's LAN IP (bridged mode), or
- the Windows PC's LAN IP (NAT + port-forward mode).
Then set the endpoint to your public IP/DDNS, e.g. `sudo ./myvpn-server.sh setup 165.84.139.112`.

---

## Option 3 — WSL2 (not recommended here)

You already have WSL2 + Ubuntu, which is tempting, but:
- WSL2 sits behind the Windows NAT using the same **WinNAT** stack that is broken on this machine.
- Forwarding inbound **UDP** to WSL is painful (`netsh portproxy` is TCP-only).

So WSL2 is a poor fit for an inbound WireGuard server. Use Option 1 or 2.

---

## After the server is up

Verify and manage:
```bash
sudo wg show
sudo ./myvpn-server.sh list
```

Then in the MyVpn app: **Generate key → Copy public key** (give it to `add-client`), then
**Scan QR / Paste** the output of `show-client` and **Connect**.
