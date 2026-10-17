# Installs the freshly built driver from out\. Run elevated (install.cmd elevates for you).
#   install.ps1                       install / update MagicKeyboardFilter
#   install.ps1 -RemoveVendorDriver   also remove Magic Utilities' keyboard driver package first
param([switch]$RemoveVendorDriver)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$inf = Join-Path $here 'out\MagicKeyboardFilter.inf'
$keyboardPids = '0267|026C|029C|029A|029F|0320|0321|0322'

function Get-DriverPackages {
    # Parse "pnputil /enum-drivers" into objects.
    $cur = $null
    foreach ($line in (pnputil /enum-drivers)) {
        if ($line -match '^\s*Published Name:\s*(\S+)') { if ($cur) { $cur }; $cur = [ordered]@{ Published = $Matches[1] } }
        elseif ($cur -and $line -match '^\s*Original Name:\s*(\S+)') { $cur.Original = $Matches[1] }
        elseif ($cur -and $line -match '^\s*Provider Name:\s*(.+)$') { $cur.Provider = $Matches[1].Trim() }
        elseif ($cur -and $line -match '^\s*Driver Version:\s*(.+)$') { $cur.Version = $Matches[1].Trim() }
    }
    if ($cur) { $cur }
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an elevated prompt (or use install.cmd).' }
if (-not (Test-Path $inf)) { throw "Build first: $here\build.cmd" }

# The options Windows actually booted with (bcdedit shows the next-boot setting, and -notmatch on its
# multi-line output filters lines instead of returning a boolean).
$bootOptions = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control').SystemStartOptions
if ($bootOptions -notmatch '\bTESTSIGNING\b') {
    Write-Warning 'Test signing is OFF in this boot. Run "bcdedit /set testsigning on" (Secure Boot must be off), reboot, then rerun.'
    exit 1
}

$vendor = @(Get-DriverPackages | Where-Object { $_.Original -ieq 'magickeyboard.inf' -and $_.Provider -like 'Magic Utilities*' })
if ($vendor.Count -gt 0) {
    if (-not $RemoveVendorDriver) {
        Write-Host 'Magic Keyboard Utilities'' driver is still installed. Both filters bind to the same keyboard.' -ForegroundColor Yellow
        $vendor | ForEach-Object { Write-Host "  $($_.Published)  $($_.Version)" }
        Write-Host 'Either uninstall "Magic Keyboard Utilities" from Settings > Apps (recommended), or rerun with:'
        Write-Host '  install.cmd -RemoveVendorDriver'
        exit 1
    }
    foreach ($v in $vendor) {
        Write-Host "Removing vendor driver $($v.Published) ($($v.Version))..."
        pnputil /delete-driver $v.Published /uninstall /force | Out-Host
    }
}

Write-Host 'Closing MagicKeyboard app (its open handle would defer the update to a reboot)...'
Get-Process MagicKeyboard -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$cer = Join-Path $here 'out\MagicKeyboardTest.cer'
if (Test-Path $cer) {
    certutil -f -addstore Root $cer | Out-Null
    certutil -f -addstore TrustedPublisher $cer | Out-Null
}

Write-Host 'Installing MagicKeyboardFilter...'
pnputil /add-driver $inf /install | Out-Host

Write-Host 'Restarting Magic Keyboard devices...'
Get-PnpDevice -PresentOnly | Where-Object {
    $_.InstanceId -match "^BTHENUM\\\{00001124-.*_VID&0001004C_PID&($keyboardPids)\\" -or
    $_.InstanceId -match "^USB\\VID_05AC&PID_($keyboardPids)&MI_01\\"
} | ForEach-Object { Write-Host "  $($_.InstanceId)"; pnputil /restart-device "$($_.InstanceId)" | Out-Null }

Write-Host 'Done. If pnputil reported "pending system reboot", reboot once to finish.' -ForegroundColor Green
Write-Host 'Check with: KeyProbe info   (tools\KeyProbe)'
