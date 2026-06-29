# KingshotAuto Build Script
#
# Builds a self-contained Windows package (portable zip) and, optionally, an
# NSIS installer, into ./dist. This script ONLY builds artifacts locally - it
# does not upload or publish anything. (GitHub Releases are produced by
# .github/workflows/release.yml, which simply runs this script and attaches
# the output.)
#
# Requirements:
#   - .NET 8 SDK
#   - NSIS (https://nsis.sourceforge.io/) only if you want the installer;
#     otherwise pass -SkipInstaller.
#
# Usage:
#   ./Build.ps1 -Version 0.1.0                 # zip + installer
#   ./Build.ps1 -Version 0.1.0 -SkipInstaller  # portable zip only
param(
    [string]$Version = "1.0.0",
    [switch]$SkipInstaller = $false
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "KingshotAuto Build" -ForegroundColor Cyan
Write-Host "Version: $Version" -ForegroundColor Cyan
Write-Host "Create Installer: $(-not $SkipInstaller)" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan

# Paths
$rootDir = Get-Location
$guiProject = Join-Path $rootDir "Bot.GUI\Bot.GUI.csproj"
$publishDir = Join-Path $rootDir "Bot.GUI\bin\Release\net8.0-windows\win-x64\publish"
$outputDir = Join-Path $rootDir "dist"
$packageName = "KingshotAuto-v$Version-win-x64"
$packageDir = Join-Path $outputDir $packageName

# Clean previous builds
Write-Host "`nCleaning previous builds..." -ForegroundColor Yellow
Remove-Item -Path $publishDir, $outputDir -Recurse -Force -ErrorAction SilentlyContinue

# Build and publish the application
Write-Host "`nBuilding and publishing application..." -ForegroundColor Yellow
$publishArgs = @(
    "publish", $guiProject,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-p:PublishReadyToRun=true",
    "-p:SelfContained=true",
    "-p:UseAppHost=true",
    "-p:Version=$Version",
    "-p:AssemblyVersion=$Version",
    "-p:FileVersion=$Version",
    "-o", $publishDir,
    "--verbosity", "normal"
)
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed!"; exit 1 }
Write-Host "Build successful!" -ForegroundColor Green

# Create distribution directory and copy publish output
Write-Host "`nCreating distribution package..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
Copy-Item -Path "$publishDir\*" -Destination $packageDir -Recurse -Force

# Copy custom runtime config
$runtimeConfigSource = Join-Path $rootDir "Bot.GUI\KingshotAuto.runtimeconfig.json"
if (Test-Path $runtimeConfigSource) {
    Copy-Item -Path $runtimeConfigSource -Destination (Join-Path $packageDir "KingshotAuto.runtimeconfig.json") -Force
}

# Copy template images
$templatesSource = Join-Path $rootDir "templates"
if (Test-Path $templatesSource) {
    Copy-Item -Path $templatesSource -Destination (Join-Path $packageDir "templates") -Recurse -Force
} else { Write-Warning "Templates directory not found!" }

# Set up Tesseract structure
$tessdataDest = Join-Path $packageDir "tessdata"
$x64Dir = Join-Path $packageDir "x64"
New-Item -ItemType Directory -Path $tessdataDest, $x64Dir -Force | Out-Null

# Copy OCR training data
$languageSource = Join-Path $rootDir "tessdata\eng.traineddata"
if (-not (Test-Path $languageSource)) { throw "FATAL: eng.traineddata not found at $languageSource." }
Copy-Item -Path $languageSource -Destination $tessdataDest -Force

# Copy Tesseract / Leptonica native DLLs (to x64\ and to the package root)
$runtimesX64Dir = Join-Path $rootDir "runtimes\win-x64\native"
if (-not (Test-Path $runtimesX64Dir)) { Write-Error "runtimes/win-x64/native not found."; exit 1 }
$tessDll = Get-ChildItem -Path $runtimesX64Dir -Filter "tesseract*.dll" | Select-Object -First 1
$lepDll = Get-ChildItem -Path $runtimesX64Dir -Filter "leptonica*.dll" | Select-Object -First 1
if ($tessDll -and $lepDll) {
    Copy-Item -Path $tessDll.FullName -Destination $x64Dir -Force
    Copy-Item -Path $lepDll.FullName -Destination $x64Dir -Force
    Copy-Item -Path $tessDll.FullName -Destination $packageDir -Force
    Copy-Item -Path $lepDll.FullName -Destination $packageDir -Force
} else { Write-Error "Could not find tesseract*.dll / leptonica*.dll in $runtimesX64Dir."; exit 1 }

# Copy configs (excluding the live default_config.json)
$configsSource = Join-Path $rootDir "configs"
if (Test-Path $configsSource) {
    $configsDest = Join-Path $packageDir "configs"
    New-Item -ItemType Directory -Path $configsDest -Force | Out-Null
    Get-ChildItem -Path $configsSource -File | Where-Object { $_.Name -ne "default_config.json" } | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination $configsDest -Force
    }
}

# Copy README into the package
$readmeSource = Join-Path $rootDir "README.md"
if (Test-Path $readmeSource) { Copy-Item -Path $readmeSource -Destination (Join-Path $packageDir "README.md") -Force }

# Clean up PDBs
Get-ChildItem -Path $packageDir -Filter "*.pdb" -File -Recurse | Remove-Item -Force

