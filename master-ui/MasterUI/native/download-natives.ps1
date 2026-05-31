# download-natives.ps1
# Downloads tun2socks.exe and wintun.dll required for the Layer 3 VPN tunnel.
# Run this script once after cloning the repo.

$ErrorActionPreference = "Stop"
$nativeDir = $PSScriptRoot

Write-Host "=== Downloading native binaries to $nativeDir ===" -ForegroundColor Cyan

# --- tun2socks.exe ---
$tun2socksUrl = "https://github.com/xjasonlyu/tun2socks/releases/download/v2.5.2/tun2socks-windows-amd64.zip"
$tun2socksZip = Join-Path $nativeDir "tun2socks.zip"
$tun2socksExe = Join-Path $nativeDir "tun2socks.exe"

if (-not (Test-Path $tun2socksExe)) {
    Write-Host "[1/2] Downloading tun2socks v2.5.2..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri $tun2socksUrl -OutFile $tun2socksZip -UseBasicParsing
    Expand-Archive -Path $tun2socksZip -DestinationPath $nativeDir -Force
    # The zip contains tun2socks-windows-amd64.exe, rename it
    $extracted = Join-Path $nativeDir "tun2socks-windows-amd64.exe"
    if (Test-Path $extracted) {
        Move-Item $extracted $tun2socksExe -Force
    }
    Remove-Item $tun2socksZip -Force -ErrorAction SilentlyContinue
    Write-Host "[1/2] tun2socks.exe OK" -ForegroundColor Green
} else {
    Write-Host "[1/2] tun2socks.exe already exists, skipping." -ForegroundColor Gray
}

# --- wintun.dll ---
$wintunUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip"
$wintunZip = Join-Path $nativeDir "wintun.zip"
$wintunDll = Join-Path $nativeDir "wintun.dll"

if (-not (Test-Path $wintunDll)) {
    Write-Host "[2/2] Downloading wintun v0.14.1..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri $wintunUrl -OutFile $wintunZip -UseBasicParsing
    $wintunExtract = Join-Path $nativeDir "wintun-temp"
    Expand-Archive -Path $wintunZip -DestinationPath $wintunExtract -Force
    # wintun zip structure: wintun/bin/amd64/wintun.dll
    $srcDll = Join-Path $wintunExtract "wintun\bin\amd64\wintun.dll"
    if (Test-Path $srcDll) {
        Copy-Item $srcDll $wintunDll -Force
    }
    Remove-Item $wintunZip -Force -ErrorAction SilentlyContinue
    Remove-Item $wintunExtract -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[2/2] wintun.dll OK" -ForegroundColor Green
} else {
    Write-Host "[2/2] wintun.dll already exists, skipping." -ForegroundColor Gray
}

Write-Host ""
Write-Host "=== All native binaries ready! ===" -ForegroundColor Cyan
Write-Host "Now rebuild the project and tun2socks.exe + wintun.dll will be auto-copied to the output folder."
