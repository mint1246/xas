#requires -RunAsAdministrator
param(
    [string]$PackageDirectory = $PSScriptRoot,
    [switch]$NoPathUpdate,
    [switch]$NoStart
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
    if ($existing.Status -ne 'StopPending') {
        try { Stop-Service -Name 'XasAdminBroker' -Force }
        catch {
            $existing.Refresh()
            if ($existing.Status -notin @('Stopped', 'StopPending')) { throw }
        }
    }
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

# The service normally owns the interactive daemon and should reap it on shutdown. During an in-place
# upgrade, be defensive: a crashed/older supervisor may leave that user-session process alive, which keeps
# Xas.Daemon.exe locked and prevents the new package from replacing it. Kill only the installed daemon path.
$installedDaemonPath = Join-Path $target 'Xas.Daemon.exe'
function Get-InstalledXasDaemonProcesses {
    $expected = [IO.Path]::GetFullPath($installedDaemonPath)
    @(Get-CimInstance Win32_Process -Filter "Name='Xas.Daemon.exe'" -ErrorAction SilentlyContinue | Where-Object {
        $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $expected, [StringComparison]::OrdinalIgnoreCase)
    })
}

# Give a current supervisor a brief chance to perform its normal cleanup first.
for ($attempt = 0; $attempt -lt 12 -and (Get-InstalledXasDaemonProcesses).Count -gt 0; $attempt++) {
    Start-Sleep -Milliseconds 250
}
foreach ($process in @(Get-InstalledXasDaemonProcesses)) {
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
}
for ($attempt = 0; $attempt -lt 20 -and (Get-InstalledXasDaemonProcesses).Count -gt 0; $attempt++) {
    Start-Sleep -Milliseconds 100
}
if ((Get-InstalledXasDaemonProcesses).Count -gt 0) {
    throw 'Could not stop the installed Xas.Daemon process for upgrade.'
}
foreach ($name in @('xas.exe', 'Xas.Daemon.exe', 'Xas.PrivilegedService.exe', $winFspFile)) {
    $file = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Package is missing ${name}: $file" }
}
$daemonRuntime = Join-Path $source 'daemon-runtime'
if (-not (Test-Path -LiteralPath (Join-Path $daemonRuntime 'Xas.Daemon.runtimeconfig.json') -PathType Leaf)) {
    throw "Package is missing the loose Xas.Daemon runtime dependencies: $daemonRuntime"
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
Copy-Item -Path (Join-Path $daemonRuntime '*') -Destination $target -Recurse -Force

# Older packages launched the user daemon from Task Scheduler. The Windows service now supervises the
# daemon in the active interactive session instead, so remove the legacy task to guarantee one owner.
if ($existingDaemonTask) {
    Unregister-ScheduledTask -TaskName $daemonTaskName -Confirm:$false
}

$servicePath = Join-Path $target 'Xas.PrivilegedService.exe'
$existing = Get-Service -Name 'XasAdminBroker' -ErrorAction SilentlyContinue
if (-not $existing) {
    New-Service -Name 'XasAdminBroker' -BinaryPathName "`"$servicePath`"" -StartupType Automatic `
        -DisplayName 'XAS Background Service' | Out-Null
} else {
    # The install path is stable, so upgrades do not need to delete/recreate the service. Reusing it avoids
    # the SCM's asynchronous "marked for deletion" state and makes in-place upgrades deterministic.
    Set-Service -Name 'XasAdminBroker' -StartupType Automatic -DisplayName 'XAS Background Service'
}
& sc.exe description XasAdminBroker 'Supervises the interactive Xas daemon and runs approved administrator commands.' | Out-Null
& sc.exe failure XasAdminBroker 'reset= 86400' 'actions= restart/5000/restart/15000/restart/30000' | Out-Null
& sc.exe failureflag XasAdminBroker 1 | Out-Null
if (-not $NoStart) { Start-Service -Name 'XasAdminBroker' }

if (-not $NoPathUpdate) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($userPath -split ';' | Where-Object { $_ })
    if (-not ($entries | Where-Object { [string]::Equals($_.TrimEnd('\'), $target.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) })) {
        $updated = (@($entries) + $target) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
    }
}
Write-Host "Installed xas and the XasAdminBroker service to $target."
if ($NoStart) { Write-Host 'The service remains stopped (-NoStart). Start it with: Start-Service XasAdminBroker' }
else { Write-Host 'The service keeps Xas.Daemon running in the active user session.' }
if ($NoPathUpdate) { Write-Host 'PATH was not changed (-NoPathUpdate).' }
else { Write-Host 'User PATH includes the install directory. Open a new terminal to use xas.' }
