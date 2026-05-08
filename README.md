# CatchmentTool2

Civil 3D catchment delineation tool — pure C# pipeline (no Python, no
WhiteboxTools). Site-scale (parking lots, subdivisions, small commercial:
fractions of an acre up to ~100 ac).

Three ways to use it:

1. **Civil 3D plugin** — `CT2_DELINEATE` command, emits native Civil 3D
   `Catchment` objects.
2. **CLI** — headless runner against a LandXML export. Outputs GeoJSON +
   PNG visualization + grade JSON.
3. **Tuner** — sweeps parameter ranges across a fixture set and reports the
   winning configuration.

For a deep dive on methodology, scoring, and parameter sensitivity, see
[CLAUDE.md](CLAUDE.md).

---

## Build

Requires .NET 8 SDK (or newer; the repo currently builds on .NET 10).

```bash
# Core (cross-platform), CLI, Tuner — all build on macOS/Linux/Windows
dotnet build src/CatchmentTool2.Core/CatchmentTool2.Core.csproj -c Release
dotnet build src/CatchmentTool2.Cli/CatchmentTool2.Cli.csproj -c Release
dotnet build tools/CatchmentTool2.Tuner/CatchmentTool2.Tuner.csproj -c Release

# Civil 3D plugin — Windows only, requires Civil 3D 2024+ installed
dotnet build src/CatchmentTool2.Cad/CatchmentTool2.Cad.csproj -c Release
```

The Cad project targets `net8.0-windows` and references Autodesk libraries
that ship with Civil 3D — it will not build on macOS/Linux.

---

## Civil 3D plugin

After building on Windows, load the resulting DLL into Civil 3D:

```
Command: NETLOAD
File: src\CatchmentTool2.Cad\bin\Release\net8.0-windows\CatchmentTool2.Cad.dll
```

Then run:

```
Command: CT2_DELINEATE
```

Pick the TIN surface, pick the pipe network, optionally select pour-point
structures (if none selected, the auto-classifier marks any structure with
no downstream pipe as a pond). The pipeline runs in-process and emits one
Civil 3D `Catchment` object per pour point, linked back to its outlet
structure via `ReferencePipeNetworkStructureId`.

---

## CLI

Run the pipeline against a LandXML file without needing Civil 3D:

```bash
dotnet run --project src/CatchmentTool2.Cli -c Release -- \
    <input.xml> <output_dir> [param=value ...]
```

Example:

```bash
dotnet run --project src/CatchmentTool2.Cli -c Release -- \
    test_data/landxml/synth_01_lot.xml runs/manual \
    cellsize=2 inletsnap=4 depression=Hybrid
```

Outputs in `<output_dir>`:

- `<name>.png` — catchment map (semi-transparent fills, pipes in gray, ponds
  as blue dots, inlets as red dots, site boundary as dark outline)
- `<name>.catchments.geojson` — polygons + properties
- `<name>.grade.json` — Tier 1 + Tier 2 metric breakdown

### Override keys

| Key          | Maps to                      | Notes                                      |
| ------------ | ---------------------------- | ------------------------------------------ |
| `cellsize`   | `CellSize`                   | ft                                         |
| `inletsnap`  | `InletSnapRadiusCells`       | cells                                      |
| `snapdepth`  | `InletSnapDepth`             | ft                                         |
| `depression` | `DepressionHandling`         | `Fill` / `Breach` / `HybridBreachThenFill` |
| `maxbreach`  | `MaxBreachDepth`             | ft                                         |
| `pondradius` | `PondCaptureRadiusCells`     | cells                                      |
| `routing`    | `RoutingAlgorithm`           | `D8` / `DInf`                              |
| `fallback`   | `FallbackMetric`             | `NearestEuclidean` / `DownhillWeighted`    |
| `bias`       | `DownhillBiasWeight`         |                                            |
| `rdp`        | `RdpToleranceCellMultiplier` | × cell size                                |
| `chaikin`    | `ChaikinIterations`          | 0–5                                        |
| `minarea`    | `MinCatchmentArea`           | ft²                                        |
| `minslope`   | `MinSlopeThreshold`          |                                            |
| `burn`       | `PipeBurnDepth`              | ft                                         |

---

## Tuner

The tuner runs the pipeline against every LandXML fixture under
`test_data/landxml/`, averages per-fixture composite scores, and narrows
the parameter grid each round until convergence.

```bash
dotnet run --project tools/CatchmentTool2.Tuner -c Release
```

Optional positional args: `[fixturesDir] [runsDir] [maxRounds]
[sizeLimitBytes] [convergenceDelta] [searchFlowSamples] [samplesPerRound]
[numSeeds] [finalFlowSamples]`. Defaults are sensible (4000 samples × 3
seeds × up to 8 rounds, 5 MB fixture size limit).

Output ends up at `runs/<YYYYMMDD-HHmmss>/`:

```
runs/<timestamp>/
├── scores.csv         # every (round, combo, fixture) result
├── report.html        # final report with embedded PNGs
├── report.md          # markdown version
└── final/             # per-fixture catchment maps + GeoJSON for the winning combo
```

The current parameter ranges are calibrated against the literature
(Zhang & Montgomery 1994, Vaze et al. 2010, Lindsay 2016, Tarboton 1997,
USGS StreamStats urban, Springer 2024 on pipe burning). See [CLAUDE.md](CLAUDE.md)
under "Tuning axes (active)" for the active ranges.

---

## Test data

`test_data/landxml/` is **not in the repo** — `*.xml` is gitignored. The
fixtures are real Civil 3D project exports kept local only.

### Adding your own fixtures

Drop a LandXML file with at least one `<Surface>` and one `<PipeNetwork>`
into `test_data/landxml/`. The reader auto-classifies pond/inlet by pipe
topology — any structure with no downstream pipe is a pond. No extra
config required.

If you want a synthetic fixture (no real-site data), use
`generate_synthetic_landxml.py` to produce one of the 10 site archetypes
(parking lot, subdivision, strip, flat pad, multi-pond, etc.).

If your file is over the tuner's 5 MB size limit, raise
`sizeLimitBytes` on the command line, or run it through the CLI manually.

---

## Project layout

```
src/
├── CatchmentTool2.Core/    # pipeline, grader, LandXML reader, output writers (.NET 8)
├── CatchmentTool2.Cad/     # Civil 3D NETLOAD plugin (.NET 8, Windows-only)
└── CatchmentTool2.Cli/     # headless pipeline runner

tools/
└── CatchmentTool2.Tuner/   # adaptive parameter sweep

test_data/landxml/          # LandXML fixtures (gitignored — supply your own)
runs/                       # tuner output (gitignored)
```

---

## License

(Project license TBD.)
