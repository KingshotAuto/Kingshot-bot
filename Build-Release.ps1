# KingshotAuto Release Build Script
param(
    [string]$Version = "1.0.0",
    [switch]$SkipUpdatePackage = $false,
    [switch]$SkipInstaller = $false,
    [string]$InstallerType = "NSIS"
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "KingshotAuto Release Build Script" -ForegroundColor Cyan
Write-Host "Version: $Version" -ForegroundColor Cyan
Write-Host "Create Update Package: $(-not $SkipUpdatePackage)" -ForegroundColor Cyan
Write-Host "Create Installer: $(-not $SkipInstaller)" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan

# Set paths
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
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed!"
    exit 1
}

Write-Host "Build successful!" -ForegroundColor Green


# Create distribution directory
Write-Host "`nCreating distribution package..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

# Copy publish contents
Write-Host "Copying runtime dependencies from publish directory..." -ForegroundColor Gray
Copy-Item -Path "$publishDir\*" -Destination $packageDir -Recurse -Force

# Copy custom runtime config
$runtimeConfigSource = Join-Path $rootDir "Bot.GUI\KingshotAuto.runtimeconfig.json"
$runtimeConfigDest = Join-Path $packageDir "KingshotAuto.runtimeconfig.json"
if (Test-Path $runtimeConfigSource) {
    Write-Host "Copying custom runtime config..." -ForegroundColor Gray
    Copy-Item -Path $runtimeConfigSource -Destination $runtimeConfigDest -Force
} else {
    Write-Warning "Custom runtime config not found!"
}

# Copy templates if exist
$templatesSource = Join-Path $rootDir "templates"
$templatesDest = Join-Path $packageDir "templates"
if (Test-Path $templatesSource) {
    Copy-Item -Path $templatesSource -Destination $templatesDest -Recurse -Force
} else {
    Write-Warning "Templates directory not found!"
}

# Setup Tesseract structure
$tessdataDest = Join-Path $packageDir "tessdata"
$x64Dir = Join-Path $packageDir "x64"
New-Item -ItemType Directory -Path $tessdataDest, $x64Dir -Force | Out-Null

# Copy traineddata
$languageFile = "eng.traineddata"
$tessdataDir = Join-Path $rootDir "tessdata"
$languageSource = Join-Path $tessdataDir $languageFile
if (-not (Test-Path $languageSource)) {
    throw "FATAL: eng.traineddata not found at $languageSource. Please ensure it is present."
}
Copy-Item -Path $languageSource -Destination $tessdataDest -Force

# Copy Tesseract DLLs from local runtimes directory
$runtimesX64Dir = Join-Path $rootDir "runtimes\win-x64\native"
if (-not (Test-Path $runtimesX64Dir)) {
    Write-Error "Could not find runtimes/win-x64/native directory. Build cannot continue."
    exit 1
}

$tessDll = Get-ChildItem -Path $runtimesX64Dir -Filter "tesseract*.dll" | Select-Object -First 1
$lepDll = Get-ChildItem -Path $runtimesX64Dir -Filter "leptonica*.dll" | Select-Object -First 1

if ($tessDll -and $lepDll) {
    Write-Host "Found Tesseract DLLs in runtimes: $($tessDll.Name), $($lepDll.Name)" -ForegroundColor Cyan
    Write-Host "Copying DLLs to package x64 directory..." -ForegroundColor Gray
    Copy-Item -Path $tessDll.FullName -Destination $x64Dir -Force
    Copy-Item -Path $lepDll.FullName -Destination $x64Dir -Force
    
    # Also copy to root for compatibility
    Write-Host "Copying DLLs to package root directory..." -ForegroundColor Gray
    Copy-Item -Path $tessDll.FullName -Destination $packageDir -Force
    Copy-Item -Path $lepDll.FullName -Destination $packageDir -Force
} else {
    Write-Error "Could not find tesseract*.dll or leptonica*.dll in $runtimesX64Dir. Build cannot continue."
    exit 1
}

