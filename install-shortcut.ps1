# Creates the desktop shortcuts for MyVpn:
#   MyVpn Server  -> start everything needed for the VPN server (WSL + WireGuard). ONE CLICK.
#   MyVpn App     -> open the WinForms app (optional; NOT part of the server).
#   MyVpn Stop    -> stop the server (shut down the WSL distro).
#
# Run once:  powershell -ExecutionPolicy Bypass -File .\install-shortcut.ps1

$RepoDir   = 'D:\Projects\MyVpn'
$startPs1  = Join-Path $RepoDir 'start-myvpn-server.ps1'
$desktop   = [Environment]::GetFolderPath('Desktop')

$appExe = @(
    (Join-Path $RepoDir 'server\MyVpn.Server\bin\Release\net10.0-windows7.0\MyVpn.Server.exe'),
    (Join-Path $RepoDir 'server\MyVpn.Server\bin\Debug\net10.0-windows7.0\MyVpn.Server.exe'),
    (Join-Path $RepoDir 'server\MyVpn.Server\bin\Release\net10.0-windows\MyVpn.Server.exe'),
    (Join-Path $RepoDir 'server\MyVpn.Server\bin\Debug\net10.0-windows\MyVpn.Server.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

$shell = New-Object -ComObject WScript.Shell

# Remove old shortcuts from earlier revisions.
foreach ($old in 'MyVpn.lnk', 'MyVpn (stop).lnk') {
    $p = Join-Path $desktop $old
    if (Test-Path $p) { Remove-Item $p -Force }
}

# --- MyVpn Server (the one click you want) ---
$lnk = Join-Path $desktop 'MyVpn Server.lnk'
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = 'powershell.exe'
$sc.Arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $startPs1 + '"'
$sc.WorkingDirectory = $RepoDir
$sc.Description = 'Start the MyVpn VPN server (WSL + WireGuard)'
if ($appExe) { $sc.IconLocation = "$appExe,0" }
$sc.Save()
Write-Host "Created: $lnk"

# --- MyVpn App (optional) ---
if ($appExe) {
    $lnk = Join-Path $desktop 'MyVpn App.lnk'
    $sc = $shell.CreateShortcut($lnk)
    $sc.TargetPath = $appExe
    $sc.WorkingDirectory = Split-Path $appExe
    $sc.Description = 'Open the MyVpn Windows app (not required for the server)'
    $sc.IconLocation = "$appExe,0"
    $sc.Save()
    Write-Host "Created: $lnk"
} else {
    Write-Host "Skipped 'MyVpn App' (app not built; run: dotnet build `"$RepoDir\MyVpn.slnx`" -c Release)"
}

# --- MyVpn Stop ---
$lnk = Join-Path $desktop 'MyVpn Stop.lnk'
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = 'powershell.exe'
$sc.Arguments = '-NoProfile -WindowStyle Hidden -Command "wsl.exe --terminate Ubuntu"'
$sc.WorkingDirectory = $RepoDir
$sc.Description = 'Stop the MyVpn VPN server'
if ($appExe) { $sc.IconLocation = "$appExe,0" }
$sc.Save()
Write-Host "Created: $lnk"
