"""Generate 10 synthetic LandXML fixtures matching the structure of test_data/landxml/*.xml.

Each fixture has:
  - One Imperial-units Surface with a TIN (regular grid of vertices, split into triangles).
  - One PipeNetwork with structures (each with <Center> and <Invert> children) and pipes.
  - Z-elevations driven by a per-fixture pattern (sloped plane + parabolic depressions
    near ponds + low-amplitude noise).

Output: test_data/landxml/synth_NN.xml
"""
from __future__ import annotations

import math
import os
import random
from dataclasses import dataclass

OUT_DIR = "/Users/jchollingsworth/Documents/CatchmentTool2/test_data/landxml"
NS = "http://www.landxml.org/schema/LandXML-1.1"


@dataclass
class FixtureSpec:
    name: str
    width: float           # site width (ft)
    height: float          # site height (ft)
    grid_nx: int           # TIN columns
    grid_ny: int           # TIN rows
    base_elev: float       # baseline elevation
    slope_x: float         # slope in X direction (ft/ft)
    slope_y: float         # slope in Y direction (ft/ft)
    noise_amp: float       # random noise amplitude (ft)
    network_kind: str      # "linear" | "branch" | "grid" | "star" | "multi_pond"
    n_inlets: int
    n_ponds: int
    seed: int


SPECS: list[FixtureSpec] = [
    # 1 — small parking lot, simple linear network, gentle slope
    FixtureSpec("synth_01_lot",         400, 300,  20, 15, 100, 0.020, 0.005, 0.1, "linear",     6, 1, 101),
    # 2 — subdivision, branching network, moderate slope
    FixtureSpec("synth_02_subdivision", 800, 600,  32, 24,  90, 0.015, 0.012, 0.2, "branch",    14, 1, 102),
    # 3 — long thin strip (linear road)
    FixtureSpec("synth_03_strip",      1200, 200,  60, 12, 110, 0.025, 0.000, 0.1, "linear",     8, 1, 103),
    # 4 — flat commercial pad, grid-style network
    FixtureSpec("synth_04_flat_pad",    500, 500,  25, 25, 100, 0.002, 0.002, 0.05, "grid",     20, 1, 104),
    # 5 — multi-pond cluster
    FixtureSpec("synth_05_multi_pond",  900, 700,  36, 28,  95, 0.010, 0.015, 0.15, "multi_pond", 16, 3, 105),
    # 6 — steep terrain, branching
    FixtureSpec("synth_06_steep",       600, 500,  24, 20, 120, 0.060, 0.040, 0.3, "branch",    12, 1, 106),
    # 7 — large site, many inlets
    FixtureSpec("synth_07_large",      1400, 1000, 56, 40,  85, 0.012, 0.010, 0.2, "branch",    32, 1, 107),
    # 8 — star network (one pond, all inlets feed it directly)
    FixtureSpec("synth_08_star",        500, 500,  25, 25, 100, 0.015, 0.015, 0.1, "star",      10, 1, 108),
    # 9 — two parallel networks, two ponds
    FixtureSpec("synth_09_dual",        800, 500,  32, 20,  95, 0.010, 0.020, 0.15, "multi_pond", 12, 2, 109),
    # 10 — irregular shape proxy: small site, dense structures
    FixtureSpec("synth_10_dense",       400, 400,  30, 30, 100, 0.020, 0.010, 0.15, "branch",    20, 2, 110),
]


