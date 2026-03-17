# Catchment Delineation Tool for Civil 3D

**One-click automated watershed/catchment delineation** for Civil 3D pipe networks using WhiteboxTools.

---

## Quick Install

### Prerequisites
- **Python 3.10+** - Download from [python.org](https://www.python.org/downloads/)  
  ⚠️ Check "Add Python to PATH" during installation!
- **Civil 3D 2024 or later** (tested on 2026)

### Installation Steps

1. **Extract this ZIP** to any folder
2. **Right-click `Install.bat`** → **Run as administrator**
3. **Restart Civil 3D** (if it was open)

That's it! The installer will:
- ✅ Check for Python
- ✅ Install required Python packages (whitebox, rasterio, geopandas, etc.)
- ✅ Copy the plugin to Civil 3D's ApplicationPlugins folder

---

## Usage

### Command: `CATCHMENTAUTO`

1. Type `CATCHMENTAUTO` in Civil 3D and press Enter
2. Select a **TIN Surface**
3. Select a **Pipe Network**
4. Check which **structure types** to use as inlets
5. Click **Run Delineation**

The tool will:
- Export your surface as a DEM
- Find all inlet structures
- Run WhiteboxTools watershed analysis
- Create catchment areas linked to each inlet

### Analysis Settings (Optional)

Expand "Analysis Settings" to fine-tune:

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

### First run is slow
- WhiteboxTools downloads its binaries on first use (~50MB)
- Subsequent runs will be much faster

### Catchments look wrong
- Try decreasing DEM Cell Size (e.g., 0.5 ft)
- Enable "Burn Pipes" option
- Check that your surface has good coverage

---

## Uninstall

Right-click `Uninstall.bat` → Run as administrator

---

## Files Included

```
CatchmentTool/
├── Install.bat          ← Double-click to install
├── Uninstall.bat        ← Double-click to remove
├── README.md            ← This file
└── CatchmentTool.bundle/
    ├── PackageContents.xml
    └── Contents/
        ├── CatchmentTool.dll
        ├── Newtonsoft.Json.dll
        ├── Data/
        └── Python/
            ├── catchment_delineation.py
            └── requirements.txt
```

---

## Support

For issues or feature requests, contact your IT administrator or the tool developer.
