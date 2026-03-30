# Catchment Delineation Tool for Civil 3D

**Automated watershed/catchment delineation** for Civil 3D pipe networks using WhiteboxTools.

---

## Quick Install

### Prerequisites
- **Python 3.10+** - Download from [python.org](https://www.python.org/downloads/)
  Check "Add Python to PATH" during installation!
- **Civil 3D 2026**

### Installation Steps

1. **Extract this ZIP** to any folder
2. **Right-click `Install.bat`** and select **Run as administrator**
3. **Restart Civil 3D** (if it was open)

The installer will:
- Check for Python
- Install required Python packages (whitebox, rasterio, geopandas, shapely)
- Copy the plugin to Civil 3D's ApplicationPlugins folder

---

## Commands

### `CATCHMENTAUTO` - Automated Delineation

1. Type `CATCHMENTAUTO` in Civil 3D and press Enter
2. Select a **TIN Surface** and **Pipe Network**
3. Check which **structure types** to use as inlets
4. Click **Run Delineation**

The tool exports your surface as a DEM, runs WhiteboxTools watershed analysis, and creates Civil 3D Catchment objects linked to each inlet structure.

### `MAKECATCHMENTS` - From Existing Linework

1. Draw or import closed polylines representing catchment boundaries
2. Type `MAKECATCHMENTS` and press Enter
3. Select the polylines, then select the reference surface
4. The tool finds the pipe network structure inside each polygon and creates Civil 3D Catchment objects with outlets assigned

### Analysis Settings (Optional)

Expand "Analysis Settings" in the CATCHMENTAUTO dialog to fine-tune:

| Setting | Default | Description |
|---------|---------|-------------|
| DEM Cell Size | 1.0 ft | Higher = faster, Lower = more accurate |
| Snap Distance | 10 ft | How far inlets snap to flow paths |
| Min Catchment Area | 100 sq ft | Filter out tiny catchments |
| Burn Pipes | Yes | Improve accuracy along pipe routes |

---

## Troubleshooting

### "Python not found"
- Install Python 3.10+ from [python.org](https://www.python.org/downloads/)
- **IMPORTANT:** Check "Add Python to PATH" during installation
- Restart your computer after installing Python

### "No module named 'rasterio'" or similar
Run this in Command Prompt:
```
pip install rasterio geopandas shapely fiona whitebox numpy
```

### Catchments look wrong
- Try decreasing DEM Cell Size (e.g., 0.5 ft)
- Enable "Burn Pipes" option
- Check that your surface has good coverage

---

## Uninstall

Right-click `Uninstall.bat` and select Run as administrator.

---

## Support

For issues or feature requests: [github.com/jcholly/CatchmentTool](https://github.com/jcholly/CatchmentTool)