# Copy configs if they exist (excluding default_config.json)
$configsSource = Join-Path $rootDir "configs"
$configsDest = Join-Path $packageDir "configs"
if (Test-Path $configsSource) {
    Write-Host "Copying configs (excluding default_config.json)..." -ForegroundColor Gray
    New-Item -ItemType Directory -Path $configsDest -Force | Out-Null
    Get-ChildItem -Path $configsSource -File | Where-Object { $_.Name -ne "default_config.json" } | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination $configsDest -Force
    }
}

# Copy README into the package
$readmeSource = Join-Path $rootDir "README.md"
$readmeDest = Join-Path $packageDir "README.md"
if (Test-Path $readmeSource) {
    Copy-Item -Path $readmeSource -Destination $readmeDest -Force
} else {
    Write-Warning "README.md not found in root directory!"
}

# Clean up PDBs
Get-ChildItem -Path $packageDir -Filter "*.pdb" -File -Recurse | Remove-Item -Force

# ============================================
# Package Size Optimization
# ============================================
Write-Host "`nOptimizing package size..." -ForegroundColor Yellow

# Remove x86 folder (app is x64 only) - saves ~5.5 MB
$x86Dir = Join-Path $packageDir "x86"
if (Test-Path $x86Dir) {
    $x86Size = (Get-ChildItem -Path $x86Dir -Recurse | Measure-Object -Property Length -Sum).Sum
    Remove-Item -Path $x86Dir -Recurse -Force
    Write-Host "  Removed x86 folder (~$([math]::Round($x86Size / 1MB, 1)) MB saved)" -ForegroundColor Green
}

# Remove FFmpeg video DLL (not used for screenshots) - saves ~27 MB
$ffmpegDlls = Get-ChildItem -Path $packageDir -Filter "opencv_videoio_ffmpeg*.dll" -Recurse
foreach ($dll in $ffmpegDlls) {
    $dllSize = $dll.Length
    Remove-Item -Path $dll.FullName -Force
    Write-Host "  Removed $($dll.Name) (~$([math]::Round($dllSize / 1MB, 1)) MB saved)" -ForegroundColor Green
}

# Remove unnecessary design-time assemblies - saves ~5-10 MB
$designTimeAssemblies = @(
    "System.Windows.Forms.Design.dll",
    "System.Drawing.Design.dll",
    "System.ComponentModel.Design.dll",
    "Microsoft.DiaSymReader.Native.amd64.dll"
)
foreach ($assembly in $designTimeAssemblies) {
    $assemblyPath = Join-Path $packageDir $assembly
    if (Test-Path $assemblyPath) {
        $assemblySize = (Get-Item $assemblyPath).Length
        Remove-Item -Path $assemblyPath -Force
        Write-Host "  Removed $assembly (~$([math]::Round($assemblySize / 1MB, 1)) MB saved)" -ForegroundColor Green
    }
}

# Remove XML documentation files (not needed at runtime)
$xmlDocs = Get-ChildItem -Path $packageDir -Filter "*.xml" -File -Recurse
$xmlTotalSize = ($xmlDocs | Measure-Object -Property Length -Sum).Sum
if ($xmlDocs.Count -gt 0) {
    $xmlDocs | Remove-Item -Force
    Write-Host "  Removed $($xmlDocs.Count) XML doc files (~$([math]::Round($xmlTotalSize / 1MB, 1)) MB saved)" -ForegroundColor Green
}

Write-Host "Package optimization complete!" -ForegroundColor Green

# Verify critical files
Write-Host "`nVerifying critical files..." -ForegroundColor Yellow

$tessDllName = (Get-ChildItem -Path $x64Dir -Filter "tesseract*.dll" | Select-Object -First 1).Name
$lepDllName = (Get-ChildItem -Path $x64Dir -Filter "leptonica*.dll" | Select-Object -First 1).Name

if (-not $tessDllName -or -not $lepDllName) {
    Write-Error "Verification failed: missing Tesseract or Leptonica DLLs in x64 directory."
    exit 1
}

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
    $filePath = Join-Path $packageDir $file
    if (-not (Test-Path $filePath)) {
        $missingFiles += $file
        Write-Host ('✗ Missing critical file: ' + $file) -ForegroundColor Red
    } else {
        Write-Host ('✓ Found critical file: ' + $file) -ForegroundColor Green
    }
}

