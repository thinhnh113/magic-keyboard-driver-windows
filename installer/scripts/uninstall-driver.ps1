# Removes the MagicKeyboard filter driver (MagicKeyboardFilter) so the keyboard falls back to the stock Windows driver.
# Run elevated. Exit codes: 0 = done, 3010 = done but a reboot is required.
param([string]$LogFile)
$ErrorActionPreference = 'Continue'

function Log([string]$Message) {
    $line = "$(Get-Date -Format s) $Message"
    Write-Output $line
    if ($LogFile) { Add-Content -Path $LogFile -Value $line -Encoding UTF8 }
}

$reboot = $false

# Every staged copy of our package (each build has its own oemNN.inf). Get-WindowsDriver is
# locale-independent, unlike parsing pnputil's text output.
$packages = Get-WindowsDriver -Online -All -ErrorAction SilentlyContinue |
    Where-Object { $_.OriginalFileName -like '*\magickeyboardfilter.inf' }
foreach ($p in $packages) {
    # /uninstall removes the driver from devices using it; they are re-installed with the stock driver.
    $out = pnputil /delete-driver $p.Driver /uninstall /force 2>&1
    Log "delete $($p.Driver) ($($p.Version)): exit $LASTEXITCODE $($out -join ' ')"
    if ($LASTEXITCODE -eq 3010) { $reboot = $true }
}

pnputil /scan-devices | Out-Null

# Service entry left behind by the driver package. This also removes the config the driver persisted
# under its Parameters key.
if (Get-Service -Name MagicKeyboardFilter -ErrorAction SilentlyContinue) {
    sc.exe delete MagicKeyboardFilter | Out-Null
    Log 'Deleted service MagicKeyboardFilter'
}

# Test certificate trusted by install-driver.ps1 (only ours: MagicMouse has its own).
foreach ($store in 'Root', 'TrustedPublisher') {
    Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object Subject -eq 'CN=MagicKeyboard Test' | ForEach-Object {
        Remove-Item $_.PSPath
        Log "Removed test certificate from $store"
    }
}

if ($reboot) { Log 'Reboot required'; exit 3010 }
Log 'Driver removed'
exit 0
