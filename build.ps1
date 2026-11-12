# Builds everything: tests, self-contained single-file exe, portable zip and per-user installer.
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 1.0.0] [-SkipTests] [-SkipInstaller]
param(
    [string]$Version = '1.0.0',
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# .NET SDK: PATH first, then the per-user install location used by dotnet-install.ps1.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw '.NET 8 SDK not found. Install it from https://dot.net or with dotnet-install.ps1.' }

if (-not $SkipTests) {
    & $dotnet test (Join-Path $root 'tests\PeriBattery.Tests') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

$publish = Join-Path $root 'publish\win-x64'
$dist = Join-Path $root 'dist'
Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

& $dotnet publish (Join-Path $root 'src\PeriBattery') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none -p:DebugSymbols=false -p:Version=$Version -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# Diagnostic tool (framework-independent too, so it runs on any PC).
$diag = Join-Path $root 'publish\hiddiag'
Remove-Item -Recurse -Force $diag -ErrorAction SilentlyContinue
& $dotnet publish (Join-Path $root 'src\PeriBattery.HidDiag') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false `
    -p:Version=$Version -o $diag --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish of HidDiag failed.' }

# Portable zip.
$zip = Join-Path $dist "PeriBattery-$Version-portable-win-x64.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $publish 'PeriBattery.exe'), (Join-Path $diag 'PeriBattery.HidDiag.exe'), (Join-Path $root 'README.md') -DestinationPath $zip
Write-Host "Portable: $zip"

# Installer (Inno Setup 6, per-user or machine-wide install).
if (-not $SkipInstaller) {
    $iscc = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        Write-Warning 'Inno Setup 6 not found: installer skipped. Get it from https://jrsoftware.org/isdl.php'
    } else {
        & $iscc "/DAppVersion=$Version" /Q (Join-Path $root 'installer\PeriBattery.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
        Write-Host "Installer: $(Join-Path $dist "PeriBattery-Setup-$Version.exe")"
    }
}
