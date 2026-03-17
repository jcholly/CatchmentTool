@echo off
echo ========================================
echo  Installing Python Dependencies
echo  for Catchment Delineation Tool
echo ========================================
echo.

REM Check if Python is installed
python --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: Python is not installed or not in PATH
    echo.
    echo Please install Python 3.10 or later from:
    echo   https://www.python.org/downloads/
    echo.
    echo Make sure to check "Add Python to PATH" during installation
    pause
    exit /b 1
)

echo Found Python:
python --version
echo.

echo Installing required packages...
echo.

pip install rasterio geopandas shapely fiona whitebox numpy

if errorlevel 1 (
    echo.
    echo ========================================
    echo  Some packages failed to install
    echo ========================================
    echo.
    echo Try running this script as Administrator
    echo or install packages individually:
    echo   pip install rasterio
    echo   pip install geopandas
    echo   pip install shapely
    echo   pip install fiona
    echo   pip install whitebox
    echo   pip install numpy
    echo.
) else (
    echo.
    echo ========================================
    echo  Installation Complete!
    echo ========================================
    echo.
    echo Python dependencies are now installed.
    echo You can now use the Catchment Delineation Tool in Civil 3D.
    echo.
)

pause
