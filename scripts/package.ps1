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
    foreach ($project in @('Xas.Cli', 'Xas.Daemon')) {
        $out = Join-Path $stage $project
        dotnet publish (Join-Path $root "src\$project\$project.csproj") -c $Configuration -r $rid --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:RestoreSources=$nuget -o $out
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
        Copy-Item (Join-Path $PSScriptRoot 'install-windows.ps1') $package
        @('xas Windows x64 package', '', 'Run install-windows.ps1 from PowerShell to install both programs under %LOCALAPPDATA%\Programs\xas and add that folder to your user PATH.', 'Open a new terminal after installation.') | Set-Content -LiteralPath $readme
    } else {
        Copy-Item (Join-Path $PSScriptRoot 'install-linux.sh') $package
        foreach ($helper in @('xas-linux-pty', 'xas-wayland-eis', 'xas-uinput')) {
            $candidate = Join-Path $root "artifacts\native-linux\$helper"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { Copy-Item -LiteralPath $candidate -Destination $package }
        }
        @('xas Linux x64 package', '', 'Run: chmod +x install-linux.sh && ./install-linux.sh', 'Installs both programs to ~/.local/bin and adds it to ~/.profile if missing from PATH.', 'Linux native helpers are included only if separately built and placed in artifacts/native-linux before packaging.') | Set-Content -LiteralPath $readme
    }
    $archive = Join-Path $releaseRoot "xas-$rid.zip"
    if (Test-Path $archive) { Remove-Item -LiteralPath $archive -Force }
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive
}
Write-Host "Release packages created under $releaseRoot"
