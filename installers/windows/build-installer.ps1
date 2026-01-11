# Build Windows installer using Inno Setup
# Usage: .\build-installer.ps1 -SourceDir .\publish -OutputDir .\output -Version 0.1.0
#
# Prerequisites:
#   - Inno Setup 6.x installed (https://jrsoftware.org/isdl.php)
#   - ISCC.exe in PATH or set $env:ISCC_PATH

param(
    [Parameter(Mandatory=$true)]
    [string]$SourceDir,

    [Parameter(Mandatory=$true)]
    [string]$OutputDir,

    [Parameter(Mandatory=$true)]
    [string]$Version,

    [string]$SignCert = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "Building Snacka Windows Installer" -ForegroundColor Cyan
Write-Host "  Source: $SourceDir"
Write-Host "  Output: $OutputDir"
Write-Host "  Version: $Version"

# Find Inno Setup compiler
$IsccPaths = @(
    $env:ISCC_PATH,
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    (Get-Command iscc -ErrorAction SilentlyContinue).Source
) | Where-Object { $_ -and (Test-Path $_) }

if (-not $IsccPaths) {
    Write-Error "Inno Setup compiler (ISCC.exe) not found. Please install Inno Setup from https://jrsoftware.org/isdl.php"
    exit 1
}

$Iscc = $IsccPaths[0]
Write-Host "  Using ISCC: $Iscc"

# Ensure output directory exists
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# Resolve paths
$SourceDir = Resolve-Path $SourceDir
$OutputDir = Resolve-Path $OutputDir
$IssFile = Join-Path $ScriptDir "Snacka.iss"

# Check for icon file, use a placeholder if not present
$IconFile = Join-Path $ScriptDir "snacka.ico"
if (-not (Test-Path $IconFile)) {
    Write-Host "  Warning: snacka.ico not found, installer will use default icon" -ForegroundColor Yellow
    # Remove the icon line from the iss file for this build
    $IssContent = Get-Content $IssFile -Raw
    $IssContent = $IssContent -replace "SetupIconFile=snacka.ico", "; SetupIconFile=snacka.ico (not found)"
    $TempIss = Join-Path $env:TEMP "Snacka-temp.iss"
    Set-Content -Path $TempIss -Value $IssContent
    $IssFile = $TempIss
}

# Build the installer
Write-Host ""
Write-Host "Running Inno Setup compiler..." -ForegroundColor Cyan

$IsccArgs = @(
    $IssFile,
    "/DVersion=$Version",
    "/DSourceDir=$SourceDir",
    "/DOutputDir=$OutputDir"
)

# Add signing if certificate is provided
if ($SignCert) {
    Write-Host "  Code signing enabled"
    $IsccArgs += "/DSignTool=signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /f `"$SignCert`" `$f"
}

& $Iscc $IsccArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Installer created successfully!" -ForegroundColor Green
Get-ChildItem $OutputDir -Filter "*.exe" | ForEach-Object {
    Write-Host "  $($_.Name) ($([math]::Round($_.Length / 1MB, 2)) MB)"
}
