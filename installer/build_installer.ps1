# AMCCA Engineering V3.1 — Installer Build Pipeline (SPEC/76, DEF-CERT-001)
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "dist/installer"
)

$ErrorActionPreference = "Stop"
Write-Host "=== AMCCA Installer Pipeline (WiX Toolset Bootstrapper) ===" -ForegroundColor Cyan

$root = Resolve-Path "."
$publishDir = Join-Path $root "artifacts/publish/win-x64"
$outFullPath = Join-Path $root $OutputDir

New-Item -ItemType Directory -Force -Path $outFullPath | Out-Null

# 1. Publish Self-Contained Desktop Application
Write-Host "[1/5] Publishing AMCCA.App ($Configuration, $Runtime)..."
$dotnet = if (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") { "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" } else { "dotnet" }
& $dotnet publish src/AMCCA.App/AMCCA.App.csproj -c $Configuration -r $Runtime --self-contained true -o $publishDir

# 2. Generate WiX File Harvesting Components
Write-Host "[2/5] Harvesting published files into WiX components..."
$python = if (Test-Path ".\.venv\Scripts\python.exe") { ".\.venv\Scripts\python.exe" } else { "python" }
& $python installer/generate_components.py $publishDir installer/Components.wxs

# 3. Build WiX MSI Package
Write-Host "[3/5] Compiling WiX MSI installer package..."
if (-not $env:DOTNET_ROOT -and (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet")) {
    $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
}
$wix = if (Test-Path "$env:USERPROFILE\.dotnet\tools\wix.exe") { "$env:USERPROFILE\.dotnet\tools\wix.exe" } else { "wix" }

$msiOut = Join-Path $outFullPath "AMCCA-Setup.msi"
$exeOut = Join-Path $outFullPath "AMCCA-Setup.exe"

& $wix build installer/Package.wxs installer/Components.wxs -arch x64 -o $msiOut
if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed with exit code $LASTEXITCODE" }

# 4. Build WiX Bootstrapper Bundle EXE (DEF-CERT-001: True PE Bootstrapper)
Write-Host "[4/5] Compiling WiX Burn Bootstrapper Bundle EXE..."
& $wix build installer/Bundle.wxs -ext WixToolset.BootstrapperApplications.wixext/5.0.0 -arch x64 -d MsiPath="$msiOut" -o $exeOut
if ($LASTEXITCODE -ne 0) { throw "WiX Bootstrapper build failed with exit code $LASTEXITCODE" }

# 5. Verify Artifact Identity & Generate SHA256 Checksums
Write-Host "[5/5] Verifying installer PE integrity & generating checksums..."
$msiBytes = [System.IO.File]::ReadAllBytes($msiOut)
$exeBytes = [System.IO.File]::ReadAllBytes($exeOut)

# Verify MZ header for EXE
if ($exeBytes.Length -lt 2 -or $exeBytes[0] -ne 0x4D -or $exeBytes[1] -ne 0x5A) {
    throw "DEF-CERT-001 VIOLATION: AMCCA-Setup.exe is not a valid Windows PE binary (missing MZ signature)."
}

$msiHash = (Get-FileHash -Path $msiOut -Algorithm SHA256).Hash.ToLowerInvariant()
$exeHash = (Get-FileHash -Path $exeOut -Algorithm SHA256).Hash.ToLowerInvariant()

if ($msiHash -eq $exeHash) {
    throw "DEF-CERT-001 VIOLATION: AMCCA-Setup.exe has identical SHA256 to AMCCA-Setup.msi. Renaming/copying MSI is strictly forbidden."
}

$checksums = @(
    "$exeHash  AMCCA-Setup.exe",
    "$msiHash  AMCCA-Setup.msi"
)
Set-Content -Path (Join-Path $outFullPath "SHA256SUMS") -Value $checksums -Encoding UTF8
Set-Content -Path (Join-Path $outFullPath "SHA256SUMS.txt") -Value $checksums -Encoding UTF8

Write-Host "  AMCCA-Setup.exe: $exeHash (PE Executable verified)" -ForegroundColor Green
Write-Host "  AMCCA-Setup.msi: $msiHash (Windows Installer verified)" -ForegroundColor Green
Write-Host "=== AMCCA Installer Pipeline Complete ===" -ForegroundColor Green
