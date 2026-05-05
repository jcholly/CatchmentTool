# CatchmentTool2

Civil 3D catchment delineation tool. Distributed as a NETLOAD plugin (`CatchmentTool2.Cad.dll`).

## Architecture

- **`src/CatchmentTool2.Core`** — pure .NET algorithm library (no Civil 3D deps). Contains the 8-phase pipeline, LandXML reader, GeoJSON + PNG writers, grading harness. Cross-platform.
- **`src/CatchmentTool2.Cad`** — Civil 3D NETLOAD plugin. References Core + AutoCAD/Civil 3D APIs. Targets `net8.0-windows`. Exposes the `CT2_DELINEATE` command. Build on Windows with Civil 3D installed.
- **`src/CatchmentTool2.Cli`** — CLI for running the pipeline against a LandXML file (used for testing without Civil 3D).
- **`tools/CatchmentTool2.Tuner`** — adaptive grid-search tuner. Runs the pipeline against all fixtures in `test_data/landxml/`, scores each, and narrows the parameter grid until convergence.

## Methodology

The pipeline runs in 8 phases; only the top-10 parameters are exposed for tuning (see "Tuning Parameters" below).

1. **Boundary derivation** — buffer the convex hull of selected structures, or use the TIN hull / a user polygon.
2. **Rasterize** — sample the TIN to a square grid at the chosen `cell_size`. Cells outside the TIN are NaN.
3. **Conditioning** — protect pond cells from depression handling, snap inlets to local minima, then breach/fill remaining sinks per the configured strategy.
4. **D8 routing** — each cell traces steepest-descent until it reaches a labeled structure cell or runs off the data area.
5. **Voronoi fallback** — multi-source Dijkstra from each labeled cell, with optional downhill bias, fills any cell D8 didn't reach.
6. **Pipe-network union** — for each user-selected pond, walk the pipe graph upstream and union the labels of all upstream inlets into the pond's label.
7. **Polygonize + smooth** — boundary-trace each label set, run RDP simplify then Chaikin smoothing under an area-drift guard.
8. **Emit** — in Civil 3D, call `Catchment.Create(name, styleId, surfaceId, polyline)` per polygon and link `ReferencePipeNetworkStructureId`. In the CLI, write GeoJSON + PNG.

**Pond identification**: in v1, the LandXML reader auto-classifies any structure with no downstream pipe as a pond and marks it `UserSelected = true`. The Civil 3D plugin uses the same rule by default; the user can override by selecting structures explicitly.

## Results Criteria

Site-scale targets (parking lots, subdivisions, small commercial — fractions of an acre up to ~100 ac). The tool may estimate/approximate; the bar is "good engineering output", not pixel-perfect.

### Tier 1 — Core correctness (must pass)

1. **One catchment per selected outlet** — `count(catchments) == count(selected inlets/structures)`, 1:1 join, each outlet point lies inside or on its catchment polygon. **Score:** % of outlets with a valid catchment (target 100%).

2. **Native Civil 3D Catchment objects** — output is actual Civil 3D Catchment objects, not generic polylines. **Score:** binary pass/fail.

3. **Coverage / minimal gaps** — `(site_boundary − union(catchments)) / site_boundary < 5%` of the contributing area. **Score:** gap fraction (lower is better; this is the most important quality bar).

4. **Minimal overlaps** — pairwise intersect of catchment polygons ≤ 2% of mean catchment area. **Score:** total overlap area / total catchment area.

5. **Flow-path correctness** — sample N random points inside each catchment, run steepest-descent on the TIN, count how many terminate at the assigned outlet. **Score:** % of sample points reaching correct outlet (target ≥ 80% per catchment, ≥ 90% averaged across the site). *This is the strongest single indicator the boundaries actually match the hydrology.*

### Tier 2 — Geometric sanity (catches obvious failures)

6. **No slivers** — thinness ratio `T = 4πA/P² > 0.05` for every catchment polygon. **Score:** count of catchments failing.

