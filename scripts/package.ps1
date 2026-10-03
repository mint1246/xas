param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishRoot = Join-Path $root 'artifacts\publish'
$releaseRoot = Join-Path $root 'artifacts\release'
$nuget = 'https://api.nuget.org/v3/index.json'
$winFspVersion = '2.1.25156'
$winFspFile = "winfsp-$winFspVersion.msi"
$winFspUrl = "https://github.com/winfsp/winfsp/releases/download/v2.1/$winFspFile"
$winFspSha256 = '073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A'
$dependencyRoot = Join-Path $root 'artifacts\dependencies'

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

function Get-VerifiedDependency([string]$name, [string]$url, [string]$sha256) {
    New-Item -ItemType Directory -Force -Path $dependencyRoot | Out-Null
    $path = Join-Path $dependencyRoot $name
    $valid = $false
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $valid = [string]::Equals($actual, $sha256, [StringComparison]::OrdinalIgnoreCase)
        if (-not $valid) { Remove-Item -LiteralPath $path -Force }
    }
    if (-not $valid) {
        Write-Host "Downloading pinned dependency $name"
        Invoke-WebRequest -Uri $url -OutFile $path
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if (-not [string]::Equals($actual, $sha256, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            throw "SHA-256 verification failed for $name. Expected $sha256, got $actual."
        }
    }
    return $path
}

foreach ($rid in @('win-x64', 'linux-x64')) {
    $stage = Join-Path $publishRoot $rid
    Remove-WorkspaceTree $stage
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $projects = if ($rid -eq 'win-x64') { @('Xas.Cli', 'Xas.Daemon', 'Xas.PrivilegedService') } else { @('Xas.Cli', 'Xas.Daemon') }
    foreach ($project in $projects) {
        $out = Join-Path $stage $project
        # WinFsp's managed API inspects its own Assembly.Location during initialization.
        # Bundled assemblies report an empty Location, so keep Windows daemon dependencies loose.
        $singleFile = -not ($rid -eq 'win-x64' -and $project -eq 'Xas.Daemon')
        $publishArgs = @('publish', (Join-Path $root "src\$project\$project.csproj"), '-c', $Configuration,
            '-r', $rid, '--self-contained', 'true', "-p:PublishSingleFile=$($singleFile.ToString().ToLowerInvariant())",
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
        $winFspMsi = Get-VerifiedDependency $winFspFile $winFspUrl $winFspSha256
        Copy-Item (Join-Path $stage 'Xas.PrivilegedService\Xas.PrivilegedService.exe') $package
        $daemonRuntime = Join-Path $package 'daemon-runtime'
        New-Item -ItemType Directory -Force -Path $daemonRuntime | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $stage 'Xas.Daemon') -File |
            Where-Object { $_.Name -ne 'Xas.Daemon.exe' } |
            Copy-Item -Destination $daemonRuntime
        Copy-Item (Join-Path $PSScriptRoot 'install-windows.ps1') $package
        Copy-Item -LiteralPath $winFspMsi -Destination $package
        @('xas Windows x64 package', '', 'Run install-windows.ps1 from an elevated PowerShell window. It installs the client under %ProgramFiles%\xas, installs the pinned WinFsp runtime when needed, and installs the automatic XAS Background Service. The service keeps Xas.Daemon running inside the active user desktop session; no Scheduled Task or manual daemon start is required.', 'Open a new terminal after installation if PATH was changed.', '', 'Only the installed Xas.Daemon process may connect to the privileged broker. Remote administrator execution still requires the peer PrivilegedShell grant.') | Set-Content -LiteralPath $readme
    } else {
        Copy-Item (Join-Path $PSScriptRoot 'install-linux.sh') $package
        foreach ($helper in @('xas-linux-pty', 'xas-wayland-eis', 'xas-uinput')) {
            $candidate = Join-Path $root "artifacts\native-linux\$helper"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { Copy-Item -LiteralPath $candidate -Destination $package }
        }
        @('xas Linux x64 package', '', 'Run: chmod +x install-linux.sh && ./install-linux.sh', 'Installs both programs to ~/.local/bin, registers/enables the per-user xas daemon with systemd when available, and adds the folder to your shell PATH if needed.', 'Native remote filesystem mounts require FUSE3: fusermount3, libfuse3, and access to /dev/fuse. The installer checks these and prints a distro-specific package hint without requiring root itself.', 'Automatic Linux remote mounts are created under ~/xas.', 'Linux native helpers are included only if separately built and placed in artifacts/native-linux before packaging.') | Set-Content -LiteralPath $readme
    }
    $archive = Join-Path $releaseRoot "xas-$rid.zip"
    if (Test-Path $archive) { Remove-Item -LiteralPath $archive -Force }
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive
}
Write-Host "Release packages created under $releaseRoot"
