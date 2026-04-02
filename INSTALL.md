# Installation Guide - Civil 3D Catchment Delineation Tool

## Prerequisites

- **Civil 3D:** AutoCAD Civil 3D 2026
- **Python:** 3.10 or later ([python.org/downloads](https://www.python.org/downloads/))
- **.NET 8.0 SDK** (for building from source only)

---

## Option A: Install from Release ZIP (recommended for end users)

1. Download the latest `CatchmentTool-v*.zip` from the [Releases](https://github.com/jcholly/CatchmentTool/releases) page
2. Extract it anywhere
3. Right-click **Install.bat** → **Run as administrator**
   - Installs Python dependencies and copies the plugin bundle to ApplicationPlugins
   - Alternatively, right-click **Install-CatchmentTool.ps1** → **Run with PowerShell** (better error messages)
4. Start (or restart) Civil 3D

## Option B: Build from Source (developers)

```powershell
# Clone the repo
git clone https://github.com/jcholly/CatchmentTool.git
cd CatchmentTool

# Install Python dependencies
pip install -r Python/requirements.txt

# Build C# plugin and install to ApplicationPlugins
.\Build-Distribution.ps1 -Install
```

> **Note:** The C# build requires Civil 3D 2026 DLLs. If Civil 3D is not at the default path, set the environment variable `CIVIL3D_PATH` or pass it directly:
> ```powershell
> dotnet build CSharp/CatchmentTool.csproj -c Release -p:Civil3DPath="D:\Your\Civil3D\Path"
> ```

### Updating Python-only changes

If you only changed Python files (no C# changes), you can redeploy without restarting Civil 3D:

```powershell
.\Build-Distribution.ps1 -Install -SkipBuild
```

---

## Python Dependencies on Windows

The GIS stack (`rasterio`, `fiona`, `geopandas`) can be tricky to install on Windows via pip alone. If `pip install` fails with compilation errors:

**Option 1 — conda-forge (most reliable):**
```
conda install -c conda-forge rasterio fiona geopandas shapely whitebox numpy pyproj
```

**Option 2 — Pre-built wheels:**
Download wheels from [Christoph Gohlke's page](https://github.com/cgohlke/geospatial-wheels/) and install manually:
```
pip install rasterio-*.whl fiona-*.whl
pip install geopandas shapely whitebox numpy pyproj
```

---

## Verify Installation

1. Open Civil 3D 2026
2. Open a drawing with a TIN surface and pipe network
3. Type `CATCHMENTAUTO` and press Enter
4. The delineation dialog should appear

---

## Available Commands

| Command | Description |
|---------|-------------|
| `CATCHMENTAUTO` | Opens a dialog to automatically delineate catchments from a surface and pipe network using WhiteboxTools |
| `MAKECATCHMENTS` | Select existing closed polylines and convert them to Civil 3D Catchment objects with outlet structures assigned |

---

## Troubleshooting

### Python not found
The tool searches for Python in this order: bundled Python, `py` launcher, `python` on PATH, common install locations. Make sure Python 3.10+ is installed. On Windows, the `py` launcher (installed by default with Python) is the most reliable method.

### WhiteboxTools errors
Verify WhiteboxTools is installed:
```powershell
python -c "import whitebox; wbt = whitebox.WhiteboxTools(); print(wbt.version())"
```

### Plugin not loading
Check that the bundle exists at:
```
C:\ProgramData\Autodesk\ApplicationPlugins\CatchmentTool.bundle\
```
The folder must contain `Contents\CatchmentTool.dll`. If the DLL is missing, you need to build from source or download a release ZIP.

### "No inlet structures found"
The tool looks for structures with part families containing: inlet, catch basin, area drain, curb inlet, grate, etc. If your structures use different naming, modify the filter in `StructureExtractor.cs`.

---

## Uninstalling

Run `Uninstall.bat` from the distribution, or manually delete:
```
C:\ProgramData\Autodesk\ApplicationPlugins\CatchmentTool.bundle\
```