7. **No micro-polygon artifacts** — no catchment < 100 ft² unless it's intentionally a roof drain or similar. **Score:** count of sub-threshold polygons.

8. **Smooth, presentable boundaries** — boundaries are smoothed (no raw TIN-edge stairstepping). **Score:** qualitative visual review, or smoothness via mean turning angle between consecutive segments.

9. **Sump / closed-basin handling** — designated terminal sumps form closed catchments (no flow trace exits). Minor terrain pits below threshold are bridged, not treated as outlets. **Score:** qualitative review of sump catchments; flag any that leak.

### Tier 3 — Performance

10. **Runtime** — < 30 s for ≤ 50 inlets, < 3 min for ≤ 200 inlets on a typical workstation. **Score:** wall-clock time.

### Optional — Regression baseline (only if a manual reference exists)

11. **Area % error vs. manual** — mean `|auto − ref| / ref < 5%`, max < 15%.
12. **Boundary IoU vs. manual** — per-catchment Intersection-over-Union ≥ 0.75.

## Tuning Parameters

The pipeline exposes ~35 knobs across 8 phases, but only ~10 meaningfully move the scoring metrics above. These are the parameters to expose for grid-search tuning. Defaults are starting points, not final answers — they should be re-tuned against the regression baseline (Tier 3) once a representative test set exists.

For each parameter: range, default, the scoring criteria it primarily affects, and key citations.

