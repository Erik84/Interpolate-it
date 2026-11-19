# Builds installer\output\Interpolate-it-Setup.exe:
#   1. publishes the application self-contained (the .NET runtime goes with it: nothing to install first),
#   2. puts the licence texts of everything that is shipped next to it,
#   3. compiles installer\Interpolate-it.iss with Inno Setup 6 (winget install JRSoftware.InnoSetup).
# The models (models\*.onnx, see README: "Build from source") must have been exported: they are part of the installer.
#   tools\build-installer.ps1 [-SkipPublish] [-Test]
# -Test builds installer\output\Interpolate-it-Setup-test.exe: the same content under another identity. Use that
# one to try installing and uninstalling on a PC where the application itself is installed; the real installer
# in a second folder would take away the installed application's registration and shortcuts.
param(
    [switch]$SkipPublish,
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish = Join-Path $root 'artifacts\publish\Interpolate-it'
$output = Join-Path $root 'installer\output'

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = "$($props.Project.PropertyGroup.Version)".Trim()
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

if (-not (Get-ChildItem (Join-Path $root 'models') -Filter '*.onnx' -ErrorAction SilentlyContinue)) {
    throw 'models\*.onnx are missing: export the models first (README, "Build from source").'
}

if (-not $SkipPublish) {
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    dotnet publish (Join-Path $root 'src\InterpolatIt.App') -c Release -r win-x64 --self-contained true -p:SelfContained=true `
        -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=en -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}
if (-not (Test-Path (Join-Path $publish 'InterpolatIt.exe'))) { throw "Nothing published in $publish." }

# Licence texts. The application itself is MIT; what comes with it keeps its own terms.
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $publish 'LICENSE.txt') -Force
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $publish 'THIRD-PARTY-NOTICES.txt') -Force
$licences = Join-Path $publish 'licenses'
New-Item -ItemType Directory -Force $licences | Out-Null
$packages = $(if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' })
[xml]$central = Get-Content (Join-Path $root 'Directory.Packages.props')
$winMl = ($central.Project.ItemGroup.PackageVersion | Where-Object Include -eq 'Microsoft.Windows.AI.MachineLearning').Version
$winMlPackage = Join-Path $packages "microsoft.windows.ai.machinelearning\$winMl"
Copy-Item (Join-Path $winMlPackage 'license.txt') (Join-Path $licences 'Windows-ML-Runtime-license.txt') -Force
Copy-Item (Join-Path $winMlPackage 'ThirdPartyNotices.txt') (Join-Path $licences 'Windows-ML-Runtime-ThirdPartyNotices.txt') -Force
$dotnetRoot = Split-Path (Get-Command dotnet).Source
foreach ($name in 'LICENSE.txt', 'ThirdPartyNotices.txt') {
    $file = Join-Path $dotnetRoot $name
    if (Test-Path $file) { Copy-Item $file (Join-Path $licences "dotnet-$name") -Force }
}

# What the wizard shows: the application's own licence, and where the others are.
New-Item -ItemType Directory -Force $output | Out-Null
$page = (Get-Content (Join-Path $root 'LICENSE') -Raw).TrimEnd() + @"


----------------------------------------------------------------------

The licence above is that of Interpolate-it itself. This installer also
contains work of others, which stays under its own terms: the .NET runtime
(MIT), the Microsoft Windows ML runtime with ONNX Runtime and DirectML
(Microsoft Software License Terms), Vortice.Windows (MIT) and the RIFE frame
interpolation models (MIT). After installation their notices and licence
texts are in THIRD-PARTY-NOTICES.txt and in the "licenses" folder next to
the application.
"@
Set-Content -Path (Join-Path $output 'license-page.txt') -Value $page -Encoding ASCII

$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue | ForEach-Object Source),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found: winget install JRSoftware.InnoSetup' }

$defines = @("/DAppVersion=$version", "/DPublishDir=$publish")
if ($Test) { $defines += '/DTestBuild' }
& $iscc @defines /Qp (Join-Path $root 'installer\Interpolate-it.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$setup = Get-Item (Join-Path $output $(if ($Test) { 'Interpolate-it-Setup-test.exe' } else { 'Interpolate-it-Setup.exe' }))
$files = Get-ChildItem $publish -Recurse -File | Where-Object Extension -ne '.pdb'
'{0}: version {1}, {2:N0} MB ({3} files, {4:N0} MB installed)' -f $setup.FullName, $version, ($setup.Length / 1MB), $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB)
