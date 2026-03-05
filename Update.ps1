#Requires -Version 5.1
<#
.SYNOPSIS
    Downloads and installs the latest ImagingTool release from GitHub.
.DESCRIPTION
    Queries the GitHub releases API, compares against the currently installed
    version, and replaces all files if a newer release is available.
.EXAMPLE
    .\Update.ps1
    .\Update.ps1 -Force   # Skip version check and always update
#>

param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$repo      = "Jessomadic/ImagingTool"
$assetName = "ImagingTool-Debug.zip"
$scriptDir = $PSScriptRoot
$versionFile = Join-Path $scriptDir "version.txt"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    WARNING: $msg" -ForegroundColor Yellow }

# --- Check current version ---
$currentTag = $null
if (Test-Path $versionFile) {
    $currentTag = (Get-Content $versionFile -Raw).Trim()
}

Write-Step "Checking latest release..."
try {
    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$repo/releases/latest" `
        -Headers @{ "User-Agent" = "ImagingTool-Updater/1.0" }
} catch {
    Write-Error "Failed to reach GitHub API: $_"
    exit 1
}

$latestTag = $release.tag_name
$asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1

if (-not $asset) {
    Write-Error "Release '$latestTag' has no asset named '$assetName'. The release may still be processing — try again in a minute."
    exit 1
}

Write-Ok "Latest : $latestTag"
Write-Ok "Current: $(if ($currentTag) { $currentTag } else { '(unknown)' })"

if (-not $Force -and $currentTag -eq $latestTag) {
    Write-Host "`nAlready up to date." -ForegroundColor Green
    exit 0
}

# --- Check ImagingTool is not running ---
$running = Get-Process -Name "ImagingTool" -ErrorAction SilentlyContinue
if ($running) {
    Write-Error "ImagingTool.exe is currently running. Close it and try again."
    exit 1
}

# --- Download ---
$tempDir  = Join-Path $env:TEMP "ImagingTool-update-$(New-Guid)"
$zipPath  = Join-Path $tempDir "update.zip"
$extractDir = Join-Path $tempDir "extracted"

try {
    New-Item -ItemType Directory -Path $tempDir | Out-Null

    Write-Step "Downloading $latestTag..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -UseBasicParsing
    Write-Ok "Downloaded $('{0:N1} MB' -f ((Get-Item $zipPath).Length / 1MB))"

    # --- Extract ---
    Write-Step "Extracting..."
    Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force
    Write-Ok "Extracted to $extractDir"

    # --- Install ---
    Write-Step "Installing to $scriptDir..."
    Get-ChildItem -Path $extractDir | ForEach-Object {
        $dest = Join-Path $scriptDir $_.Name
        Copy-Item -Path $_.FullName -Destination $dest -Recurse -Force
        Write-Ok "Updated: $($_.Name)"
    }

    # --- Record version ---
    Set-Content -Path $versionFile -Value $latestTag -Encoding UTF8
    Write-Ok "Version saved: $latestTag"

    Write-Host "`nUpdate complete! ImagingTool is now at $latestTag." -ForegroundColor Green

} catch {
    Write-Error "Update failed: $_"
    exit 1
} finally {
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
