@echo off
:: ============================================================
:: Catchment Delineation Tool - Uninstaller
:: ============================================================

title Catchment Tool Uninstaller
color 0C

echo.
echo  ========================================================
echo   Catchment Delineation Tool - Uninstaller
echo  ========================================================
echo.

:: Check for admin rights
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo  [!] This uninstaller needs administrator privileges.
    echo      Right-click and select "Run as administrator"
    echo.
    pause
    exit /b 1
)

set "BUNDLE_TARGET=C:\ProgramData\Autodesk\ApplicationPlugins\CatchmentTool.bundle"

if not exist "%BUNDLE_TARGET%" (
    echo  [!] CatchmentTool is not installed.
    echo      Nothing to remove.
    echo.
    pause
    exit /b 0
)

echo  Removing: %BUNDLE_TARGET%
rmdir /s /q "%BUNDLE_TARGET%"

if exist "%BUNDLE_TARGET%" (
    echo  [X] Failed to remove. Is Civil 3D running?
    echo      Please close Civil 3D and try again.
) else (
    echo  [OK] CatchmentTool has been uninstalled.
    echo.
    echo  Note: Python packages (whitebox, rasterio, etc.) were not removed.
    echo        To remove them: pip uninstall whitebox rasterio geopandas shapely fiona
)

echo.
pause
