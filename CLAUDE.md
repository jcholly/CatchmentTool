# CatchmentTool (v2) — Project Guide

Civil 3D catchment delineation plugin. NETLOAD-able; two commands (`DELINEATE`, `MAKECATCHMENTS`).

## Architecture

- **`CSharp/Core/`** — pure .NET algorithm library, **no Civil 3D dependencies**. The 8-phase pipeline lives here. Cross-platform; can run against LandXML files outside of Civil 3D for testing/tuning.
- **`CSharp/Commands/`** — `[CommandMethod]`-decorated entry points (one per Civil 3D command).
- **`CSharp/UI/`** — WPF dialogs.
- **`CSharp/Services/`** — Civil 3D adapter layer (Catchment object creation, drawing-units detection).
- **`CSharp/CatchmentTool.csproj`** — `net8.0-windows`, references AutoCAD/Civil 3D 2026 assemblies via `Civil3DPath` MSBuild property.

## Pipeline (8 phases)

1. **Boundary** — buffered convex hull of selected structures, clipped to TIN convex hull (Sutherland-Hodgman).
2. **Rasterize** — TIN → square-cell grid at `cell_size` resolution.
3. **Pipe-burn (2b)** — lower rasterized surface along pipe alignments (Saunders 1999 / Lindsay 2016).
4. **Condition** — protect pond cells, snap inlets to local minima, breach + fill depressions (Lindsay-style priority-queue least-cost path).
5. **D8 routing** — steepest descent per cell with min-slope threshold; **edge off-site cells excluded**.
6. **Voronoi fallback** — multi-source Dijkstra from labeled cells with downhill bias; respects off-site flag.
7. **Pipe-network upstream union** — for each selected pond, BFS upstream and merge inlet labels into the pond's label.
8. **Polygonize + smooth** — 3×3 majority filter on labeled raster (topology-aware smoothing → polygons tile by construction), then marching-squares boundary trace, RDP simplify, 2 Chaikin iterations unconditionally.

## Tuning (offline)

Run `tools/CatchmentTool2.Tuner` against `test_data/landxml/*.xml`. The tuner does adaptive random sampling over 14 axes with multi-seed restart and a composite-v2 grading function. Winning params get baked into `CSharp/Services/TunedDefaults.cs`.

**Composite v2 grading** (sum = 1.0):

- Flow correctness: 36% (Monte-Carlo D8 trace from samples → walk pipe graph to terminus)
- Coverage / gap (linear ramp 2%→10%): 23%
- Outlet match (count × centroid distance): 17%
- Sump containment: 7%
- Area conservation: 6%
- Overlap (linear ramp 0%→3%): 5%
- Flow length sanity (shape elongation): 3%
- Vertex density (3–8 / 100 ft): 3%
- Slivers, micros, smoothness: **0% each** (vestigial GIS metrics that pushed the optimizer toward unrealistically coarse cells; computed and reported but no longer weighted).

## Tuned parameters (current production)

See [`CSharp/Services/TunedDefaults.cs`](CSharp/Services/TunedDefaults.cs).

```
cell_size                  = 13.2 ft
inlet_snap_radius          = 6.04 cells
inlet_snap_depth           = 0.30 ft
depression_handling        = Fill
max_breach_depth           = 2.79 ft
pond_capture_radius        = 3.74 cells
pipe_burn_depth            = 1.5 ft
routing                    = D8
min_slope_threshold        = 0.001
fallback_metric            = DownhillWeighted
downhill_bias_weight       = 0.59
min_catchment_area         = 191 ft²
rdp_tolerance × cell       = 0.22
chaikin_iterations         = 0  (Phase 7 forces +2 unconditionally)
```

## Testing

`tools/CatchmentTool2.Tuner` (and the `CatchmentTool2.Cli`) operate on LandXML files outside Civil 3D — useful for development and CI.

`test_data/landxml/` holds 7 real Civil 3D project exports + 10 synthetic fixtures (varied topology: linear, branch, grid, star, multi-pond). Per-fixture scoring + PNG rendering on each tuner run.

Real-fixture mean: ~72/100. Synthetic mean: ~88/100. The 16-point gap quantifies how much "clean TINs" matter — synthetic fixtures don't capture real-world TIN noise.

## Known limitations

- The unsupervised score ceiling for small developed sites is ~75–86% area agreement vs hand-drawn (Sanzana 2017, Petroselli 2012, Lindsay & Dhun 2015). v2 is at the upper end of this band; further gains require ground-truth references for IoU scoring or a manual-edit UI.
- `MAKECATCHMENTS` is the manual-edit path that production tools (PCSWMM, InfoDrainage, OpenFlows) ship with. We keep it intentionally.
- Routing is grid-D8 + min-slope threshold. D-infinity (snap-to-D8) is implemented but consistently underperforms D8 on these fixtures — kept as a tunable but defaulted to D8.
- The flow-path grader walks downstream until terminus or selected-structure hit; does not model lateral overflow at sag inlets.

## v1 archive

The v1 architecture (3 commands, WhiteboxTools via Python, TinWalker + BasinHierarchy priority-flood) is on `master~1` and back. To revisit:

```
git log --oneline master
git show <pre-v2-rewrite-sha>:CSharp/Services/TinWalker.cs
```

v1 Python prototypes are on branch `wip/python-prototypes-2026-04-26`.