def gen_tin(spec: FixtureSpec, ponds: list[tuple[float, float]], origin_x: float, origin_y: float):
    """Returns (vertices, triangles) where vertices is list[(x,y,z)] and triangles is list[(i1,i2,i3)] (1-based)."""
    rng = random.Random(spec.seed)
    nx, ny = spec.grid_nx, spec.grid_ny
    dx = spec.width / (nx - 1)
    dy = spec.height / (ny - 1)
    verts = []
    for j in range(ny):
        for i in range(nx):
            x = origin_x + i * dx
            y = origin_y + j * dy
            # base sloping plane
            z = spec.base_elev - spec.slope_x * (i * dx) - spec.slope_y * (j * dy)
            # parabolic depression near each pond (drops by up to 4 ft within 50 ft radius)
            for px, py in ponds:
                d2 = (x - px) ** 2 + (y - py) ** 2
                r = 80.0
                if d2 < r * r:
                    drop = 4.0 * (1 - d2 / (r * r))
                    z -= drop
            # gentle noise
            z += (rng.random() - 0.5) * 2 * spec.noise_amp
            verts.append((x, y, z))
    # Triangulate each grid cell into two triangles.
    tris = []
    for j in range(ny - 1):
        for i in range(nx - 1):
            a = j * nx + i
            b = j * nx + (i + 1)
            c = (j + 1) * nx + i
            d = (j + 1) * nx + (i + 1)
            # 1-based ids
            tris.append((a + 1, b + 1, d + 1))
            tris.append((a + 1, d + 1, c + 1))
    return verts, tris