if ($missingFiles.Count -gt 0) {
    Write-Error ('Build verification failed! Missing: ' + ($missingFiles -join ', '))
    exit 1
}

# Create update package if requested
if (-not $SkipUpdatePackage) {
    Write-Host "`nCreating update package..." -ForegroundColor Yellow
    
    # Create ZIP package for updates
    $zipFileName = "KingshotAuto-v$Version-win-x64.zip"
    $zipPath = Join-Path $outputDir $zipFileName
    
    Write-Host "Creating ZIP package: $zipFileName" -ForegroundColor Gray
    
    # Remove existing ZIP if present
    if (Test-Path $zipPath) {
        Remove-Item -Path $zipPath -Force
    }
    
    # Create ZIP archive
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($packageDir, $zipPath, 'Optimal', $false)
        
        Write-Host "ZIP package created successfully!" -ForegroundColor Green
        Write-Host "Package location: $zipPath" -ForegroundColor Cyan
        
        # Get file size
        $fileInfo = Get-Item $zipPath
        $fileSizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
        Write-Host "Package size: $fileSizeMB MB" -ForegroundColor Gray
        
    } catch {
        Write-Error "Failed to create ZIP package: $_"
        exit 1
    }
}

# Create installer if requested
if (-not $SkipInstaller) {
    Write-Host "`nCreating installer..." -ForegroundColor Yellow

    try {
        # Run Build-Installer.ps1
        $installerScript = Join-Path $rootDir "Build-Installer.ps1"
        if (Test-Path $installerScript) {
            Write-Host "Running installer build script..." -ForegroundColor Gray
            & $installerScript -Version $Version -InstallerType $InstallerType

            if ($LASTEXITCODE -eq 0) {
                Write-Host "Installer created successfully!" -ForegroundColor Green

                # Verify installer was created
                $expectedInstallerPath = Join-Path $outputDir "Installers\KingshotAuto-Setup-v$Version.exe"
                if (Test-Path $expectedInstallerPath) {
                    $installerInfo = Get-Item $expectedInstallerPath
                    $installerSizeMB = [math]::Round($installerInfo.Length / 1MB, 2)
                    Write-Host "Installer location: $expectedInstallerPath" -ForegroundColor Cyan
                    Write-Host "Installer size: $installerSizeMB MB" -ForegroundColor Gray
                } else {
                    Write-Warning "Installer file not found at expected location: $expectedInstallerPath"
                }
            } else {
                Write-Warning "Installer build failed, but continuing..."
            }
        } else {
            Write-Warning "Build-Installer.ps1 script not found!"
        }
    } catch {
        Write-Warning "Failed to create installer: $_"
        Write-Host "Continuing without installer..." -ForegroundColor Yellow
    }
}

# Summary
Write-Host "`n=====================================" -ForegroundColor Green
Write-Host "Build Complete!" -ForegroundColor Green
Write-Host "=====================================" -ForegroundColor Green
Write-Host ('Package Location: ' + $packageDir) -ForegroundColor Cyan
Write-Host 'All required Tesseract files verified!' -ForegroundColor Green
if (-not $SkipUpdatePackage) {
    Write-Host 'Update package created!' -ForegroundColor Green
    $zipPath = Join-Path $outputDir "KingshotAuto-v$Version-win-x64.zip"
    if (Test-Path $zipPath) {
        Write-Host ('Update package location: ' + $zipPath) -ForegroundColor Cyan
    }
}
if (-not $SkipInstaller) {
    $expectedInstallerPath = Join-Path $outputDir "Installers\KingshotAuto-Setup-v$Version.exe"
    if (Test-Path $expectedInstallerPath) {
        Write-Host 'Professional installer created!' -ForegroundColor Green
        Write-Host ('Installer location: ' + $expectedInstallerPath) -ForegroundColor Cyan
    }
}
Write-Host 'The package is ready for distribution!' -ForegroundColor Green