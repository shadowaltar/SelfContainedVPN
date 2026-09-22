# One-click start for the MyVpn server side.
# Starts the WSL Ubuntu distro, brings up the WireGuard interface (wg0) and keeps WSL alive.
# The VPN server runs inside Linux, so nothing on the Windows side is required.

$ErrorActionPreference = 'SilentlyContinue'
$Distro = 'Ubuntu'

# Start WSL, ensure wg0 is up, and keep the distro alive (hidden).
# WSL shuts down its VM when no process is running, which would stop the VPN - so we hold it.
$wslArgs = @(
    '-d', $Distro, '-u', 'root', '--',
    'bash', '-lc', 'pkill -f "sleep 2147483647"; systemctl start wg-quick@wg0 >/dev/null 2>&1; exec sleep 2147483647'
)
Start-Process -FilePath 'wsl.exe' -ArgumentList $wslArgs -WindowStyle Hidden

# Wait until the server is really listening (up to ~20s).
$ok = $false
for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Seconds 1
    $out = (& wsl.exe -d $Distro -u root -- wg show wg0) 2>$null
    if ($out -match 'listening port') { $ok = $true; break }
}

# Show a little confirmation balloon (no window, no click needed).
try {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $ni = New-Object System.Windows.Forms.NotifyIcon
    $ni.Icon = [System.Drawing.SystemIcons]::Information
    $ni.Visible = $true
    if ($ok) {
        $ni.ShowBalloonTip(5000, 'MyVpn Server', 'The VPN server is up (wg0, UDP 51820). You can connect the phone now.', 'Info')
    } else {
        $ni.ShowBalloonTip(8000, 'MyVpn Server', 'Could not confirm wg0. Open "Ubuntu" once and check.', 'Warning')
    }
    Start-Sleep -Seconds 6
    $ni.Dispose()
} catch { }
