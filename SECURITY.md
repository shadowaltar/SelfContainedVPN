# MyVpn — Security Review

Scope: the Windows server (`server/MyVpn.Server`) and the Android client (`android/`).
Method: manual source review plus verification of live state (Windows file ACLs, merged
Android manifest). Findings are not a formal audit.

**Assumption:** WireGuard's cryptography and the official implementations
(`wireguard.dll` / `com.wireguard.android:tunnel`) are trusted; this review covers how this
project wires them up and stores/derives secrets.

## Summary

| ID | Finding | Component | Severity | Status |
|----|---------|-----------|----------|--------|
| S1 | All private keys are stored world-readable | Server | High | Fixed |
| S2 | Server stores every client's private key | Server | High | Fixed |
| S3 | Config directory is writable by `Users` (key pre-seeding) | Server | Medium | Fixed |
| S4 | Firewall allows UDP 51820 on all network profiles | Server | Medium | Fixed |
| S5 | No peer isolation; clients can reach the server's LAN | Server | Medium | Fixed |
| S6 | Elevated PowerShell built from config with `-ExecutionPolicy Bypass` | Server | Low‑Medium | Fixed |
| S7 | NAT / ICS / IP forwarding persist after a crash | Server | Low | Mitigated |
| S8 | Client advertises `::/0` but the server has no IPv6 | Both | Low | Accepted (leak-free) |
| S9 | Secrets copied to the clipboard / shown as QR | Server | Low | Mitigated |
| S10 | Public IP discovered via a third-party service | Server | Low | Mitigated |
| A1 | `allowBackup="true"` can exfiltrate the client private key | Android | High | Fixed |
| A2 | No kill switch — cleartext leak when the tunnel drops | Android | Medium | Mitigated |
| A3 | Private key displayed without `FLAG_SECURE` | Android | Low‑Medium | Fixed |
| A4 | Any scanned/pasted configuration is trusted without confirmation | Android | Medium | Fixed |

## Remediation log

| ID | What was changed |
|----|------------------|
| S1 | The server private key and all peer private keys in `config.json` are encrypted at rest with Windows DPAPI (`Core/SecretProtector.cs`). Legacy plaintext is migrated on load. Exported peer `.conf` files must stay plaintext to be importable, so they are protected by the directory ACL instead. |
| S1/S3 | `%ProgramData%\MyVpn` (and its files) are re-ACL'd to `SYSTEM` + `Administrators` only, with inheritance disabled (`Services/ConfigStore.cs`, `HardenDataDirectory`). The `Users` ACE is removed. |
| S2 | The server now stores only the client's **public** key (`VpnServer.AddPeer`). The MyVpn app generates the key pair locally (`SecretStore`/`KeyPair`), shows the public key to paste into the server's **Add peer** dialog, and at import time fills the `__CLIENT_PRIVATE_KEY__` placeholder in the server-issued config. Server-side generation remains an explicit opt-in checkbox for convenience. |
| S4 | The UDP 51820 rule is scoped to the active network profile of the outbound adapter instead of `-Profile Any`. |
| S5 | Added firewall rules: a **peer isolation** rule (block tunnel-subnet → tunnel-subnet) and, unless `AllowLanAccess` is set, a **block LAN** rule (block tunnel-subnet → private ranges). Endpoint on a private IP (LAN-only setups) skips the LAN block. |
| S6 | Dynamic values are now passed to PowerShell as **environment variables**; nothing is interpolated into script text and `-ExecutionPolicy Bypass` was removed (`-EncodedCommand` does not need it). This removes the injection class entirely. |
| S7 | `VpnServer.Start` now tears down on any failure, and the server is stopped on `ProcessExit`. A crash can still leave state behind. |
| S8 | Accepted by design: keeping `::/0` black-holes IPv6 (dropped by cryptokey routing), which prevents leaks. Omitting `::/0` would instead send IPv6 in the clear. Real IPv6 egress would need a global prefix plus NAT66, which Windows ICS does not provide. |
| S9 | The peer dialog warns that the config is a secret and auto-clears the clipboard 30 s after copying if unchanged. |
| S10 | Detection now queries several providers with fallback, remains user-initiated (the "Detect" button), and a manually entered endpoint is always accepted. |
| A1 | `android:allowBackup="false"` and the config is stored encrypted with AES-256-GCM under an Android Keystore key (`SecretStore.kt`) instead of plaintext SharedPreferences. |
| A2 | Added an in-app hint **and a button that opens the system VPN settings**, so the user can enable Always-on VPN + "Block connections without VPN". A true kill switch is an Android OS setting that an app cannot force. |
| A3 | `FLAG_SECURE` is set on the activity window, blocking screenshots and recents thumbnails. |
| A4 | Before connecting, a confirmation dialog shows the parsed Endpoint and AllowedIPs. |
| — | Firewall/NAT teardown now removes all rules by display-name prefix, and the ICS path re-registers `hnetcfg.dll`, disables stale sharing first, and rolls back on failure. |

