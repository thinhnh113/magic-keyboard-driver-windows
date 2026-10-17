# Removes MagicKeyboardFilter. The keyboard falls back to the stock Windows driver (plain keys).
# Run elevated (uninstall.cmd elevates for you). Reinstall Magic Keyboard Utilities afterwards if wanted.
$ErrorActionPreference = 'Stop'
$keyboardPids = '0267|026C|029C|029A|029F|0320|0321|0322'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an elevated prompt (or use uninstall.cmd).' }

Get-Process MagicKeyboard -ErrorAction SilentlyContinue | Stop-Process -Force

$published = $null; $ours = @()
foreach ($line in (pnputil /enum-drivers)) {
    if ($line -match '^\s*Published Name:\s*(\S+)') { $published = $Matches[1] }
    elseif ($line -match '^\s*Original Name:\s*magickeyboardfilter\.inf') { $ours += $published }
}
if ($ours.Count -eq 0) { Write-Host 'MagicKeyboardFilter is not installed.'; exit 0 }
foreach ($p in $ours) { Write-Host "Removing $p..."; pnputil /delete-driver $p /uninstall /force | Out-Host }

Get-PnpDevice -PresentOnly | Where-Object {
    $_.InstanceId -match "^BTHENUM\\\{00001124-.*_VID&0001004C_PID&($keyboardPids)\\" -or
    $_.InstanceId -match "^USB\\VID_05AC&PID_($keyboardPids)&MI_01\\"
} | ForEach-Object { pnputil /restart-device "$($_.InstanceId)" | Out-Null }
Write-Host 'Removed. Reboot if the keyboard does not respond.' -ForegroundColor Green
