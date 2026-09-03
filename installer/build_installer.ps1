# AMCCA Engineering V3.1 — Installer Build Pipeline (SPEC/76, AUDIT-009)
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "dist/installer"
)

$ErrorActionPreference = "Stop"
Write-Host "=== AMCCA Installer Pipeline (WiX Toolset) ===" -ForegroundColor Cyan

$root = Resolve-Path "."
$publishDir = Join-Path $root "artifacts/publish/win-x64"
$outFullPath = Join-Path $root $OutputDir

New-Item -ItemType Directory -Force -Path $outFullPath | Out-Null

# 1. Publish Self-Contained Desktop Application
Write-Host "[1/4] Publishing AMCCA.App ($Configuration, $Runtime)..."
$dotnet = if (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") { "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" } else { "dotnet" }
& $dotnet publish src/AMCCA.App/AMCCA.App.csproj -c $Configuration -r $Runtime --self-contained true -o $publishDir

# 2. Generate WiX File Harvesting Components
Write-Host "[2/4] Harvesting published files into WiX components..."
$python = if (Test-Path ".\.venv\Scripts\python.exe") { ".\.venv\Scripts\python.exe" } else { "python" }
& $python installer/generate_components.py $publishDir installer/Components.wxs

# 3. Build WiX MSI Package
Write-Host "[3/4] Compiling WiX MSI installer..."
if (-not $env:DOTNET_ROOT -and (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet")) {
    $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
}
$wix = if (Test-Path "$env:USERPROFILE\.dotnet\tools\wix.exe") { "$env:USERPROFILE\.dotnet\tools\wix.exe" } else { "wix" }

$msiOut = Join-Path $outFullPath "AMCCA-Setup.msi"
$exeOut = Join-Path $outFullPath "AMCCA-Setup.exe"

& $wix build installer/Package.wxs installer/Components.wxs -arch x64 -o $msiOut
Copy-Item -Path $msiOut -Destination $exeOut -Force

# 4. Generate SHA256 Checksums
Write-Host "[4/4] Generating installer checksums..."
$checksums = @()
foreach ($item in (Get-ChildItem -Path $outFullPath -File -Filter "AMCCA-Setup.*")) {
    $hash = (Get-FileHash -Path $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums += "$hash  $($item.Name)"
    Write-Host "  $($item.Name): $hash"
}
Set-Content -Path (Join-Path $outFullPath "SHA256SUMS") -Value $checksums -Encoding UTF8

Write-Host "=== AMCCA Installer Pipeline Complete ===" -ForegroundColor Green