> Operational note: Internet Connection Sharing on Windows client editions is fragile. If the app reports that ICS could not be enabled, a reboot usually clears the stuck state. This was observed during testing on Windows 11 Home; WinNAT (`New-NetNat`) is used automatically where available and does not have this issue.

---

## Server findings

### S1 — All private keys are stored world-readable (High)

`ConfigStore.DataDirectory` is `%ProgramData%\MyVpn` (`Services/ConfigStore.cs:16`), and
`config.json` contains the server key and **every peer private key** (`Core/Models.cs:13,36`).

Verified ACL on a machine where the app ran:

```
C:\ProgramData\MyVpn            BUILTIN\Users:(I)(OI)(CI)(RX)
C:\ProgramData\MyVpn\config.json  BUILTIN\Users:(I)(RX)
```

Any standard local user can read the keys, then impersonate the server or any client.
Peer `.conf` files written to `%ProgramData%\MyVpn\peers` are affected the same way. After the fix
below, `config.json` holds DPAPI ciphertext and the directory is restricted to `SYSTEM` and
`Administrators`; the exported `.conf` files remain plaintext (they must be importable) but are
inside that restricted directory.

**Remediation**
- Create the directory with an explicit DACL granting only `SYSTEM` and `Administrators`,
  removing inherited `Users` access.
- Encrypt key material at rest with DPAPI (`ProtectedData`, `LocalMachine` scope).

### S2 — Server stores every client's private key (High, design)

Peers are generated server-side and the private key is persisted (`Core/Models.cs:36`) so
configs can be re-exported (`Services/VpnServer.cs:238`, `Core/ClientConfig.cs:12`). This
breaks WireGuard's model in which only the client knows its private key; compromising the
server (or reading `config.json`, see S1) compromises every client.

**Remediation**
- Generate the key pair on the client and upload only the public key (the server needs only
  `PublicKey` + assigned `/32`).
- If server-side generation must stay, encrypt the stored private keys with DPAPI.

### S3 — Config directory writable by `Users` (Medium)

The inherited DACL also grants `BUILTIN\Users:(I)(CI)(WD,AD,WEA,WA)` on the directory, i.e.
standard users can create files/subdirectories. On a fresh machine an unprivileged user can
pre-create `config.json` (or the `MyVpn` folder with attacker-controlled ACLs) before the
administrator first runs the app; `ConfigStore.Load` then trusts it, injecting attacker-known
server keys into an elevated process.

**Remediation**
- Apply the restrictive DACL from S1 at startup.
- Validate that `config.json` is owned by `SYSTEM`/`Administrators` before loading.

### S4 — Firewall rule on all profiles (Medium)

`New-NetFirewallRule ... -Profile Any` (`Services/NetworkConfigurator.cs:96,130`) exposes the
WireGuard UDP port on **Public** networks, so a roaming laptop keeps the port open on untrusted
Wi‑Fi.

**Remediation**
- Use `-Profile Domain,Private`, or open only the profile of the active network.

### S5 — No peer isolation; clients can reach the server's LAN (Medium)

Each peer's `AllowedIPs` on the server is its own `/32`, but there is no rule isolating peers
from each other, so one client can reach services listening on another client's tunnel IP.
Because IP forwarding and NAT are enabled with no destination restriction, a client can also
reach the **whole LAN behind the server** (router admin page, NAS, other hosts) with traffic
appearing to come from the server.

**Remediation**
- Add inbound/forward firewall rules on the WireGuard interface blocking traffic whose source
  and destination are both tunnel peers (or use per-peer `AllowedIPs` plus explicit rules).
- Restrict forwarded destinations if only internet egress is intended.

### S6 — Elevated PowerShell built from config (Low‑Medium)

`NetworkConfigurator` interpolates configuration values into a PowerShell script that runs
elevated with `-ExecutionPolicy Bypass` (`Services/NetworkConfigurator.cs`, `PowerShellRunner.cs`).
String fields are single-quote-escaped and the numeric fields are typed `int`, so no working
injection was found — but the pattern is fragile and amplifies S3.