def gen_network(spec: FixtureSpec, origin_x: float, origin_y: float):
    """Returns (structures, pipes, ponds_xy)."""
    rng = random.Random(spec.seed + 1)
    structures = []  # list of (name, x, y, rim_elev, sump_elev)
    pipes = []       # list of (name, start_name, end_name)

    cx = origin_x + spec.width / 2
    cy = origin_y + spec.height / 2

    if spec.network_kind == "linear":
        # All structures along a sloping line; last one is the pond
        n = spec.n_inlets + 1
        ponds = []
        line_y = origin_y + spec.height * 0.5
        for i in range(n):
            x = origin_x + spec.width * (0.1 + 0.8 * i / (n - 1))
            y = line_y + (rng.random() - 0.5) * 20
            rim = spec.base_elev - spec.slope_x * (x - origin_x) + 1.0
            sump = rim - 4.0
            structures.append((f"S{i+1}", x, y, rim, sump))
        ponds.append((structures[-1][1], structures[-1][2]))
        for i in range(n - 1):
            pipes.append((f"P{i+1}", structures[i][0], structures[i+1][0]))

    elif spec.network_kind == "branch":
        # Tree: pond at center-bottom, branches feeding it
        ponds = [(cx, origin_y + spec.height * 0.1)]
        structures.append(("P_OUT", ponds[0][0], ponds[0][1], spec.base_elev - 2, spec.base_elev - 6))
        # Place inlets in a loose tree
        inlets_per_branch = max(1, spec.n_inlets // 3)
        branches = 3
        for b in range(branches):
            angle = math.pi / 2 + (b - 1) * 0.7
            prev = "P_OUT"
            for k in range(inlets_per_branch):
                r = (k + 1) * spec.width * 0.18
                jitter = (rng.random() - 0.5) * 30
                x = cx + r * math.cos(angle) + jitter
                y = ponds[0][1] + r * math.sin(angle) + jitter
                # clamp inside site
                x = max(origin_x + 20, min(origin_x + spec.width - 20, x))
                y = max(origin_y + 20, min(origin_y + spec.height - 20, y))
                name = f"I{b}_{k}"
                rim = spec.base_elev - spec.slope_x * (x - origin_x) - spec.slope_y * (y - origin_y) + 1.0
                sump = rim - 4.0
                structures.append((name, x, y, rim, sump))
                pipes.append((f"P_{name}", name, prev))
                prev = name

    elif spec.network_kind == "grid":
        # Lattice: pond at one corner, inlets on a grid
        ponds = [(origin_x + spec.width * 0.95, origin_y + spec.height * 0.05)]
        structures.append(("P_OUT", ponds[0][0], ponds[0][1], spec.base_elev - 2, spec.base_elev - 6))
        n = int(math.sqrt(spec.n_inlets))
        names = []
        for j in range(n):
            for i in range(n):
                x = origin_x + spec.width * (0.1 + 0.7 * i / max(1, n - 1))
                y = origin_y + spec.height * (0.2 + 0.7 * j / max(1, n - 1))
                name = f"G{i}_{j}"
                rim = spec.base_elev - spec.slope_x * (x - origin_x) - spec.slope_y * (y - origin_y) + 1.0
                sump = rim - 4.0
                structures.append((name, x, y, rim, sump))
                names.append((name, i, j))
        # Each inlet drains to its right neighbor; rightmost drains to its bottom neighbor; corner drains to pond
        for name, i, j in names:
            if i < n - 1:
                pipes.append((f"P_{name}", name, f"G{i+1}_{j}"))
            elif j > 0:
                pipes.append((f"P_{name}", name, f"G{i}_{j-1}"))
            else:
                pipes.append((f"P_{name}", name, "P_OUT"))

    elif spec.network_kind == "star":
        # All inlets pipe directly to single central pond
        ponds = [(cx, cy)]
        structures.append(("P_OUT", cx, cy, spec.base_elev + 2, spec.base_elev - 4))
        for k in range(spec.n_inlets):
            angle = 2 * math.pi * k / spec.n_inlets
            r = min(spec.width, spec.height) * 0.35
            x = cx + r * math.cos(angle)
            y = cy + r * math.sin(angle)
            name = f"I{k+1}"
            rim = spec.base_elev - spec.slope_x * (x - origin_x) - spec.slope_y * (y - origin_y) + 1.0
            sump = rim - 4.0
            structures.append((name, x, y, rim, sump))
            pipes.append((f"P_{name}", name, "P_OUT"))

    elif spec.network_kind == "multi_pond":
        ponds = []
        for k in range(spec.n_ponds):
            px = origin_x + spec.width * (0.2 + 0.6 * k / max(1, spec.n_ponds - 1))
            py = origin_y + spec.height * 0.15
            ponds.append((px, py))
            structures.append((f"P_OUT_{k}", px, py, spec.base_elev - 2, spec.base_elev - 6))
        # Distribute inlets across pond regions
        inlets_per_pond = max(1, spec.n_inlets // spec.n_ponds)
        for k, (px, py) in enumerate(ponds):
            prev = f"P_OUT_{k}"
            for m in range(inlets_per_pond):
                r = (m + 1) * spec.height * 0.18
                angle = math.pi / 2 + (rng.random() - 0.5) * 0.4
                x = px + r * math.cos(angle) + (rng.random() - 0.5) * 20
                y = py + r * math.sin(angle)
                x = max(origin_x + 20, min(origin_x + spec.width - 20, x))
                y = max(origin_y + 20, min(origin_y + spec.height - 20, y))
                name = f"P{k}_I{m}"
                rim = spec.base_elev - spec.slope_x * (x - origin_x) - spec.slope_y * (y - origin_y) + 1.0
                sump = rim - 4.0
                structures.append((name, x, y, rim, sump))
                pipes.append((f"P_{name}", name, prev))
                prev = name

    else:
        raise ValueError(f"unknown network kind: {spec.network_kind}")

    return structures, pipes, ponds


def write_landxml(spec: FixtureSpec):
    rng_origin = random.Random(spec.seed + 999)
    origin_x = 20000 + rng_origin.random() * 5000
    origin_y = 20000 + rng_origin.random() * 5000

    structures, pipes, ponds = gen_network(spec, origin_x, origin_y)
    verts, tris = gen_tin(spec, ponds, origin_x, origin_y)

    lines = []
    lines.append('<?xml version="1.0" encoding="UTF-8" standalone="no"?>')
    lines.append(f'<LandXML xmlns="{NS}" date="2026-04-26" time="12:00:00" version="1.1" language="English" readOnly="false">')
    lines.append('  <Units>')
    lines.append('    <Imperial areaUnit="squareFoot" linearUnit="foot" volumeUnit="cubicYard" temperatureUnit="fahrenheit" pressureUnit="inchHG" diameterUnit="inch" angularUnit="decimal degrees" directionUnit="decimal degrees"/>')
    lines.append('  </Units>')
    lines.append(f'  <Project name="synthetic/{spec.name}.dwg"/>')
    lines.append('  <Application name="CatchmentTool2 Synthetic Generator" desc="" manufacturer="" version="1.0" timeStamp="2026-04-26T12:00:00"/>')
    # Surfaces
    lines.append('  <Surfaces>')
    lines.append(f'    <Surface name="Surface Proposed">')
    lines.append('      <Definition surfType="TIN">')
    lines.append('        <Pnts>')
    for i, (x, y, z) in enumerate(verts, start=1):
        # LandXML order: Y X Z
        lines.append(f'          <P id="{i}">{y:.6f} {x:.6f} {z:.6f}</P>')
    lines.append('        </Pnts>')
    lines.append('        <Faces>')
    for a, b, c in tris:
        lines.append(f'          <F>{a} {b} {c}</F>')
    lines.append('        </Faces>')
    lines.append('      </Definition>')
    lines.append('    </Surface>')
    lines.append('  </Surfaces>')
    # PipeNetworks
    lines.append('  <PipeNetworks>')
    lines.append(f'    <PipeNetwork name="Network - {spec.name}" pipeNetType="storm" desc="">')
    lines.append('      <Structs>')
    # Build map name -> list of (refPipe, elev, flowDir) inverts
    inverts_by_struct: dict[str, list[tuple[str, float, str]]] = {s[0]: [] for s in structures}
    struct_by_name = {s[0]: s for s in structures}
    for pipe_name, start_name, end_name in pipes:
        if start_name in struct_by_name:
            sump = struct_by_name[start_name][4]
            inverts_by_struct[start_name].append((pipe_name, sump + 0.5, "out"))
        if end_name in struct_by_name:
            sump = struct_by_name[end_name][4]
            inverts_by_struct[end_name].append((pipe_name, sump + 0.2, "in"))
    for name, x, y, rim, sump in structures:
        lines.append(f'        <Struct name="{name}" desc="synthetic" elevRim="{rim:.4f}" elevSump="{sump:.4f}">')
        # LandXML order in <Center>: Y X
        lines.append(f'          <Center>{y:.6f} {x:.6f}</Center>')
        lines.append('          <CircStruct diameter="48." thickness="0.42"/>')
        for refPipe, elev, flowDir in inverts_by_struct.get(name, []):
            lines.append(f'          <Invert elev="{elev:.4f}" flowDir="{flowDir}" refPipe="{refPipe}"/>')
        lines.append('        </Struct>')
        # close-tag style sometimes used but we keep proper closure
    lines.append('      </Structs>')
    lines.append('      <Pipes>')
    for pipe_name, start_name, end_name in pipes:
        if start_name not in struct_by_name or end_name not in struct_by_name:
            continue
        sx = struct_by_name[start_name][1]; sy = struct_by_name[start_name][2]
        ex = struct_by_name[end_name][1]; ey = struct_by_name[end_name][2]
        length = math.hypot(sx - ex, sy - ey)
        slope = 0.01
        lines.append(f'        <Pipe name="{pipe_name}" refStart="{start_name}" refEnd="{end_name}" desc="synthetic 18in conc" length="{length:.4f}" slope="{slope:.4f}">')
        lines.append('          <CircPipe diameter="18." thickness="0.42"/>')
        lines.append('        </Pipe>')
    lines.append('      </Pipes>')
    lines.append('    </PipeNetwork>')
    lines.append('  </PipeNetworks>')
    lines.append('</LandXML>')

    out_path = os.path.join(OUT_DIR, f"{spec.name}.xml")
    with open(out_path, "w") as f:
        f.write("\n".join(lines))
    print(f"  {out_path}: {len(verts)} verts, {len(tris)} tris, {len(structures)} structs ({sum(1 for s in structures if s[0].startswith('P_OUT') or 'P_OUT' in s[0])} ponds), {len(pipes)} pipes")


def main():
    print(f"Writing {len(SPECS)} synthetic LandXML fixtures to {OUT_DIR}")
    for spec in SPECS:
        write_landxml(spec)
    print("done.")


if __name__ == "__main__":
    main()
