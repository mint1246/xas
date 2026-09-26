param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishRoot = Join-Path $root 'artifacts\publish'
$releaseRoot = Join-Path $root 'artifacts\release'
$nuget = 'https://api.nuget.org/v3/index.json'

function Remove-WorkspaceTree([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    $rootFull = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing recursive deletion outside workspace: $full"
    }
    if (Test-Path -LiteralPath $full) {
        $item = Get-Item -LiteralPath $full -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing recursive deletion of a reparse point: $full"
        }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

foreach ($rid in @('win-x64', 'linux-x64')) {
    $stage = Join-Path $publishRoot $rid
    Remove-WorkspaceTree $stage
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $projects = if ($rid -eq 'win-x64') { @('Xas.Cli', 'Xas.Daemon', 'Xas.PrivilegedService') } else { @('Xas.Cli', 'Xas.Daemon') }
    foreach ($project in $projects) {
        $out = Join-Path $stage $project
        $publishArgs = @('publish', (Join-Path $root "src\$project\$project.csproj"), '-c', $Configuration,
            '-r', $rid, '--self-contained', 'true', '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true', "-p:RestoreSources=$nuget", '-o', $out)
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project ($rid)" }
    }
    $package = Join-Path $releaseRoot $rid
    Remove-WorkspaceTree $package
    New-Item -ItemType Directory -Force -Path $package | Out-Null
    $exeExt = if ($rid -eq 'win-x64') { '.exe' } else { '' }
    Copy-Item (Join-Path $stage "Xas.Cli\xas$exeExt") $package
    Copy-Item (Join-Path $stage "Xas.Daemon\Xas.Daemon$exeExt") $package
    $readme = Join-Path $package 'INSTALL.txt'
    if ($rid -eq 'win-x64') {
        Copy-Item (Join-Path $stage 'Xas.PrivilegedService\Xas.PrivilegedService.exe') $package
        Copy-Item (Join-Path $PSScriptRoot 'install-windows.ps1') $package
        @('xas Windows x64 package', '', 'Run install-windows.ps1 from an elevated PowerShell window. It installs the client and background daemon under %ProgramFiles%\xas, registers the daemon to start silently at user logon, and installs the automatic XasAdminBroker Windows service.', 'Open a new terminal after installation if PATH was changed.', '', 'Only the installed Xas.Daemon process may connect to the privileged broker. Remote administrator execution still requires the peer PrivilegedShell grant.') | Set-Content -LiteralPath $readme
    } else {
        Copy-Item (Join-Path $PSScriptRoot 'install-linux.sh') $package
        foreach ($helper in @('xas-linux-pty', 'xas-wayland-eis', 'xas-uinput')) {
            $candidate = Join-Path $root "artifacts\native-linux\$helper"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { Copy-Item -LiteralPath $candidate -Destination $package }
        }
        @('xas Linux x64 package', '', 'Run: chmod +x install-linux.sh && ./install-linux.sh', 'Installs both programs to ~/.local/bin, registers/enables the per-user xas daemon with systemd when available, and adds the folder to your shell PATH if needed.', 'Linux native helpers are included only if separately built and placed in artifacts/native-linux before packaging.') | Set-Content -LiteralPath $readme
    }
    $archive = Join-Path $releaseRoot "xas-$rid.zip"
    if (Test-Path $archive) { Remove-Item -LiteralPath $archive -Force }
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive
}
Write-Host "Release packages created under $releaseRoot"
