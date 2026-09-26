#requires -RunAsAdministrator
param(
    [string]$PackageDirectory = $PSScriptRoot,
    [switch]$NoPathUpdate
)

# The service runs as LocalSystem, so its binaries live in the admin-writable Program Files tree.

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PackageDirectory).Path
$target = Join-Path $env:ProgramFiles 'xas'
$winFspVersion = [version]'2.1.25156'
$winFspFile = 'winfsp-2.1.25156.msi'
$winFspSha256 = '073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A'
$daemonTaskName = 'Xas User Daemon'
$existingDaemonTask = Get-ScheduledTask -TaskName $daemonTaskName -ErrorAction SilentlyContinue
if ($existingDaemonTask) { Stop-ScheduledTask -TaskName $daemonTaskName -ErrorAction SilentlyContinue }
$existing = Get-Service -Name 'XasAdminBroker' -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Stop-Service -Name 'XasAdminBroker' -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
}
foreach ($name in @('xas.exe', 'Xas.Daemon.exe', 'Xas.PrivilegedService.exe', $winFspFile)) {
    $file = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Package is missing ${name}: $file" }
}

function Get-InstalledWinFspVersion {
    $versions = @()
    foreach ($path in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )) {
        foreach ($item in @(Get-ItemProperty $path -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'WinFsp*' })) {
            try { if ($item.DisplayVersion) { $versions += [version]$item.DisplayVersion } } catch { }
        }
    }
    if ($versions.Count -eq 0) { return $null }
    return ($versions | Sort-Object -Descending | Select-Object -First 1)
}

$installedWinFsp = Get-InstalledWinFspVersion
if ($null -eq $installedWinFsp -or $installedWinFsp -lt $winFspVersion) {
    $winFspMsi = Join-Path $source $winFspFile
    $actualHash = (Get-FileHash -LiteralPath $winFspMsi -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, $winFspSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Bundled WinFsp installer failed SHA-256 verification. Expected $winFspSha256, got $actualHash."
    }
    Write-Host "Installing WinFsp $winFspVersion for remote-drive support..."
    $msi = Start-Process -FilePath msiexec.exe -ArgumentList @('/i', "`"$winFspMsi`"", '/qn', '/norestart') -Wait -PassThru
    if ($msi.ExitCode -notin @(0, 3010)) { throw "WinFsp installation failed with MSI exit code $($msi.ExitCode)." }
    if ($msi.ExitCode -eq 3010) { Write-Warning 'WinFsp requested a reboot. XAS was installed, but remote drives may require a reboot before they can mount.' }
    $installedWinFsp = Get-InstalledWinFspVersion
    if ($null -eq $installedWinFsp -or $installedWinFsp -lt $winFspVersion) {
        throw "WinFsp installation completed but version $winFspVersion or newer was not detected."
    }
} else {
    Write-Host "WinFsp $installedWinFsp is already installed."
}

New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'xas.exe') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $source 'Xas.Daemon.exe') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $source 'Xas.PrivilegedService.exe') -Destination $target -Force

# Older packages launched the user daemon from Task Scheduler. The Windows service now supervises the
# daemon in the active interactive session instead, so remove the legacy task to guarantee one owner.
if ($existingDaemonTask) {
    Unregister-ScheduledTask -TaskName $daemonTaskName -Confirm:$false
}

$servicePath = Join-Path $target 'Xas.PrivilegedService.exe'
$existing = Get-Service -Name 'XasAdminBroker' -ErrorAction SilentlyContinue
if ($existing) {
    & sc.exe delete XasAdminBroker | Out-Null
    for ($attempt = 0; $attempt -lt 20 -and (Get-Service -Name 'XasAdminBroker' -ErrorAction SilentlyContinue); $attempt++) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-Service -Name 'XasAdminBroker' -ErrorAction SilentlyContinue) { throw 'The existing XasAdminBroker service is still being removed.' }
}
& sc.exe create XasAdminBroker "binPath= `"$servicePath`"" 'start= auto' 'obj= LocalSystem' 'DisplayName= XAS Background Service' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not register the Xas administrator broker service.' }
& sc.exe description XasAdminBroker 'Supervises the interactive Xas daemon and runs approved administrator commands.' | Out-Null
& sc.exe failure XasAdminBroker 'reset= 86400' 'actions= restart/5000/restart/15000/restart/30000' | Out-Null
& sc.exe failureflag XasAdminBroker 1 | Out-Null
Start-Service -Name 'XasAdminBroker'

if (-not $NoPathUpdate) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($userPath -split ';' | Where-Object { $_ })
    if (-not ($entries | Where-Object { [string]::Equals($_.TrimEnd('\'), $target.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) })) {
        $updated = (@($entries) + $target) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
    }
}
Write-Host "Installed xas and the XasAdminBroker service to $target. The service keeps Xas.Daemon running in the active user session."
if ($NoPathUpdate) { Write-Host 'PATH was not changed (-NoPathUpdate).' }
else { Write-Host 'User PATH includes the install directory. Open a new terminal to use xas.' }
