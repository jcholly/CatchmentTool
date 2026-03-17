# Install-CatchmentTool.ps1
# Installs the Catchment Delineation Tool for Civil 3D

param(
    [switch]$SkipPython  # Skip Python dependency installation
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Catchment Delineation Tool Installer" -ForegroundColor Cyan
Write-Host " for Autodesk Civil 3D" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Check for Python
if (-not $SkipPython) {
    Write-Host "[1/3] Checking Python installation..." -ForegroundColor Yellow
    
    try {
        $pythonVersion = python --version 2>&1
        Write-Host "      Found: $pythonVersion" -ForegroundColor Green
    }
    catch {
        Write-Host "      ERROR: Python not found!" -ForegroundColor Red
        Write-Host ""
        Write-Host "      Please install Python 3.10+ from:" -ForegroundColor White
        Write-Host "        https://www.python.org/downloads/" -ForegroundColor Gray
        Write-Host ""
        Write-Host "      Make sure to check 'Add Python to PATH' during installation" -ForegroundColor White
        Write-Host ""
        Read-Host "Press Enter to exit"
        exit 1
    }
    
    # Install Python dependencies
    Write-Host "[2/3] Installing Python packages..." -ForegroundColor Yellow
    Write-Host "      This may take a few minutes..." -ForegroundColor Gray
    
    $packages = @("rasterio", "geopandas", "shapely", "fiona", "whitebox", "numpy")
    
    foreach ($pkg in $packages) {
        Write-Host "      Installing $pkg..." -ForegroundColor Gray
        pip install $pkg --quiet 2>&1 | Out-Null
    }
    
    Write-Host "      Python packages installed" -ForegroundColor Green
}
else {
    Write-Host "[1/3] Skipping Python check (--SkipPython)" -ForegroundColor Gray
    Write-Host "[2/3] Skipping Python packages (--SkipPython)" -ForegroundColor Gray
}

# Step 3: Install Civil 3D Bundle
Write-Host "[3/3] Installing Civil 3D plugin..." -ForegroundColor Yellow

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$bundleSource = Join-Path $scriptDir "CatchmentTool.bundle"
$appPlugins = "C:\ProgramData\Autodesk\ApplicationPlugins"
$bundleTarget = Join-Path $appPlugins "CatchmentTool.bundle"

# Check if bundle folder exists in distribution
if (-not (Test-Path $bundleSource)) {
    Write-Host "      ERROR: CatchmentTool.bundle not found!" -ForegroundColor Red
    Write-Host "      Expected at: $bundleSource" -ForegroundColor Gray
    Read-Host "Press Enter to exit"
    exit 1
}

# Create ApplicationPlugins folder if it doesn't exist
if (-not (Test-Path $appPlugins)) {
    Write-Host "      Creating ApplicationPlugins folder..." -ForegroundColor Gray
    New-Item -ItemType Directory -Path $appPlugins -Force | Out-Null
}

# Remove existing installation
if (Test-Path $bundleTarget) {
    Write-Host "      Removing previous installation..." -ForegroundColor Gray
    Remove-Item $bundleTarget -Recurse -Force
}

# Copy bundle
Copy-Item $bundleSource -Destination $appPlugins -Recurse -Force

Write-Host "      Installed to: $bundleTarget" -ForegroundColor Green

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Installation Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Start (or restart) Civil 3D" -ForegroundColor Gray
Write-Host "  2. Type CATCHMENTAUTO and press Enter" -ForegroundColor Gray
Write-Host "  3. Select surface, network, and inlet types" -ForegroundColor Gray
Write-Host "  4. Click 'Run Delineation'" -ForegroundColor Gray
Write-Host ""
Write-Host "For help, see README.md" -ForegroundColor White
Write-Host ""

Read-Host "Press Enter to close"
