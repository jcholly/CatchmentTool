# Installation Guide - Civil 3D Catchment Delineation Tool

## Prerequisites

### Python Environment
- Python 3.10 or later
- pip package manager

### Civil 3D
- AutoCAD Civil 3D 2026
- .NET Framework 4.8

### Visual Studio (for building C# plugin)
- Visual Studio 2022 (Community edition is fine)
- .NET Framework 4.8 targeting pack

---

## Step 1: Install Python Dependencies

Open a command prompt and navigate to the Python folder:

```powershell
cd CatchmentTool\Python

# Install dependencies
pip install -r requirements.txt
```

This installs:
- WhiteboxTools - hydrological analysis
- rasterio - raster I/O
- geopandas - vector data handling
- shapely - geometry operations
- numpy - numerical operations

### Verify WhiteboxTools Installation

```powershell
python -c "import whitebox; wbt = whitebox.WhiteboxTools(); print(wbt.version())"
```

You should see the WhiteboxTools version printed.

---

## Step 2: Build the Civil 3D Plugin

### Option A: Using Visual Studio

1. Open `CatchmentTool\CSharp\CatchmentTool.csproj` in Visual Studio 2022
2. Update the Civil 3D reference paths if needed (in the .csproj file)
3. Build the solution (Ctrl+Shift+B)
4. The output DLL will be in `CatchmentTool\CSharp\bin\`

### Option B: Using dotnet CLI

```powershell
cd CatchmentTool\CSharp

# Restore and build
dotnet build -c Release
```

---

## Step 3: Load Plugin in Civil 3D

### Manual Loading (for testing)

1. Open Civil 3D 2026
2. Type `NETLOAD` at the command prompt
3. Browse to `CatchmentTool\CSharp\bin\CatchmentTool.dll`
4. Click Open

### Automatic Loading (recommended)

Create a file named `CatchmentTool.bundle` in your Civil 3D plugin folder:

**Location:** `C:\Users\<username>\AppData\Roaming\Autodesk\ApplicationPlugins\`

```xml
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage 
    SchemaVersion="1.0" 
    Name="Catchment Delineation Tool" 
    Description="Automated catchment delineation using WhiteboxTools"
    Author="Your Name"
    Version="1.0.0">
    
    <CompanyDetails Name="Your Company" />
    
    <Components>
        <RuntimeRequirements OS="Win64" Platform="AutoCAD Civil 3D" SeriesMin="C3D2026" />
        <ComponentEntry AppName="CatchmentTool" 
                        ModuleName="./Contents/CatchmentTool.dll" 
                        LoadOnAutoCADStartup="True" />
    </Components>
</ApplicationPackage>
```

Then copy `CatchmentTool.dll` and `Newtonsoft.Json.dll` to a `Contents` subfolder.

---

## Step 4: Verify Installation

1. Open Civil 3D 2026
2. Open a drawing with a surface and pipe network
3. Type `LISTPIPESTRUCTURES` to verify the plugin loaded
4. You should see a list of structures in the command window

---

## Available Commands

| Command | Description |
|---------|-------------|
| `EXPORTCATCHMENTDATA` | Export surface DEM and inlet structures |
| `IMPORTCATCHMENTS` | Import catchment polygons from WhiteboxTools |
| `DELINEATECATCHMENTS` | Full workflow (export, process, import) |
| `LISTPIPESTRUCTURES` | List all structures in a pipe network |

---

## Typical Workflow

1. **In Civil 3D:**
   ```
   Command: EXPORTCATCHMENTDATA
   ```
   - Select surface
   - Select pipe network
   - Choose working directory (or accept default)

2. **In Command Prompt:**
   ```powershell
   cd "C:\path\to\CatchmentData"
   python "C:\...\CatchmentTool\Python\run_delineation.py" .
   ```

3. **In Civil 3D:**
   ```
   Command: IMPORTCATCHMENTS
   ```
   - Select the generated `catchments.json` file
   - Select pipe network
   - Select surface

---

## Troubleshooting

### "No inlet structures found"
The tool looks for structures with names containing: inlet, catch basin, area drain, curb inlet, grate, etc. If your structures use different naming, you may need to modify the filter in `StructureExtractor.cs`.

### WhiteboxTools errors
Make sure Python can find the whitebox module:
```powershell
python -c "import whitebox; print(whitebox.__file__)"
```

### DEM conversion issues
If the BIL to GeoTIFF conversion fails, check that rasterio is properly installed:
```powershell
python -c "import rasterio; print(rasterio.__version__)"
```

### Large surfaces
For very large surfaces, the DEM export may take several minutes. Consider:
- Using a coarser cell size (add `--cell-size` parameter in future version)
- Exporting a smaller area of the surface

---

## Configuration

Create a `config.json` file in your working directory to customize delineation:

```json
{
    "depression_method": "breach",
    "snap_distance": 10.0,
    "min_catchment_area": 100.0
}
```

- `depression_method`: "breach" (recommended) or "fill"
- `snap_distance`: Distance to snap pour points to flow network (map units)
- `min_catchment_area`: Minimum catchment size to include (sq map units)

