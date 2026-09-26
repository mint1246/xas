#requires -RunAsAdministrator
param(
    [string]$PackageDirectory = $PSScriptRoot,
    [switch]$NoPathUpdate
)

# The service runs as LocalSystem, so its binaries live in the admin-writable Program Files tree.

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PackageDirectory).Path
$target = Join-Path $env:ProgramFiles 'xas'
$daemonTaskName = 'Xas User Daemon'
$existingDaemonTask = Get-ScheduledTask -TaskName $daemonTaskName -ErrorAction SilentlyContinue
if ($existingDaemonTask) {
    Stop-ScheduledTask -TaskName $daemonTaskName -ErrorAction SilentlyContinue
}
$existing = Get-Service -Name 'XasAdminBroker' -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Stop-Service -Name 'XasAdminBroker' -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
}
foreach ($name in @('xas.exe', 'Xas.Daemon.exe', 'Xas.PrivilegedService.exe')) {
    $file = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Package is missing ${name}: $file" }
}
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'xas.exe') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $source 'Xas.Daemon.exe') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $source 'Xas.PrivilegedService.exe') -Destination $target -Force

# Run the normal daemon in the logged-in user's interactive session. The packaged Windows daemon
# is built as WinExe, so launching it directly does not allocate a console window.
$daemonPath = Join-Path $target 'Xas.Daemon.exe'
$installingIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$userSid = $installingIdentity.User.Value
$userName = $installingIdentity.Name
$daemonAction = New-ScheduledTaskAction -Execute $daemonPath -Argument 'serve'
$daemonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userName
$daemonPrincipal = New-ScheduledTaskPrincipal -UserId $userSid -LogonType Interactive -RunLevel Limited
$daemonSettings = New-ScheduledTaskSettingsSet -Hidden -StartWhenAvailable -RestartCount 5 `
    -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $daemonTaskName -Action $daemonAction -Trigger $daemonTrigger `
    -Principal $daemonPrincipal -Settings $daemonSettings -Force | Out-Null

$servicePath = Join-Path $target 'Xas.PrivilegedService.exe'
$existing = Get-Service -Name 'XasAdminBroker' -ErrorAction SilentlyContinue
if ($existing) {
    & sc.exe delete XasAdminBroker | Out-Null
    for ($attempt = 0; $attempt -lt 20 -and (Get-Service -Name 'XasAdminBroker' -ErrorAction SilentlyContinue); $attempt++) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-Service -Name 'XasAdminBroker' -ErrorAction SilentlyContinue) { throw 'The existing XasAdminBroker service is still being removed.' }
}
& sc.exe create XasAdminBroker "binPath= `"$servicePath`"" 'start= auto' 'obj= LocalSystem' 'DisplayName= Xas Administrator Broker' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not register the Xas administrator broker service.' }
& sc.exe description XasAdminBroker 'Runs approved Xas commands with the active administrator user token.' | Out-Null
& sc.exe failure XasAdminBroker 'reset= 86400' 'actions= restart/5000/restart/15000/restart/30000' | Out-Null
& sc.exe failureflag XasAdminBroker 1 | Out-Null
Start-Service -Name 'XasAdminBroker'
Start-ScheduledTask -TaskName $daemonTaskName

if (-not $NoPathUpdate) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($userPath -split ';' | Where-Object { $_ })
    if (-not ($entries | Where-Object { [string]::Equals($_.TrimEnd('\'), $target.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) })) {
        $updated = (@($entries) + $target) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
    }
}
Write-Host "Installed xas, the per-user Xas daemon, and the XasAdminBroker service to $target"
if ($NoPathUpdate) { Write-Host 'PATH was not changed (-NoPathUpdate).' }
else { Write-Host 'User PATH includes the install directory. Open a new terminal to use xas.' }
