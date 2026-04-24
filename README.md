# Civil 3D Catchment Delineation Tool

Automated catchment delineation using WhiteboxTools integrated with AutoCAD Civil 3D 2026.

## Overview

Three commands cover the full storm drainage catchment workflow:

| Command | Description |
|---------|-------------|
| `CATCHMENTTIN` | **TIN-based delineation** — traces steepest descent directly on TIN surface (no rasterization); fast and accurate for small to medium catchments; creates Civil 3D Catchment objects for selected inlet structures |
| `CATCHMENTAUTO` | **Automated delineation** — exports TIN surface + inlet structures, runs WhiteboxTools hydrological analysis, and imports Civil 3D Catchment objects linked to their outlet structures; for complex hydrology or large-scale watersheds |
| `MAKECATCHMENTS` | **Linework to Catchments** — select closed polylines already in the drawing; assigns the pipe network structure found inside each polygon as the outlet and creates Civil 3D Catchment objects |

## Typical Workflows

### Workflow A — TIN-based delineation (fast, direct)
1. Type `CATCHMENTTIN`
2. Select TIN surface and pipe network in the dialog
3. Select which inlet structures to include as outlets (checkboxes)
4. Adjust grid cell size and snap tolerance if needed (defaults: 1 ft, 5 ft)
5. Click **Run** — delineation traces directly on TIN surface (~10 seconds)
6. Civil 3D Catchment objects are created, each linked to its inlet structure
7. **Best for:** small to medium sites with a single TIN surface and designed drainage

#### How CATCHMENTTIN resolves stuck drops (spillover routing)

Every grid cell is a virtual water drop that walks steepest descent on the TIN. When a drop cannot physically reach an inlet (flat triangle, micro-sink, off-surface), `CATCHMENTTIN` no longer falls back to nearest-inlet guessing. Instead, a **priority-flood from the inlets** (Barnes, Lehman & Mulla 2014) precomputes, for every cell on the surface, the inlet that rising water would spill toward. Stuck drops are resolved via this hierarchy — topologically correct, independent of distance.

The dialog log reports a resolution breakdown so you can judge the answer's quality:

```
Resolved to inlets: 248,113
  Direct (walker arrived)   : 231,440  (93.3%)
  Spillover (rising water)  :  15,889  ( 6.4%)
  Off-surface               :     612
  Orphan (no inlet reach)   :     172
```

A high spillover share (>30%) or non-zero orphan count indicates micro-sinks, missing inlets, or the need for pipe burning — planned in a later phase.

### Workflow B — Automated with WhiteboxTools (complex hydrology)
1. Type `CATCHMENTAUTO`
2. Select surface and pipe network in the dialog
3. Configure advanced settings (cell size, snap distance, breach options, pipe burning)
4. Click **Run** — delineation runs automatically via WhiteboxTools (~1-5 minutes)
5. Civil 3D Catchment objects are created, each linked to its inlet structure
6. **Best for:** large sites, complex drainage patterns, or when WhiteboxTools analysis is needed

### Workflow C — From existing linework
1. Draw or import closed polylines representing your catchment boundaries
2. Type `MAKECATCHMENTS`
3. Select the polylines, then select the reference surface
4. The tool finds the pipe network structure inside each polygon and creates Civil 3D Catchment objects with outlets assigned
5. **Best for:** manual refinement of automated boundaries or importing external catchment definitions

## Project Structure

```
CatchmentTool/
├── CSharp/                          # Civil 3D .NET Plugin
│   ├── Commands/
│   │   ├── CatchmentCommands.cs     # CATCHMENTAUTO + MAKECATCHMENTS
│   │   └── TinCatchmentCommand.cs   # CATCHMENTTIN
│   ├── Services/
│   │   ├── TinWalker.cs             # Steepest descent tracer on TIN
│   │   ├── BasinHierarchy.cs        # Priority-flood spillover routing
│   │   ├── WatershedGrid.cs         # Regular grid + flow tracing
│   │   ├── SurfaceExporter.cs       # TIN surface → DEM raster
│   │   ├── StructureExtractor.cs    # Pipe network → inlet points
│   │   ├── CatchmentCreator.cs      # Polygons → Civil 3D Catchments
│   │   └── PythonEnvironment.cs     # Python discovery + execution
│   └── UI/
│       ├── TinCatchmentDialog.xaml  # CATCHMENTTIN dialog
│       ├── TinCatchmentDialog.xaml.cs
│       ├── AutoCatchmentDialog.xaml # CATCHMENTAUTO dialog
│       └── AutoCatchmentDialog.xaml.cs
├── Python/                          # WhiteboxTools processing
│   ├── catchment_delineation.py     # Main delineation pipeline
│   ├── crs_utils.py                 # CRS resolution
│   ├── validation.py                # Pour point validation
│   └── requirements.txt
├── Distribution/                    # Packaging and installation
│   └── CatchmentTool.bundle/        # AutoCAD ApplicationPlugins bundle
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
# Build the C# plugin (needs Civil 3D 2026 installed)
dotnet build CSharp/CatchmentTool.csproj -c Release

# Copy the bundle to ApplicationPlugins
Copy-Item -Recurse -Force Distribution\CatchmentTool.bundle `
    "$env:ProgramData\Autodesk\ApplicationPlugins\"
```

See [INSTALL.md](INSTALL.md) for release-ZIP install, Python setup, and troubleshooting.

## License

Internal tool — Autodesk Water Infrastructure.