### 1. `cell_size` (Phase 2 — raster sampling)
- **Range:** 0.3–3 ft. **Default:** 1 ft.
- **Affects:** flow-path correctness (#5), area accuracy (#11), runtime (#10, quadratic), boundary smoothness (#8 — finer = jaggier raw output).
- **Why high impact:** Single most-studied DEM parameter; dominant control on accuracy. Zhang & Montgomery (1994) and Vaze et al. (2010) document 5–15% area error growth as resolution coarsens. Below ~2 ft, curb lines and minor swales get captured; above, they don't.
- **Couples with:** `inlet_snap_radius` (snap should be ≥ 1–2 cells), `rdp_tolerance` (tolerance scales with cell size), `max_breach_depth`.

### 2. `inlet_snap_radius` (Phase 3 — conditioning)
- **Range:** 1–10 cells (1.5–10 ft at 1 ft resolution). **Default:** 3 cells.
- **Affects:** coverage (#3 — catastrophic if wrong; unsnapped inlet → empty catchment), area accuracy (#11), flow-path correctness (#5).
- **Why high impact:** ArcGIS `Snap Pour Point` docs flag this as the most error-prone parameter. Lindsay & Seibert (2009) showed catchment area can vary by orders of magnitude with snap distances differing by a few cells. TauDEM `MoveOutletsToStreams` defaults to ~50 cells (basin-scale, not site-scale).
- **Couples with:** `cell_size`, `pond_capture_radius`.

### 3. `depression_handling` (Phase 3)
- **Range:** `{fill, breach, hybrid_breach_then_fill}`. **Default:** `hybrid_breach_then_fill`.
- **Affects:** flow-path correctness (#5), sump handling (#9), ridge placement between adjacent catchments.
- **Why high impact:** Lindsay (2016) "Efficient hybrid breaching-filling" is the canonical reference. Pure fill creates artificial flats that propagate flow-direction errors; pure breach can carve through legitimate berms. Hybrid is the de-facto modern default (WhiteboxTools `BreachDepressionsLeastCost`).
- **Couples with:** `max_breach_depth`, `flat_epsilon`.

### 4. `max_breach_depth` (Phase 3)
- **Range:** 0.3–3 ft, or `unlimited`. **Default:** 1 ft.
- **Affects:** sump handling (#9 — controls whether pond berms and curbs are respected or carved through), flow-path correctness (#5).
- **Why high impact:** On developed sites with engineered berms, this is the parameter that decides whether the tool respects built features. Whitebox default is unlimited; recommended cap for developed sites is 1–2 ft.
- **Couples with:** `pond_seed_method` (a properly seeded pond shouldn't need breaching).

### 5. `pond_capture_radius` (Phase 3)
- **Range:** 1–20× cell size. **Default:** 3× cell size.
- **Affects:** coverage (#3), sump handling (#9), area accuracy (#11) for pond catchments.
- **Why high impact:** Methodology is pond-centric — misidentifying a pond breaks its entire catchment. Determines whether the pond seed grabs the actual depression bottom.
- **Couples with:** `depression_handling`, `inlet_snap_radius`.

### 6. `routing_algorithm` (Phase 4)
- **Range:** `{D8, D-inf, MFD}`. **Default:** `D8`.
- **Affects:** flow-path correctness (#5), boundary jaggedness (#8 — D8 produces staircase artifacts).
- **Why moderate impact:** Tarboton (1997) introduced D-infinity for planar surfaces (i.e., developed sites). D-inf is better for flow accumulation rasters but worse for crisp polygon boundaries — D8 gives clean discrete boundaries, which is what we want. Honest take: for boundary delineation with pipe-network BFS dominating, this matters less than it does in basin-scale hydrology. Keep tunable but don't expect huge swings.
- **Couples with:** `cell_size`, smoothing parameters.

### 7. `rdp_tolerance` (Phase 7 — smoothing)
- **Range:** 0.5–3× cell size. **Default:** 1× cell size.
- **Affects:** smoothness (#8 — primary knob), area accuracy (#11), area drift (`max_area_drift_pct` is the safety guard).
- **Why high impact:** Single biggest knob for the smoothness criterion. Cartographic rule-of-thumb (Töpfer's radical law, ESRI Generalize Polygons) suggests ~0.5–2× cell size for hydrologic polygons. Too low = D8 staircase persists; too high = catchment drifts off the actual ridge.
- **Couples with:** `cell_size` (tightly), `chaikin_iterations`.

### 8. `chaikin_iterations` (Phase 7)
- **Range:** 0–5. **Default:** 2.
- **Affects:** smoothness (#8 — primary), small area drift (~0–2% per iteration).
- **Why moderate impact:** Standard finishing pass after RDP. 2–3 iterations is the practical sweet spot — beyond that, no perceptual gain plus area loss. Always apply *after* RDP.
- **Couples with:** `rdp_tolerance`, `max_area_drift_pct`.

### 9. `min_catchment_area` (Phase 5 — fallback)
- **Range:** 0–500 ft² (or 0.1–5% of site area). **Default:** 100 ft² (matches Tier 2 #7).
- **Affects:** no-slivers (#6), no-micro-polygons (#7), count match to ground truth, coverage (#3).
- **Why high impact:** Single threshold deciding whether a sliver becomes its own catchment or gets merged into a neighbor. On small sites, this often determines whether output matches a hand-drawn map. Hand-drawn maps always merge slivers; raster pipelines don't unless told to.
- **Couples with:** `fallback_metric`, `unselected_intermediate_structure_policy`.

### 10. `fallback_metric` (Phase 5)
- **Range:** `{nearest_euclidean, downhill_weighted, drop_tail_influenced}`. **Default:** `downhill_weighted`.
- **Affects:** area accuracy on flats (#11), flow-path correctness (#5) on flats, coverage (#3).
- **Why high impact:** On flat areas, this swings 10–20% of area between adjacent catchments. SWMM defaults to nearest (Thiessen); modern tools use cost-distance with elevation. The `drop_tail_influenced` variant is the geometric implementation of the polyline-info intuition — uses where flow paths *almost* terminate, not just start points.
- **Couples with:** `cell_size`, `min_catchment_area`.

### Recommended grid-search shape

For a first defensible Pareto sweep, grid 5 axes × 3 values = 243 runs:

| Axis | Values |
|---|---|
| `cell_size` | 0.5, 1, 2 ft |
| `inlet_snap_radius` | 2, 3, 5 cells |
| `depression_handling` | fill, breach, hybrid |
| `rdp_tolerance` | 0.5×, 1×, 2× cell size |
| `min_catchment_area` | 25, 100, 250 ft² |

Hold the other 5 (algorithm=D8, max_breach_depth=1 ft, pond_capture_radius=3× cell, chaikin=2, fallback=downhill_weighted) at modern defaults. This covers ~80% of the expected score variance per the cited sensitivity studies; the remaining 5 are second-stage refinements once stage-1 identifies a winning region.

### Parameters intentionally excluded from tuning

These were considered and rejected as low-signal: `flat_epsilon`, `water_drop_density`, `sampling_method`, `target_cell_count`, `boundary_extraction`, `hole_threshold_area`, `style_id`, `naming_template`, `regenerate_flow_paths`, `cycle_policy`. Either categorical-and-fixed-by-context (boundary, naming) or in the noise vs. higher-leverage knobs (sampling method vs. cell size).

### Citations

Zhang & Montgomery 1994 (*Water Resources Research*); Vaze, Teng & Spencer 2010 (*Environ. Modelling & Software*); Lindsay 2016 (*Hydrological Processes*, hybrid breach-fill); Lindsay & Seibert 2009; Tarboton 1997 (D-infinity); Buttenfield & McMaster 1991 (cartographic generalization); ArcGIS Pro `SnapPourPoint` docs; TauDEM `MoveOutletsToStreams` docs; WhiteboxTools manual (`BreachDepressionsLeastCost`); EPA SWMM Reference Manual Vol. I.

## Testing

### Fixtures

`test_data/landxml/` holds 8 real LandXML exports of past Civil 3D projects. Each contains a TIN surface and a pipe network (structures + pipes). The cleanup script `clean_landxml.py` was already run to keep one surface and one network per file.

Fixture sizes vary from ~400 KB to ~47 MB. The tuner respects a `--size-limit` flag (default 5 MB) so the largest one (`testing5.xml`) is skipped during tuning to keep round time bounded; it can be enabled by raising the limit.

### Pipeline runner (CLI)

```bash
dotnet run --project src/CatchmentTool2.Cli -c Release -- <input.xml> <output_dir> [k=v ...]
```

Per-parameter overrides use `name=value` pairs (e.g. `cellsize=2 inletsnap=4 depression=Fill`). Outputs: `<name>.png` (catchment map), `<name>.catchments.geojson`, `<name>.grade.json`.

### Grading harness

`Grader.Grade(...)` implements every Tier 1 + Tier 2 criterion in this CLAUDE.md:

**Composite v2** (replaces v1 — slivers/micros/smoothness dropped per research showing they were vestigial GIS metrics that pushed the optimizer toward unrealistically coarse cells; outlet split into count + centroid; gap and overlap softened to linear ramps; new metrics added):

| Component | Metric | Weight |
|---|---|---|
| Flow correctness | Monte-Carlo D8 trace from samples, walk pipe graph to terminus, % matching assigned outlet | **0.36** |
| Coverage / gap | Linear ramp 100 at 2% → 0 at 10% gap (vs hard cliff in v1) | 0.23 |
| Outlet match | 0.5 × count_match + 0.5 × centroid_score (centroid: 1 − distance/equivalent_diameter) | 0.17 |
| Sump containment | % pond samples whose D8 trace stays inside polygon | 0.07 |
| Overlap | Linear ramp 100 at 0% → 0 at 3% overlap | 0.05 |
| Area conservation | `\|Σcatch − boundary\| / boundary`; linear ramp 2% → 12% | 0.06 |
| Flow length sanity | bbox_diagonal / √area; 100 below 5, 0 above 12 (catches pathological elongation) | 0.03 |
| Vertex density | 100 if 3–8 vertices per 100 ft perimeter, drops outside (engineer-style) | 0.03 |

Slivers (#6), micros (#7), smoothness (#8) are still computed and reported as flags in the per-fixture row but **carry 0 weight** in the composite — they were forcing the optimizer to pick 12 ft cells which produced unrealistically coarse catchments.

Composite weighted score is on a 0–100 scale. The grader also reports slivers (#6, thinness < 0.05), micro polygons (#7, area < 100 ft²), and runtime (#10) but they don't enter the composite by default — they're flags, not scores. Adjust the weighting in `Grader.cs` if your scoring criteria change.

### Adaptive tuner

```bash
dotnet run --project tools/CatchmentTool2.Tuner -c Release [-- fixturesDir runsDir maxRounds sizeLimitBytes convergenceDelta]
```

Defaults: `test_data/landxml`, `runs/`, 8 rounds, 5 MB size limit, 0.5 convergence delta.

**Algorithm:**
1. Load all fixtures under the size limit into memory once (LandXML parsing is expensive).
2. Round 1 grid: 3 values × 5 axes = 243 combinations × N fixtures runs. Axes (per the top-10 list): `cell_size`, `inlet_snap_radius`, `depression_handling`, `rdp_tolerance_multiplier`, `min_catchment_area`. Other parameters held at defaults.
3. For each combination, run the pipeline against every fixture and average the weighted score. Track the per-fixture grade in `scores.csv` for offline analysis.
4. After the round, narrow each axis to a 3-value grid centered on the round's winner (categorical axes lock to the winner). Halve the step size each round.
5. Stop when `roundBest − previousRoundBest < convergenceDelta`, or `maxRounds` is hit.
6. Run the winning parameter set against every fixture once more with `flowSamplesPerCatchment=30` (vs. 15 during search), render PNGs, and emit `report.html` + `report.md`.

**Output structure** (per run):

```
runs/<YYYYMMDD-HHMMSS>/
├── scores.csv           # every (round, combo, fixture) row
├── report.html          # final report w/ embedded PNGs
├── report.md            # same content, markdown
└── final/
    ├── testing1.png     # catchment map per fixture
    ├── testing1.geojson
    ├── testing2.png
    └── ...
```

The PNG renderer is hand-rolled (zero deps, pure managed C#). Each catchment is a semi-transparent fill from a 16-color palette with a darker outline; pipes are gray lines; ponds are large blue dots; inlets/junctions are small red dots; site boundary is a dark outline.

### Adding new fixtures

Drop a LandXML file (with at least one `<Surface>` and one `<PipeNetwork>`) into `test_data/landxml/`. The reader auto-classifies pond/inlet by pipe topology — no extra config needed. If the file is over the size limit, the tuner will skip it; raise `sizeLimitBytes` or run it through the CLI manually.

### Known limitations

- Routing is grid-based D8 or D-infinity-snap-to-D8 (Tarboton 1997). Water-drop variant is exposed in `TuningParameters` but not wired in.
- The flow-path grader walks downstream until it hits a selected structure or terminal sink. It does not model lateral overflow at sag inlets.
- Lindsay-style breach uses a priority-queue least-cost path (max breach-depth along path ≤ `max_breach_depth`), not the full Lindsay/Conrad 2016 algorithm.

### Tuning axes (active)

The tuner samples 13 parameters (8 continuous + 5 categorical):

| Continuous | Range |
|---|---|
| `cell_size` | [0.5, 15] ft |
| `inlet_snap_radius_cells` | [0.5, 10] |
| `inlet_snap_depth` | [0.05, 1.0] ft |
| `max_breach_depth` | [0.2, 4.0] ft |
| `pond_capture_radius_cells` | [1, 8] |
| `rdp_tolerance_multiplier` | [0.1, 3.0] |
| `min_catchment_area` | [10, 500] ft² |
| `downhill_bias_weight` | [0, 5] |
| `min_slope_threshold` | [0, 0.01] |

| Categorical | Options |
|---|---|
| `depression_handling` | {Fill, Breach, HybridBreachThenFill} |
| `routing_algorithm` | {D8, DInf} |
| `fallback_metric` | {NearestEuclidean, DownhillWeighted} |
| `chaikin_iterations` | {0, 1, 2, 3} |
