param(
    [string]$PackageDirectory = $PSScriptRoot,
    [switch]$NoPathUpdate
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PackageDirectory).Path
$target = Join-Path $env:LOCALAPPDATA 'Programs\xas'
foreach ($name in @('xas.exe', 'Xas.Daemon.exe')) {
    $file = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Package is missing ${name}: $file" }
}
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'xas.exe') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $source 'Xas.Daemon.exe') -Destination $target -Force

if (-not $NoPathUpdate) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($userPath -split ';' | Where-Object { $_ })
    if (-not ($entries | Where-Object { [string]::Equals($_.TrimEnd('\'), $target.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) })) {
        $updated = (@($entries) + $target) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
    }
}
Write-Host "Installed xas and Xas.Daemon to $target"
if ($NoPathUpdate) { Write-Host 'PATH was not changed (-NoPathUpdate).' }
else { Write-Host 'User PATH includes the install directory. Open a new terminal to use xas.' }
