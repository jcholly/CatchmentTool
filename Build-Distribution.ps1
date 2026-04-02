# Build-Distribution.ps1
# Creates a distribution package for the Catchment Delineation Tool
#
# Usage:
#   .\Build-Distribution.ps1              # Build only
#   .\Build-Distribution.ps1 -Install     # Build and install locally
#   .\Build-Distribution.ps1 -Version "1.2.0"  # Build with version

param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [switch]$Install,  # If set, also installs to ApplicationPlugins folder
    [switch]$SkipBuild # Skip C# build (use existing DLL)
)

$ErrorActionPreference = "Stop"

# Paths
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$CSharpDir = Join-Path $ScriptDir "CSharp"
$PythonDir = Join-Path $ScriptDir "Python"
$DistDir = Join-Path $ScriptDir "Distribution"
$BundleDir = Join-Path $DistDir "CatchmentTool.bundle"
$ContentsDir = Join-Path $BundleDir "Contents"
$BinDir = Join-Path $CSharpDir "bin"
$DataDir = Join-Path $ContentsDir "Data"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Catchment Tool - Build Distribution" -ForegroundColor Cyan
Write-Host " Version: $Version" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Build the C# project
if (-not $SkipBuild) {
    Write-Host "[1/5] Building C# project..." -ForegroundColor Yellow
    Push-Location $CSharpDir
    try {
        dotnet build -c $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed"
        }
        Write-Host "      Build successful" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
} else {
    Write-Host "[1/5] Skipping build (using existing DLL)" -ForegroundColor Gray
}

# Step 2: Create distribution folders
Write-Host "[2/5] Creating distribution folders..." -ForegroundColor Yellow
$PythonContentsDir = Join-Path $ContentsDir "Python"

# Clean and recreate Contents folder
if (Test-Path $ContentsDir) {
    Remove-Item $ContentsDir -Recurse -Force
}
New-Item -ItemType Directory -Path $ContentsDir -Force | Out-Null
New-Item -ItemType Directory -Path $PythonContentsDir -Force | Out-Null
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
Write-Host "      Folders created" -ForegroundColor Green

# Step 3: Copy files
Write-Host "[3/5] Copying files..." -ForegroundColor Yellow

# Copy DLL
$dllSource = Join-Path $BinDir "CatchmentTool.dll"
if (Test-Path $dllSource) {
    Copy-Item $dllSource -Destination $ContentsDir -Force
    Write-Host "      [OK] CatchmentTool.dll" -ForegroundColor Gray
} else {
    Write-Host "      [X] CatchmentTool.dll not found at $dllSource" -ForegroundColor Red
    exit 1
}

# Copy Newtonsoft.Json if present
$jsonDll = Join-Path $BinDir "Newtonsoft.Json.dll"
if (Test-Path $jsonDll) {
    Copy-Item $jsonDll -Destination $ContentsDir -Force
    Write-Host "      [OK] Newtonsoft.Json.dll" -ForegroundColor Gray
}

# Copy all Python files
$pythonFiles = @(
    "catchment_delineation.py",
    "crs_utils.py",
    "validation.py",
    "requirements.txt",
    "config_template.json"
)

foreach ($file in $pythonFiles) {
    $srcFile = Join-Path $PythonDir $file
    if (Test-Path $srcFile) {
        Copy-Item $srcFile -Destination $PythonContentsDir -Force
        Write-Host "      [OK] $file" -ForegroundColor Gray
    }
}

# Create .gitkeep in Data folder
"" | Out-File (Join-Path $DataDir ".gitkeep") -Encoding UTF8

Write-Host "      Files copied" -ForegroundColor Green

# Step 4: Update PackageContents.xml with version
Write-Host "[4/5] Updating package metadata..." -ForegroundColor Yellow
$packageXmlPath = Join-Path $BundleDir "PackageContents.xml"
if (Test-Path $packageXmlPath) {
    $xml = Get-Content $packageXmlPath -Raw
    $xml = $xml -replace 'AppVersion="[^"]*"', "AppVersion=`"$Version`""
    $xml | Set-Content $packageXmlPath -Encoding UTF8 -NoNewline
    Write-Host "      Updated version to $Version" -ForegroundColor Gray
}

# Step 5: Create ZIP archive
Write-Host "[5/5] Creating ZIP archive..." -ForegroundColor Yellow
$zipName = "CatchmentTool-v$Version.zip"
$zipPath = Join-Path $DistDir $zipName

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

# Include all distribution files
$itemsToZip = @(
    (Join-Path $DistDir "README.md"),
    (Join-Path $DistDir "Install.bat"),
    (Join-Path $DistDir "Install-CatchmentTool.ps1"),
    (Join-Path $DistDir "Uninstall.bat"),
    $BundleDir
) | Where-Object { Test-Path $_ }

Compress-Archive -Path $itemsToZip -DestinationPath $zipPath -Force
$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host "      Created: $zipName ($zipSize MB)" -ForegroundColor Green

# Optional: Install to ApplicationPlugins
if ($Install) {
    Write-Host ""
    Write-Host "[INSTALL] Installing to ApplicationPlugins..." -ForegroundColor Yellow
    $appPlugins = "C:\ProgramData\Autodesk\ApplicationPlugins"
    $targetBundle = Join-Path $appPlugins "CatchmentTool.bundle"
    
    if (-not (Test-Path $appPlugins)) {
        New-Item -ItemType Directory -Path $appPlugins -Force | Out-Null
    }
    
    if (Test-Path $targetBundle) {
        Remove-Item $targetBundle -Recurse -Force
    }
    
    Copy-Item $BundleDir -Destination $appPlugins -Recurse -Force
    Write-Host "      Installed to: $targetBundle" -ForegroundColor Green
    Write-Host "      Restart Civil 3D to load the plugin" -ForegroundColor Cyan
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Distribution complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Distribution package:" -ForegroundColor White
Write-Host "  $zipPath" -ForegroundColor Yellow
Write-Host ""
Write-Host "To share with others:" -ForegroundColor White
Write-Host "  1. Send them the ZIP file" -ForegroundColor Gray
Write-Host "  2. They extract it anywhere" -ForegroundColor Gray
Write-Host "  3. They right-click Install.bat -> Run as Administrator" -ForegroundColor Gray
Write-Host ""
Write-Host "To install locally:" -ForegroundColor White
Write-Host "  .\Build-Distribution.ps1 -Install" -ForegroundColor Gray
