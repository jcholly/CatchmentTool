@echo off
:: ============================================================
:: Catchment Delineation Tool - One-Click Installer
:: Double-click to install for Civil 3D
:: ============================================================

title Catchment Tool Installer
color 0B

echo.
echo  ========================================================
echo   Catchment Delineation Tool for Civil 3D
echo   One-Click Installer
echo  ========================================================
echo.

:: Check for admin rights (needed to write to ProgramData)
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo  [!] This installer needs administrator privileges.
    echo      Right-click and select "Run as administrator"
    echo.
    pause
    exit /b 1
)

:: Get the directory where this script is located
set "SCRIPT_DIR=%~dp0"
set "BUNDLE_SOURCE=%SCRIPT_DIR%CatchmentTool.bundle"
set "APP_PLUGINS=C:\ProgramData\Autodesk\ApplicationPlugins"
set "BUNDLE_TARGET=%APP_PLUGINS%\CatchmentTool.bundle"

:: Step 1: Check for Python (try py launcher first, then python)
echo  [1/3] Checking Python installation...
set "PYTHON_CMD="

py -3 --version >nul 2>&1
if %errorLevel% equ 0 (
    set "PYTHON_CMD=py -3"
    for /f "tokens=*" %%i in ('py -3 --version 2^>^&1') do set PYVER=%%i
    echo        [OK] Found !PYVER! ^(py launcher^)
    goto :python_found
)

python --version >nul 2>&1
if %errorLevel% equ 0 (
    set "PYTHON_CMD=python"
    for /f "tokens=*" %%i in ('python --version 2^>^&1') do set PYVER=%%i
    echo        [OK] Found !PYVER!
    goto :python_found
)

echo        [X] Python not found!
echo.
echo        Please install Python 3.10+ from:
echo          https://www.python.org/downloads/
echo.
echo        IMPORTANT: Check "Add Python to PATH" during install!
echo.
pause
exit /b 1

:python_found

:: Step 2: Install Python packages
echo.
echo  [2/3] Installing Python packages...
echo        This may take a few minutes on first install...
echo.

:: Use delayed expansion for PYTHON_CMD
setlocal enabledelayedexpansion
!PYTHON_CMD! -m pip install whitebox rasterio geopandas shapely fiona numpy pyproj --quiet
if %errorLevel% neq 0 (
    echo.
    echo        [!] Warning: Some packages failed to install.
    echo            Common fix: install rasterio via conda-forge:
    echo              conda install -c conda-forge rasterio fiona geopandas
    echo.
    echo            Or try:  !PYTHON_CMD! -m pip install rasterio geopandas shapely fiona whitebox numpy pyproj
    echo.
) else (
    echo        [OK] Python packages installed
)
endlocal

:: Step 3: Install Civil 3D Bundle
echo.
echo  [3/3] Installing Civil 3D plugin...

if not exist "%BUNDLE_SOURCE%" (
    echo        [X] ERROR: CatchmentTool.bundle not found!
    echo            Expected at: %BUNDLE_SOURCE%
    pause
    exit /b 1
)

:: Check if DLL exists
if not exist "%BUNDLE_SOURCE%\Contents\CatchmentTool.dll" (
    echo        [X] ERROR: CatchmentTool.dll not found in bundle!
    echo            The plugin was not built. You need to either:
    echo              - Download a pre-built release from GitHub
    echo              - Or build from source: dotnet build CSharp\CatchmentTool.csproj -c Release
    echo.
    pause
    exit /b 1
)

:: Create ApplicationPlugins folder if needed
if not exist "%APP_PLUGINS%" (
    echo        Creating ApplicationPlugins folder...
    mkdir "%APP_PLUGINS%"
)

:: Remove existing installation
if exist "%BUNDLE_TARGET%" (
    echo        Removing previous installation...
    rmdir /s /q "%BUNDLE_TARGET%"
)

:: Copy bundle
xcopy "%BUNDLE_SOURCE%" "%BUNDLE_TARGET%" /E /I /Q /Y >nul
echo        [OK] Installed to: %BUNDLE_TARGET%

:: Done!
echo.
echo  ========================================================
echo   Installation Complete!
echo  ========================================================
echo.
echo  Next steps:
echo    1. Start (or restart) Civil 3D
echo    2. Type CATCHMENTAUTO for automated delineation
echo       or MAKECATCHMENTS to convert existing polylines
echo.
echo  For help, see README.md
echo.
pause
