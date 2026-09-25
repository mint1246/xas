param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$WdkPackageRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $WdkPackageRoot) { $WdkPackageRoot = Join-Path $repoRoot '.tools\wdk' }
$wdk = Join-Path (Resolve-Path $WdkPackageRoot).Path 'c'
$kitVersion = '10.0.28000.0'
$sdk = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10"
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Visual Studio vswhere.exe was not found.' }
$vs = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
if (-not $vs) { throw 'Visual Studio with MSVC x64 tools was not found.' }
$tools = Get-ChildItem (Join-Path $vs 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (-not $tools) { throw 'MSVC tools were not found.' }
$cl = Join-Path $tools.FullName 'bin\Hostx64\x64\cl.exe'
$link = Join-Path $tools.FullName 'bin\Hostx64\x64\link.exe'
$infverif = Join-Path $wdk "tools\$kitVersion\x64\infverif.exe"
$driverSource = Join-Path $PSScriptRoot 'XasVirtualDisplay.cpp'
$driverInf = Join-Path $PSScriptRoot 'XasVirtualDisplay.inf'
$out = Join-Path $repoRoot "artifacts\windows-virtual-display\x64\$Configuration"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$obj = Join-Path $out 'XasVirtualDisplay.obj'
$dll = Join-Path $out 'XasVirtualDisplay.dll'
$pdb = Join-Path $out 'XasVirtualDisplay.pdb'

$includes = @(
    "/I$(Join-Path $tools.FullName 'include')",
    "/I$(Join-Path $sdk "Include\$kitVersion\um")",
    "/I$(Join-Path $sdk "Include\$kitVersion\shared")",
    "/I$(Join-Path $sdk "Include\$kitVersion\ucrt")",
    "/I$(Join-Path $sdk "Include\$kitVersion\winrt")",
    "/I$(Join-Path $wdk 'Include\wdf\umdf\2.25')",
    "/I$(Join-Path $wdk "Include\$kitVersion\um\iddcx\1.4")"
)
$compile = @('/nologo', '/std:c++17', '/EHsc', '/W3', '/Zi', "/Fd$(Join-Path $out 'XasVirtualDisplayCompiler.pdb')", '/DUMDF_DRIVER', '/D_WIN64',
    '/DIDDCX_VERSION_MAJOR=1', '/DIDDCX_VERSION_MINOR=4', '/DIDDCX_MINIMUM_VERSION_REQUIRED=4',
    "/Fo$obj", '/c', $driverSource) + $includes
if ($Configuration -eq 'Release') { $compile += '/O2' }
& $cl @compile
if ($LASTEXITCODE -ne 0) { throw "Driver compilation failed ($LASTEXITCODE)." }

$libPaths = @(
    "/LIBPATH:$(Join-Path $tools.FullName 'lib\x64')",
    "/LIBPATH:$(Join-Path $sdk "Lib\$kitVersion\um\x64")",
    "/LIBPATH:$(Join-Path $sdk "Lib\$kitVersion\ucrt\x64")",
    "/LIBPATH:$(Join-Path $wdk 'Lib\wdf\umdf\x64\2.25')",
    "/LIBPATH:$(Join-Path $wdk "Lib\$kitVersion\um\x64\iddcx\1.4")"
)
& $link /NOLOGO /DLL /MACHINE:X64 "/OUT:$dll" "/PDB:$pdb" /DEBUG $obj @libPaths `
    WdfDriverStubUm.lib iddcxstub.lib OneCoreUAP.lib D3D11.lib DXGI.lib ntdll.lib
if ($LASTEXITCODE -ne 0) { throw "Driver linking failed ($LASTEXITCODE)." }
& $infverif /w $driverInf
if ($LASTEXITCODE -ne 0) { throw 'Windows Driver INF verification failed.' }
& $infverif /u $driverInf
if ($LASTEXITCODE -ne 0) { throw 'Universal INF verification failed.' }
& $infverif /h $driverInf
if ($LASTEXITCODE -ne 0) { throw 'WHQL-rule INF verification failed.' }
Copy-Item $driverInf (Join-Path $out 'XasVirtualDisplay.inf')
Write-Host "Built $dll and verified the INF."
