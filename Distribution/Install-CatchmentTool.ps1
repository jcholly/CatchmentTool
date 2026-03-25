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

    $pythonCmd = $null

    # Try py launcher first (most reliable on Windows)
    try {
        $pyVersion = & py -3 --version 2>&1
        if ($LASTEXITCODE -eq 0 -and $pyVersion -match "Python 3\.") {
            $pythonCmd = "py -3"
            Write-Host "      Found: $pyVersion (via py launcher)" -ForegroundColor Green
        }
    } catch {}

    # Try python
    if (-not $pythonCmd) {
        try {
            $pyVersion = & python --version 2>&1
            if ($LASTEXITCODE -eq 0 -and $pyVersion -match "Python 3\.") {
                $pythonCmd = "python"
                Write-Host "      Found: $pyVersion" -ForegroundColor Green
            }
        } catch {}
    }

    # Try python3
    if (-not $pythonCmd) {
        try {
            $pyVersion = & python3 --version 2>&1
            if ($LASTEXITCODE -eq 0 -and $pyVersion -match "Python 3\.") {
                $pythonCmd = "python3"
                Write-Host "      Found: $pyVersion" -ForegroundColor Green
            }
        } catch {}
    }

    if (-not $pythonCmd) {
        Write-Host "      WARNING: Python 3 not found!" -ForegroundColor Red
        Write-Host ""
        Write-Host "      Please install Python 3.10+ from:" -ForegroundColor White
        Write-Host "        https://www.python.org/downloads/" -ForegroundColor Gray
        Write-Host ""
        Write-Host "      Make sure to check 'Add Python to PATH' during installation." -ForegroundColor White
        Write-Host "      You can also use the 'py' launcher (installed by default)." -ForegroundColor White
        Write-Host ""
        Write-Host "      You can skip this step with -SkipPython and install packages later." -ForegroundColor Gray
        Write-Host "      The plugin will auto-install packages on first run." -ForegroundColor Gray
        Write-Host ""
        $response = Read-Host "Continue without Python? (Y/N)"
        if ($response -ne "Y" -and $response -ne "y") {
            exit 1
        }
    }

    # Install Python dependencies
    if ($pythonCmd) {
        Write-Host "[2/3] Installing Python packages..." -ForegroundColor Yellow
        Write-Host "      This may take a few minutes on first install..." -ForegroundColor Gray

        $pipCmd = if ($pythonCmd -eq "py -3") { "py -3 -m pip" } else { "$pythonCmd -m pip" }

        # Install all at once for efficiency
        $packages = "whitebox rasterio geopandas shapely fiona numpy pyproj"
        Write-Host "      Installing: $packages" -ForegroundColor Gray

        try {
            if ($pythonCmd -eq "py -3") {
                & py -3 -m pip install $packages.Split(" ") --quiet 2>&1 | Out-Null
            } else {
                & $pythonCmd -m pip install $packages.Split(" ") --quiet 2>&1 | Out-Null
            }

            if ($LASTEXITCODE -eq 0) {
                Write-Host "      Python packages installed successfully" -ForegroundColor Green
            } else {
                Write-Host "      WARNING: Some packages may have failed to install." -ForegroundColor Yellow
                Write-Host "      The plugin will retry on first run." -ForegroundColor Yellow
                Write-Host "      If rasterio fails, try: conda install -c conda-forge rasterio" -ForegroundColor Gray
            }
        } catch {
            Write-Host "      WARNING: pip install failed. The plugin will retry on first run." -ForegroundColor Yellow
        }
    }
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

# Check if DLL exists in bundle
$dllPath = Join-Path $bundleSource "Contents\CatchmentTool.dll"
if (-not (Test-Path $dllPath)) {
    Write-Host "      WARNING: CatchmentTool.dll not found in bundle!" -ForegroundColor Yellow
    Write-Host "      The plugin may not have been built yet." -ForegroundColor Yellow
    Write-Host "      Run Build-Distribution.ps1 first, or build with:" -ForegroundColor Yellow
    Write-Host "        dotnet build CSharp/CatchmentTool.csproj -c Release" -ForegroundColor Gray
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
Write-Host "Troubleshooting:" -ForegroundColor White
Write-Host "  - Python packages install automatically on first run" -ForegroundColor Gray
Write-Host "  - If rasterio fails: conda install -c conda-forge rasterio" -ForegroundColor Gray
Write-Host "  - Assign a coordinate system in Civil 3D Drawing Settings" -ForegroundColor Gray
Write-Host ""

Read-Host "Press Enter to close"
