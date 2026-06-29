# KingshotAuto Installer Build Script (NSIS)
# Builds a Windows installer from the output produced by Build-Release.ps1.
# Requires NSIS (https://nsis.sourceforge.io/). This step is optional for
# normal development - it is only needed to produce a distributable installer.
param(
    [string]$Version = "1.0.0",
    [string]$InstallerType = "NSIS"
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "KingshotAuto Installer Builder" -ForegroundColor Cyan
Write-Host "Version: $Version" -ForegroundColor Cyan
Write-Host "Installer Type: $InstallerType" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

if ($InstallerType -ne "NSIS") {
    Write-Host "Only the NSIS installer is supported. Use -InstallerType NSIS." -ForegroundColor Red
    exit 1
}

# Check if build exists
$buildDir = "dist\KingshotAuto-v$Version-win-x64"
if (-not (Test-Path $buildDir)) {
    Write-Host "Build directory not found: $buildDir" -ForegroundColor Red
    Write-Host "Please run Build-Release.ps1 first" -ForegroundColor Yellow
    exit 1
}

# Create installer output directory
$installerDir = "dist\Installers"
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null
New-Item -ItemType Directory -Path "Installer" -Force | Out-Null

Write-Host "Building NSIS installer..." -ForegroundColor Yellow

# Locate the NSIS compiler
$nsisPath = (Get-Command "makensis" -ErrorAction SilentlyContinue).Source
if (-not $nsisPath) {
    $possiblePaths = @(
        "${env:ProgramFiles}\NSIS\makensis.exe",
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe"
    )
    foreach ($path in $possiblePaths) {
        if (Test-Path $path) { $nsisPath = $path; break }
    }
    if (-not $nsisPath) {
        Write-Host "NSIS not found. Install it from https://nsis.sourceforge.io/" -ForegroundColor Red
        exit 1
    }
}

# Create the license file shown by the installer (MIT)
$licenseContent = @"
MIT License

Copyright (c) 2025 KingshotAuto

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is furnished
to do so, subject to the conditions in the LICENSE file in the repository.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED. See the LICENSE file for the full text.
"@
$licensePath = "Installer\License.txt"
Set-Content -Path $licensePath -Value $licenseContent -Encoding UTF8

# Build a temporary NSIS script with absolute paths and the version baked in
$absoluteBuildDir = (Resolve-Path $buildDir).Path
$absoluteInstallerDir = (Resolve-Path $installerDir).Path
$absoluteLicensePath = (Resolve-Path $licensePath).Path
$absoluteToolsDir = (Resolve-Path "tools").Path

$tempNsisScript = "Installer\KingshotAuto-Modern-Temp.nsi"
$nsisScript = Get-Content "Installer\KingshotAuto-Modern.nsi" -Raw
$nsisScript = $nsisScript.Replace("dist\KingshotAuto-v`${VERSION}-win-x64\", "$absoluteBuildDir\")
$nsisScript = $nsisScript.Replace("dist\Installers\", "$absoluteInstallerDir\")
$nsisScript = $nsisScript.Replace("Installer\License.txt", "$absoluteLicensePath")
$nsisScript = $nsisScript.Replace("tools\", "$absoluteToolsDir\")
$nsisScript = $nsisScript.Replace("`${VERSION_PLACEHOLDER}", "$Version")
Set-Content -Path $tempNsisScript -Value $nsisScript -Encoding UTF8

# Verify the main executable exists in the build
$testExePath = "$buildDir\KingshotAuto.exe"
if (-not (Test-Path $testExePath)) {
    Write-Host "ERROR: Main executable not found at: $testExePath" -ForegroundColor Red
    Remove-Item $tempNsisScript -ErrorAction SilentlyContinue
    exit 1
}

Write-Host "Running NSIS compiler..." -ForegroundColor Gray
& $nsisPath $tempNsisScript
$nsisExit = $LASTEXITCODE

Remove-Item $tempNsisScript -ErrorAction SilentlyContinue

if ($nsisExit -ne 0) {
    Write-Host "Error: NSIS installer build failed" -ForegroundColor Red
    exit 1
}

$outputPath = "$installerDir\KingshotAuto-Setup-v$Version.exe"
Write-Host "`n=========================================" -ForegroundColor Green
Write-Host "Installer Build Complete!" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Green
Write-Host "Installer created at: $outputPath" -ForegroundColor Cyan
Write-Host "Attach the .exe to a GitHub Release to distribute it." -ForegroundColor Gray
