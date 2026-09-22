# Windows 11 repair upgrade (in-place) — step-by-step

Goal: reinstall Windows 11 over itself, keeping your files and apps, to restore the missing
**WinNAT** component (and a healthy Internet Connection Sharing). This is not a reset and does not
erase your data.

## Your system (verify it still matches before starting)

| Property | Value |
|---|---|
| Edition | Windows 11 Home China (`CoreCountrySpecific`, "家庭中文版") |
| Version | 25H2 |
| Build | 26200.**9457** |
| Architecture | x64 (64-bit) |
| UI language | zh-CN (Chinese Simplified) |
| Free space on C: | ~31 GB (→ free up first, see below) |

> Critical: a repair upgrade only offers **Keep personal files and apps** when the installation
> media matches the **same edition (Home China), architecture (x64), UI language (zh-CN), and a
> build ≥ 26200**. If those don't match, the "keep apps" option is greyed out.

---

## 0. Before you start

1. **Back up anything you cannot lose** to another drive / cloud (documents, keys, the
   `%ProgramData%\MyVpn` folder if you want to keep your VPN config, project folders).
2. **Free up disk space to at least 40 GB** on C: (31 GB is tight).
   - `Settings → System → Storage → Temporary files` → remove.
   - `cleanmgr` (Disk Cleanup) → as admin → "Clean up system files" → check
     "Windows Update Cleanup", "Delivery Optimization Files", "Previous Windows installations".
   - Delete large files you don't need.
   - Verify: `Get-PSDrive C` → `Free` should be ≥ 40 GB.
3. **Create a restore point** (optional but recommended):
   ```powershell
   Enable-ComputerRestore -Drive "C:\"
   Checkpoint-Computer -Description "Before repair upgrade" -RestorePointType MODIFY_SETTINGS
   ```
4. **Plug the PC into power** and make sure the internet is stable.
5. Disable any third-party antivirus (Windows Defender is fine) for the duration.
6. Make sure BitLocker, if used, is suspended or you have the recovery key. (Check:
   `manage-bde -status`).

You do **not** need to uninstall the MyVpn app or the WireGuard driver.

---

## 1. Get matching installation media

Pick **one** route.

### Route A — Installation Assistant (easiest; matches your edition automatically)

1. Open <https://www.microsoft.com/software-download/windows11>.
2. Under **Windows 11 Installation Assistant**, click **Download now**.
3. Run `Windows11InstallationAssistant.exe` and follow it.
   - It downloads the current Windows 11 release for your edition and runs an in-place upgrade,
     keeping files and apps.
   - If it says you are already up to date, use Route B instead.

### Route B — ISO (most reliable for a repair)

1. On the same page, under **Download Windows 11 Disk Image (ISO) for x64 devices**, select
   **Windows 11 (multi-edition ISO)**.
2. Choose **Chinese (Simplified)** as the product language → **Confirm** → **64-bit Download**.
3. Save the ISO (several GB).
4. **Check the ISO contains your edition before committing** (optional but wise):
   - Right-click the ISO → **Mount**, then in the DVD drive open `sources\`.
   - Run PowerShell:
     ```powershell
     Get-WindowsImage -ImagePath "D:\sources\install.wim" | Select-Object ImageName, ImageIndex
     # (if it's install.esd instead of .wim:)
     dism /Get-WimInfo /WimFile:D:\sources\install.esd
     ```
   - You are looking for **"Windows 11 Home"** — the generic ISO usually lists *Windows 11 Home*,
     *Home N*, *Home Single Language*, *Pro*, etc.
   - **Important caveat:** your machine is the OEM **"Home China" (`CoreCountrySpecific`)** edition,
     which is **not always present** in the generic ISO. If it isn't, setup may refuse to keep your
     apps. If that happens:
     - Use **Route A** (Installation Assistant), which pulls the exact edition, **or**
     - Get the **"Windows 11 家庭中文版" ISO** from your PC maker's recovery/support site, **or**
     - Use the **Media Creation Tool** (below).

### Route C — Media Creation Tool (builds matching media)

1. On the same page, click **Download tool now** under *Create Windows 11 Installation Media*.
2. Run `mediacreationtool.exe` → accept → it produces an ISO/USB for the current release, and (when
   available in your region) the "Upgrade this PC now" path keeps files and apps.

---

## 2. Run the repair upgrade (keep files and apps)

1. **Mount the ISO**: double-click the `.iso` file. It appears as a drive (e.g., `E:`).
   (If you used a USB stick, open that drive.)
2. From that drive, run **`setup.exe`** (NOT `sources\setup.exe`).
3. **Accept the UAC prompt.**
4. On *Install Windows 11*: make sure **"Download updates, drivers and optional features"** is
   selected (recommended), then click **Next**.
5. Accept the license terms.
6. Wait while it checks for updates.
7. On **"Choose what to keep"**, select:
   - ✅ **Keep personal files and apps**  ← this is the whole point
   - ⛔ Do **not** choose "Nothing" or "Keep personal files only" (those remove apps).
8. Click **Next** → **Install**.
9. The PC will reboot **several times**. Do **not** power it off, close the lid, or interrupt it.
   It typically takes 30–60 minutes.
10. Sign in when it finishes. Your files, apps and the MyVpn project remain.

---

## 3. Verify the repair worked

Run these in an **elevated** PowerShell.

```powershell
# 1) Build/edition unchanged (should be 26200 or newer)
Get-ComputerInfo | Select-Object WindowsProductName, WindowsVersion, OsBuildNumber

# 2) WinNAT provider is now present (the whole reason for the upgrade)
Get-CimClass -Namespace root/StandardCimv2 -ClassName MSFT_NetNat | Select-Object CimClassName

# 3) NAT actually works
New-NetNat -Name "testnat" -InternalIPInterfaceAddressPrefix "10.77.0.0/24"
Get-NetNat
Remove-NetNat -Name "testnat" -Confirm:$false
```

If step 2/3 succeed, the app will use the **WinNat** backend automatically. Then:

```powershell
# 4) Run the server and confirm
& "D:\Projects\MyVpn\server\MyVpn.Server\bin\Debug\net10.0-windows7.0\MyVpn.Server.exe" --diagnostics --netcheck
# The log at %TEMP%\myvpn-diagnostics.log should say "NAT backend detected: WinNat"
# and "networking applied".
```

Then launch the GUI, set the Public endpoint, click **Start server**. The status should be green
(`... (WinNat)`).

---

## 4. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "This PC can't be upgraded because the version is newer" | ISO build is older than 26200. Get a **newer** ISO (25H2 or later). |
| "Keep personal files and apps" is **greyed out** | Edition/language mismatch. Your edition is **Home China (CoreCountrySpecific)** zh-CN. Use Route A, the OEM China Home ISO, or match language exactly. |
| Setup says "already up to date" | Run Route B/C with an ISO of a **newer** build, or use the Media Creation Tool's upgrade path. |
| ISO download page only offers older builds | Use the **Media Creation Tool** (Route C) — it fetches the current release. |
| Not enough space | Free at least 40 GB on C: (Section 0.2). |
| Upgrade fails/rolls back | Look in `C:\$WINDOWS.~BT\Sources\Panther\setuperr.log` and `setupact.log`. |
| Still no `MSFT_NetNat` after the upgrade | The media didn't reinstall the component; try the Installation Assistant (Route A), or a clean install. |

> Tip: always use media from Microsoft (or your PC maker's recovery), never a third-party
> "debloated"/modified image — that is the most likely reason the WinNAT component is missing now.
