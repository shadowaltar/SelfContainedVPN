# Creates the single desktop shortcut for MyVpn: opens the server GUI.
# Run once:  powershell -ExecutionPolicy Bypass -File .\install-shortcut.ps1

$RepoDir = 'D:\Projects\MyVpn'
$desktop = [Environment]::GetFolderPath('Desktop')

$appExe = @(
    (Join-Path $RepoDir 'server\MyVpn.Server\bin\Release\net10.0-windows7.0\MyVpn.Server.exe'),
    (Join-Path $RepoDir 'server\MyVpn.Server\bin\Debug\net10.0-windows7.0\MyVpn.Server.exe'),
    (Join-Path $RepoDir 'server\MyVpn.Server\bin\Release\net10.0-windows\MyVpn.Server.exe'),
    (Join-Path $RepoDir 'server\MyVpn.Server\bin\Debug\net10.0-windows\MyVpn.Server.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

$shell = New-Object -ComObject WScript.Shell

# Remove shortcuts from earlier revisions so only one remains.
foreach ($old in 'MyVpn App.lnk', 'MyVpn Server.lnk', 'MyVpn Stop.lnk') {
    $p = Join-Path $desktop $old
    if (Test-Path $p) { Remove-Item $p -Force }
}

if (-not $appExe) {
    Write-Host "Build the app first:  dotnet build `"$RepoDir\MyVpn.slnx`" -c Release"
    return
}

$lnk = Join-Path $desktop 'MyVpn.lnk'
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = $appExe
$sc.WorkingDirectory = Split-Path $appExe
$sc.IconLocation = "$appExe,0"
$sc.Description = 'MyVpn Server - GUI (start/stop the WSL WireGuard server, manage peers)'
$sc.Save()
Write-Host "Created: $lnk"