**Remediation**
- Prefer the IP Helper API (as WireGuard's own example does) for address/MTU/forwarding.
- If scripts remain, validate values against a strict allow-list and drop `-ExecutionPolicy Bypass`.

### S7 — State persists after a crash (Low)

NAT/ICS, IP forwarding and the firewall rule are only reverted in `VpnServer.Stop()`. An
unhandled crash (or power loss) leaves the host acting as an internet router with forwarding
enabled and the WireGuard driver installed.

**Remediation**
- Reconcile/clean stale state on startup, or run the tunnel as a Windows service with recovery.

### S8 — IPv6 mismatch (Low)

The generated client config includes `AllowedIPs = ..., ::/0` (`Core/ClientConfig.cs:23`), but
the server only configures IPv4 `/32` peers and no IPv6 address. IPv6 from clients is
black-holed (dropped by cryptokey routing), which is not a leak but breaks IPv6 silently.

**Remediation**
- Configure an IPv6 tunnel address/peer, or remove `::/0` from the client config.

### S9 — Secrets on the clipboard and screen (Low)

`PeerDetailsDialog` copies the full config (including the private key) to the clipboard
(`UI/PeerDetailsDialog.cs:64`) and displays it as a QR code. The clipboard is readable by other
processes and may be captured by clipboard history/cloud sync.

**Remediation**
- Clear the clipboard after use; warn users that the `.conf`/QR is a secret.

### S10 — Public IP via third party (Low)

`NetworkConfigurator.TryGetPublicIp()` queries `https://api.ipify.org`. This discloses usage
to a third party and is a small supply-chain/availability dependency.

**Remediation**
- Make detection opt-in and/or allow a manually entered endpoint (already supported).

---

## Android findings

### A1 — `allowBackup="true"` can exfiltrate the client private key (High)

`app/src/main/AndroidManifest.xml:11` sets `android:allowBackup="true"`, and the imported
configuration — including the client private key — is stored in plaintext `SharedPreferences`
(`MainActivity.kt`: `getSharedPreferences(PREFS, MODE_PRIVATE)`). The key is therefore eligible
for Android auto-backup / device‑to‑device transfer, and readable on a debug build.

**Remediation**
- Set `android:allowBackup="false"` and add `android:dataExtractionRules`.
- Store the private key in `EncryptedSharedPreferences` backed by the Android Keystore.

### A2 — No kill switch (Medium)

The app establishes a full-tunnel `VpnService` but does not enforce "block connections without
VPN". If the tunnel drops or the process is killed, traffic reverts to cleartext. (GoBackend
protects its own UDP socket, but there is no general leak protection.)

**Remediation**
- Guide the user to enable Always-on VPN + "Block connections without VPN", and/or handle
  `VpnService.onRevoke`.

### A3 — Private key displayed without `FLAG_SECURE` (Low‑Medium)

The configuration is shown in an `EditText` with no `FLAG_SECURE`, so the private key appears
in the recents thumbnail and in screenshots/screen recordings.

**Remediation**
- Add `WindowManager.LayoutParams.FLAG_SECURE` to the activity window.

### A4 — Blind trust of scanned/pasted config (Medium)

A scanned QR (or pasted text) can point the endpoint at an attacker, change DNS, or use a
partial `AllowedIPs` to leak traffic, and the app connects without showing the operator what it
will use.

**Remediation**
- Parse and display `Endpoint`, `AllowedIPs` and `DNS`, and require explicit confirmation.

---

## What is already sound

- No custom cryptography; the Noise handshake and transport use the official implementations.
- Peer public keys are pinned, so redirecting the endpoint/DNS cannot complete a handshake
  without the server's private key.
- Server-side cryptokey routing rejects spoofed source addresses; unauthenticated packets are
  dropped.
- The library `VpnService` is `exported="false"` with `BIND_VPN_SERVICE`; no exported
  components other than the launcher activity.
- No cleartext tunnel transport and no dynamic code loading.

## Remaining / future work

1. **S8** — if IPv6 egress is ever required, add ULA tunnel addressing plus NAT66 (needs WinNAT;
   ICS is IPv4-only). Current behaviour is leak-free.
2. Rotate long-lived keys and optionally enable preshared keys.
3. Add startup reconciliation for stale NAT / forwarding state (**S7**).
4. Optionally move the remaining PowerShell address/forwarding setup to the IP Helper API
   (**S6** is no longer security-critical now that values are passed as environment variables).

## Re-audit — additional bugs fixed (robustness / correctness)

Found while reviewing the hardening changes. None are remote vulnerabilities, but two can crash
the process.

| ID | Issue | Fix |
|----|-------|-----|
| R1 | **Native use-after-free:** the stats timer (UI thread) read the WireGuard adapter handle while `Start`/`Stop` (worker thread) could free it. | Introduced a `_gate` lock around adapter mutation/reads in `VpnServer`, lock-free `Volatile.Read` for status getters, and a `ThrowIfDisposed` guard in `WireGuardAdapter`. |
| R2 | A corrupt or `null`-valued `config.json` threw during startup and bricked the app. | `ConfigStore.Load` now catches JSON/IO errors, moves the bad file aside (`.corrupt-<timestamp>`), and null-guards `Server`/`Peers`. |
| R3 | `SecretStore.save()` could throw on a Keystore error and crash the Android app. | Wrapped in try/catch with a user-visible message. |
| R4 | A config with more than one peer only displayed the first peer in the connection-confirmation dialog (could hide a malicious peer). | The dialog now lists every peer's endpoint and routes. |
| R5 | The clipboard-clearing `System.Windows.Forms.Timer` had no rooted reference and could be garbage-collected before firing. | Held in a field and disposed when the dialog closes. |
| R6 | The peer dialog was not disposed (leak) and its timer could fire after close. | Call sites use `using`; the timer is stopped/disposed in `OnFormClosed`. |
| R7 | ICS teardown disabled **all** connection sharing on the machine, including unrelated setups. | Teardown now disables only the public/private connections this app used. |
| R8 | The UI could block on the `_gate` lock if it queried status while `Start` held it during slow PowerShell work. | Status getters are lock-free (`Volatile.Read`). |



