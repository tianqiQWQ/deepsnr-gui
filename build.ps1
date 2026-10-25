# Build DeepSNR_GUI.exe with the C# compiler that ships with Windows.
# Requires Windows 10/11 and .NET Framework 4.8 (included by default).
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$src = Join-Path $here 'src'
$dist = Join-Path $here 'dist'
$cs = Join-Path $src 'DeepSNR_GUI.cs'
$icon = Join-Path $src 'deepsnr_gui.ico'
$manifest = Join-Path $src 'app.manifest'

foreach ($f in @($cs, $icon, $manifest)) {
    if (-not (Test-Path $f)) { throw "Missing source file: $f" }
}

$cscCandidates = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    $csc = Get-ChildItem 'C:\Windows\Microsoft.NET' -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $csc) {
    throw 'Could not find csc.exe. This project requires the .NET Framework 4.x on Windows (it is included with Windows 10/11).'
}

$fwDir = Split-Path -Parent $csc
$wpfDir = Join-Path $fwDir 'WPF'
$refs = @(
    (Join-Path $wpfDir 'PresentationFramework.dll'),
    (Join-Path $wpfDir 'PresentationCore.dll'),
    (Join-Path $wpfDir 'WindowsBase.dll')
)

$sysXaml = Get-ChildItem 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml' -Recurse -Filter 'System.Xaml.dll' -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $sysXaml) { throw 'Could not find System.Xaml.dll (part of .NET Framework 4.x).' }
$refs += $sysXaml

foreach ($r in @('System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Drawing.dll')) {
    $refs += $r
}

foreach ($r in $refs) {
    # Bare framework assembly names (System.dll, ...) are resolved by csc itself.
    if ($r -match '[\\/]' -and -not (Test-Path $r)) { throw "Missing assembly reference: $r" }
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$outExe = Join-Path $dist 'DeepSNR_GUI.exe'

$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
    "/out:$outExe",
    "/win32icon:$icon",
    "/win32manifest:$manifest"
)
foreach ($r in $refs) { $cscArgs += "/r:$r" }
$cscArgs += $cs

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc.exe failed with exit code $LASTEXITCODE" }

Write-Host "Built $outExe"
