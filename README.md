# Civil 3D Catchment Delineation Tool

Automated catchment delineation using WhiteboxTools integrated with AutoCAD Civil 3D 2026.

## Overview

This tool automates the watershed delineation workflow for Civil 3D storm drainage design:

1. Exports a Civil 3D TIN surface to a high-resolution DEM raster
2. Extracts pipe network inlet structure locations as pour points
3. Runs hydrologically-correct catchment delineation via WhiteboxTools (breach depressions, D8 flow direction, flow accumulation, watershed delineation)
4. Creates Civil 3D Catchment objects linked to their corresponding inlet structures

## Commands

| Command | Description |
|---------|-------------|
| `AUTOCATCHMENTS` | **One-step** automated delineation (export, process, import) |
| `CATCHMENTAUTO` | Open the automated catchment delineation dialog (WPF UI) |
| `CATCHMENTEXPORT` / `CATCHMENTTOOL` | Export dialog for surface and inlet data |
| `CATCHMENTIMPORT` | Import dialog for catchment polygons |
| `EXPORTCATCHMENTDATA` | CLI-style export of surface DEM and inlet structures |
| `IMPORTCATCHMENTS` | CLI-style import of catchment polygons |
| `LINKCATCHMENTS` | Link existing catchments to outlet structures by naming convention |
| `LISTPIPESTRUCTURES` | List all structures in a pipe network |

## Project Structure

```
CatchmentTool/
├── CSharp/                          # Civil 3D .NET Plugin
│   ├── CatchmentTool.csproj
│   ├── CatchmentTool.sln
│   ├── Commands/
│   │   └── CatchmentCommands.cs     # All command definitions
│   ├── Services/
│   │   ├── SurfaceExporter.cs       # TIN surface → DEM raster
│   │   ├── StructureExtractor.cs    # Pipe network → inlet points
│   │   └── CatchmentCreator.cs      # Polygons → Civil 3D Catchments
│   └── UI/
│       ├── AutoCatchmentDialog.xaml  # One-click delineation dialog
│       ├── CatchmentDialog.xaml
│       ├── ExportDialog.xaml
│       └── ImportDialog.xaml
├── Python/                          # WhiteboxTools processing
│   ├── catchment_delineation.py     # Main delineation pipeline
│   ├── dem_utils.py                 # DEM conversion utilities
│   ├── run_delineation.py           # CLI entry point
│   ├── config_template.json
│   └── requirements.txt
├── Distribution/                    # Packaging and installation
│   ├── CatchmentTool.bundle/        # AutoCAD ApplicationPlugins bundle
│   ├── Install-CatchmentTool.ps1
│   ├── Install.bat / Uninstall.bat
│   └── README.md
├── Data/                            # Working directory for intermediate files
├── Build-Distribution.ps1           # Build + package script
└── INSTALL.md                       # Detailed installation guide
```

## Requirements

### Civil 3D
- AutoCAD Civil 3D 2026
- .NET 8.0 (included with Civil 3D 2026)

### Python
- Python 3.10+
- whitebox, rasterio, geopandas, shapely, numpy

## Quick Start

```powershell
# 1. Install Python dependencies
pip install -r Python/requirements.txt

# 2. Build the C# plugin
dotnet build CSharp/CatchmentTool.csproj -c Release

# 3. In Civil 3D: NETLOAD → browse to CSharp/bin/CatchmentTool.dll
# 4. In Civil 3D: type AUTOCATCHMENTS
```

For detailed installation (including auto-load setup), see [INSTALL.md](INSTALL.md).

## License

Internal tool — Autodesk Water Infrastructure.