# Package size optimization
Write-Host "`nOptimizing package size..." -ForegroundColor Yellow
$x86Dir = Join-Path $packageDir "x86"
if (Test-Path $x86Dir) { Remove-Item -Path $x86Dir -Recurse -Force }   # app is x64 only
Get-ChildItem -Path $packageDir -Filter "opencv_videoio_ffmpeg*.dll" -Recurse | Remove-Item -Force  # video codecs unused
$designTimeAssemblies = @(
    "System.Windows.Forms.Design.dll",
    "System.Drawing.Design.dll",
    "System.ComponentModel.Design.dll",
    "Microsoft.DiaSymReader.Native.amd64.dll"
)
foreach ($assembly in $designTimeAssemblies) {
    $assemblyPath = Join-Path $packageDir $assembly
    if (Test-Path $assemblyPath) { Remove-Item -Path $assemblyPath -Force }
}
Get-ChildItem -Path $packageDir -Filter "*.xml" -File -Recurse | Remove-Item -Force
Write-Host "Package optimization complete!" -ForegroundColor Green

# Verify critical files
Write-Host "`nVerifying critical files..." -ForegroundColor Yellow
$tessDllName = (Get-ChildItem -Path $x64Dir -Filter "tesseract*.dll" | Select-Object -First 1).Name
$lepDllName = (Get-ChildItem -Path $x64Dir -Filter "leptonica*.dll" | Select-Object -First 1).Name
if (-not $tessDllName -or -not $lepDllName) { Write-Error "Missing Tesseract/Leptonica DLLs."; exit 1 }
$criticalFiles = @(
    "KingshotAuto.exe",
    "KingshotAuto.runtimeconfig.json",
    (Join-Path "x64" $lepDllName),
    (Join-Path "x64" $tessDllName),
    (Join-Path "tessdata" "eng.traineddata"),
    $lepDllName,
    $tessDllName
)
$missingFiles = @()
foreach ($file in $criticalFiles) {
    if (-not (Test-Path (Join-Path $packageDir $file))) { $missingFiles += $file }
}
if ($missingFiles.Count -gt 0) { Write-Error ("Build verification failed! Missing: " + ($missingFiles -join ', ')); exit 1 }
Write-Host "All critical files verified!" -ForegroundColor Green

# Create the portable ZIP
Write-Host "`nCreating portable zip..." -ForegroundColor Yellow
$zipFileName = "KingshotAuto-v$Version-win-x64.zip"
$zipPath = Join-Path $outputDir $zipFileName
if (Test-Path $zipPath) { Remove-Item -Path $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($packageDir, $zipPath, 'Optimal', $false)
$zipSizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host "Portable zip: $zipPath ($zipSizeMB MB)" -ForegroundColor Green

# Build the NSIS installer (optional)
if (-not $SkipInstaller) {
    Write-Host "`nBuilding NSIS installer..." -ForegroundColor Yellow

    $nsisPath = (Get-Command "makensis" -ErrorAction SilentlyContinue).Source
    if (-not $nsisPath) {
        foreach ($path in @("${env:ProgramFiles}\NSIS\makensis.exe", "${env:ProgramFiles(x86)}\NSIS\makensis.exe")) {
            if (Test-Path $path) { $nsisPath = $path; break }
        }
    }

    if (-not $nsisPath) {
        Write-Warning "NSIS not found - skipping installer. Install from https://nsis.sourceforge.io/ or pass -SkipInstaller. The portable zip is still available."
    } else {
        $installerDir = Join-Path $outputDir "Installers"
        New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

        # License text shown by the installer (MIT)
        $licenseContent = @"
MIT License

Copyright (c) 2025 KingshotAuto

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction. See the LICENSE file in the repository
for the full text.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
"@
        $licensePath = Join-Path $rootDir "Installer\License.txt"
        Set-Content -Path $licensePath -Value $licenseContent -Encoding UTF8

        # Prepare a temp NSIS script with absolute paths and the version baked in
        $absoluteBuildDir = (Resolve-Path $packageDir).Path
        $absoluteInstallerDir = (Resolve-Path $installerDir).Path
        $absoluteLicensePath = (Resolve-Path $licensePath).Path
        $absoluteToolsDir = (Resolve-Path "tools").Path
        $tempNsisScript = Join-Path $rootDir "Installer\KingshotAuto-Modern-Temp.nsi"
        $nsisScript = Get-Content (Join-Path $rootDir "Installer\KingshotAuto-Modern.nsi") -Raw
        $nsisScript = $nsisScript.Replace("dist\KingshotAuto-v`${VERSION}-win-x64\", "$absoluteBuildDir\")
        $nsisScript = $nsisScript.Replace("dist\Installers\", "$absoluteInstallerDir\")
        $nsisScript = $nsisScript.Replace("Installer\License.txt", "$absoluteLicensePath")
        $nsisScript = $nsisScript.Replace("tools\", "$absoluteToolsDir\")
        $nsisScript = $nsisScript.Replace("`${VERSION_PLACEHOLDER}", "$Version")
        Set-Content -Path $tempNsisScript -Value $nsisScript -Encoding UTF8

        & $nsisPath $tempNsisScript
        $nsisExit = $LASTEXITCODE
        Remove-Item $tempNsisScript -ErrorAction SilentlyContinue

        if ($nsisExit -eq 0) {
            Write-Host "Installer: $installerDir\KingshotAuto-Setup-v$Version.exe" -ForegroundColor Green
        } else {
            Write-Warning "NSIS installer build failed (continuing; the portable zip is still available)."
        }
    }
}

# Summary
Write-Host "`n=====================================" -ForegroundColor Green
Write-Host "Build complete!" -ForegroundColor Green
Write-Host "Output directory: $outputDir" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Green
