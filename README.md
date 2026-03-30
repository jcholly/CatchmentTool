# Civil 3D Catchment Delineation Tool

Automated catchment delineation using WhiteboxTools integrated with AutoCAD Civil 3D 2026.

## Overview

Two commands cover the full storm drainage catchment workflow:

| Command | Description |
|---------|-------------|
| `CATCHMENTAUTO` | **Automated delineation** — exports TIN surface + inlet structures, runs WhiteboxTools hydrological analysis, and imports Civil 3D Catchment objects linked to their outlet structures |
| `MAKECATCHMENTS` | **Linework to Catchments** — select closed polylines already in the drawing; assigns the pipe network structure found inside each polygon as the outlet and creates Civil 3D Catchment objects |

## Typical Workflows

### Workflow A — Automated (no existing linework)
1. Type `CATCHMENTAUTO`
2. Select surface and pipe network in the dialog
3. Click **Run** — delineation runs automatically via WhiteboxTools
4. Civil 3D Catchment objects are created, each linked to its inlet structure

### Workflow B — From existing linework
1. Draw or import closed polylines representing your catchment boundaries
2. Type `MAKECATCHMENTS`
3. Select the polylines, then select the reference surface
4. The tool finds the pipe network structure inside each polygon and creates Civil 3D Catchment objects with outlets assigned

## Project Structure

```
CatchmentTool/
├── CSharp/                          # Civil 3D .NET Plugin
│   ├── Commands/
│   │   └── CatchmentCommands.cs     # CATCHMENTAUTO + MAKECATCHMENTS
│   ├── Services/
│   │   ├── SurfaceExporter.cs       # TIN surface → DEM raster
│   │   ├── StructureExtractor.cs    # Pipe network → inlet points
│   │   ├── CatchmentCreator.cs      # Polygons → Civil 3D Catchments
│   │   └── PythonEnvironment.cs     # Python discovery + execution
│   └── UI/
│       └── AutoCatchmentDialog.xaml # CATCHMENTAUTO dialog
├── Python/                          # WhiteboxTools processing
│   ├── catchment_delineation.py     # Main delineation pipeline
│   ├── crs_utils.py                 # CRS resolution
│   ├── validation.py                # Pour point validation
│   └── requirements.txt
├── Distribution/                    # Packaging and installation
│   └── CatchmentTool.bundle/        # AutoCAD ApplicationPlugins bundle
├── Build-Distribution.ps1           # Build + install script
└── INSTALL.md
```

## Requirements

**Civil 3D:** AutoCAD Civil 3D 2026, .NET 8.0

**Python** (for `CATCHMENTAUTO` only): Python 3.10+
```
pip install -r Python/requirements.txt
```

## Installation

```powershell
# Build and install to ApplicationPlugins
.\Build-Distribution.ps1 -Install

# Python-only update (no Civil 3D restart needed)
.\Build-Distribution.ps1 -Install -SkipBuild
```

See [INSTALL.md](INSTALL.md) for detailed setup.

## License

Internal tool — Autodesk Water Infrastructure.
