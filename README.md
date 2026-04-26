# Civil 3D Catchment Delineation Tool (v2)

Automated catchment delineation for small developed sites — fully integrated with Civil 3D as a NETLOAD plugin. Pick a TIN surface and a pipe network, check the inlets you want as pour points, and the tool emits one native Civil 3D `Catchment` object per checked structure.

## Commands

| Command          | What it does                                                                                                                                                                                                                                                 |
| ---------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `DELINEATE`      | **Tuned auto-delineation.** Opens a dialog: pick TIN, pick pipe network, check pour-point structures, click Run. The pipeline does its thing and emits Civil 3D `Catchment` objects, each linked to its outlet structure for downstream stormwater analysis. |
| `MAKECATCHMENTS` | **Linework → Catchment objects.** Select existing closed polylines you've drawn or imported. Each polyline becomes a Civil 3D `Catchment`, and any pipe-network structure inside it is auto-assigned as the outlet.                                          |

The plugin's algorithm parameters are baked in from offline tuning — there is no settings panel. To re-tune, run the offline tuner against new fixtures and update [`CSharp/Services/TunedDefaults.cs`](CSharp/Services/TunedDefaults.cs).

## How `DELINEATE` works

The pipeline runs in 8 phases, all in-process (no Python, no WhiteboxTools):

1. **Boundary** — buffered convex hull around selected structures, clipped to the TIN convex hull.
2. **Rasterize** — sample the TIN to a square grid (default 13.2 ft cells per tuning).
3. **Pipe-burn** — lower the rasterized surface 1.5 ft along each pipe alignment (Saunders 1999 / Lindsay 2016 hydro-conditioning). Source TIN is never modified.
4. **Condition** — protect pond cells, snap inlets to local minima, fill remaining depressions.
5. **D8 routing** — each cell traces steepest descent until it reaches a labeled inlet cell or hits the data boundary.
6. **Voronoi fallback** — multi-source Dijkstra from labeled cells fills cells D8 didn't reach. **Edge cells whose flow runs off the TIN boundary are excluded** (not force-assigned to a nearby inlet).
7. **Pipe-network union** — for each user-selected pond, walk the pipe graph upstream and union all upstream inlet cells into the pond's label.
8. **Polygonize + smooth** — apply a 3×3 majority filter to the labeled raster (topology-aware smoothing — adjacent polygons tile by construction, no gaps or overlaps), then marching-squares boundary trace, then RDP simplify, then 2 Chaikin iterations for plan presentability.

For each user-selected structure (one catchment per pour point per Tier 1 #1 of the result criteria), the tool calls `Catchment.Create(name, styleId, groupId, surfaceId, point3dCollection)` with Z draped from the TIN, and links the catchment to its outlet structure via `ReferencePipeNetworkStructureId`.

## Workflow

### `DELINEATE` — tuned auto-delineation

1. Type `DELINEATE` in Civil 3D.
2. Pick the TIN surface from the dropdown.
3. Pick the pipe network from the dropdown.
4. The structures table populates with checkboxes (every structure checked by default). Uncheck any you don't want as a pour point. **One catchment per checked structure.**
5. Click **Run Delineation**.
6. Civil 3D `Catchment` objects appear in a new Catchment Group named `CT_<HHmmss>`, each linked to its outlet structure.

### `MAKECATCHMENTS` — convert existing polylines

1. Draw or import closed polylines that represent your catchment boundaries.
2. Type `MAKECATCHMENTS` in Civil 3D.
3. Select the polylines.
4. Pick the reference surface.
5. The tool finds the structure inside each polyline (if any) and creates `Catchment` objects with that structure as the outlet.

## Project structure

```
CatchmentTool/
├── CSharp/
│   ├── CatchmentTool.csproj         # net8.0-windows, Civil 3D 2026
│   ├── Commands/
│   │   ├── DelineateCommand.cs      # [CommandMethod("DELINEATE")]
│   │   └── CatchmentCommands.cs     # [CommandMethod("MAKECATCHMENTS")]
│   ├── UI/
│   │   ├── DelineateDialog.xaml     # pour-point selection, no settings
│   │   └── DelineateDialog.xaml.cs
│   ├── Services/
│   │   ├── TunedDefaults.cs         # baked-in tuned parameters
│   │   ├── CatchmentEmitter.cs      # emit Civil 3D Catchment objects
│   │   ├── CatchmentCreator.cs      # used by MAKECATCHMENTS (legacy)
│   │   ├── StructureExtractor.cs    # used by MAKECATCHMENTS (legacy)
│   │   └── DrawingUnits.cs          # imperial / metric detection
│   └── Core/                        # pure algorithm (no Civil 3D deps)
│       ├── TuningParameters.cs
│       ├── Common.cs                # Vec2, Bounds
│       ├── Geometry/                # Polygon, RDP, Chaikin, Sutherland-Hodgman, MarchingSquares
│       ├── Surface/                 # Tin, Grid
│       ├── Network/                 # Structure, Pipe, PipeNetwork
│       ├── Pipeline/                # 8 phases
│       ├── LandXml/                 # offline LandXML reader
│       ├── Output/                  # GeoJSON + PNG writers
│       └── Grading/                 # composite v2 scoring (8 components)
├── Distribution/
│   └── CatchmentTool.bundle/        # AutoCAD ApplicationPlugins bundle
├── tools/
│   └── harness/                     # offline grading + tuning harness (Python prototypes)
├── README.md
├── INSTALL.md
└── CLAUDE.md
```

## Requirements

- **Civil 3D 2026** (or 2025 with .NET 8 enabled)
- **Windows 10/11** with .NET 8 runtime

No Python dependency in v2 — the WhiteboxTools-based pipeline was replaced by an in-process C# pipeline.

## Building

```powershell
# From the repo root, with Civil 3D 2026 installed at the default location:
dotnet build CSharp/CatchmentTool.csproj -c Release

# Custom Civil 3D path:
dotnet build CSharp/CatchmentTool.csproj -c Release -p:Civil3DPath="D:\Autodesk\AutoCAD 2026"
```

## Loading in Civil 3D

```
NETLOAD
```

Then select `CSharp/bin/CatchmentTool.dll`. Type `DELINEATE` or `MAKECATCHMENTS`.

For permanent install, see [INSTALL.md](INSTALL.md).

## Tuning

Algorithm parameters were tuned offline using random sampling × multi-seed × 17 fixtures with composite v2 grading. The winning configuration is hard-coded in [`CSharp/Services/TunedDefaults.cs`](CSharp/Services/TunedDefaults.cs).

To re-tune for a different project distribution: collect representative LandXML exports, drop them in `test_data/landxml/`, run the tuner, and update `TunedDefaults.V2` with the winning params.

## v1 → v2 changes

The v1 architecture had three commands (`CATCHMENTTIN`, `CATCHMENTAUTO`, `MAKECATCHMENTS`), used WhiteboxTools via Python for complex hydrology, and had a TIN-direct walker (`TinWalker`) plus priority-flood spillover (`BasinHierarchy`). v2 collapses to two commands (`DELINEATE`, `MAKECATCHMENTS`), removes the Python/WhiteboxTools dependency, and uses a single tuned pipeline that benchmarks better on real developed sites.

The v1 algorithm code is preserved in git history (`git checkout master~1`). The v1 Python prototypes are on the `wip/python-prototypes-2026-04-26` branch.
