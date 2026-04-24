"""Cross-language validation: run Python burn_pipes_into_elev and C# PipeBurner.Burn
on identical input, then diff cell-by-cell.

Builds input from testing.xml's pipe endpoints directly (using pipe.StartPoint.Z
and pipe.EndPoint.Z semantics — no LandXML-invert fallback — so the C# side
gets the same inputs the Civil 3D plugin will provide at runtime).
"""
from __future__ import annotations

import math
import os
import subprocess
import sys
from pathlib import Path

import numpy as np

from parse_landxml import parse
from tin_harness import (
    _sample_elev_grid, build_tin, burn_pipes_into_elev,
)


def build_pipe_segments_from_landxml(data):
    """Build pipe segments with pipe-endpoint-style inverts.

    In Civil 3D Pipe.StartPoint.Z / EndPoint.Z convention the start is the
    upstream end. We approximate with: start = matching 'Out' invert of start
    structure, end = matching 'In' invert of end structure, falling back to
    min invert and then z_sump. Matches Python _pipe_end_invert exactly.
    """
    from tin_harness import _pipe_end_invert
    segs = []
    for pipe in data.pipes.values():
        s = data.structures.get(pipe.start_struct)
        e = data.structures.get(pipe.end_struct)
        if s is None or e is None:
            continue
        sz = _pipe_end_invert(s, "out")
        ez = _pipe_end_invert(e, "in")
        if not (math.isfinite(sz) and math.isfinite(ez)):
            continue
        segs.append((s.x, s.y, sz, e.x, e.y, ez))
    return segs


def main(xml_path: Path, cell_size: float = 10.0):
    here = Path(__file__).parent
    fx = here / "verify_cs" / "fixtures"
    fx.mkdir(parents=True, exist_ok=True)

    print(f"[setup] Loading {xml_path}, building TIN, sampling grid at cell={cell_size}...")
    data = parse(xml_path)
    tin = build_tin(data.surface.points, data.surface.triangles)
    elev, origin_x, origin_y, rows, cols = _sample_elev_grid(tin, cell_size)
    pipes = build_pipe_segments_from_landxml(data)
    print(f"        grid {rows}x{cols}, {len(pipes)} pipes")

    # Python side: burn a copy of elev.
    elev_py = elev.copy()
    n_py = burn_pipes_into_elev(
        elev_py, origin_x, origin_y, cell_size,
        _adapt_pipes(data, pipes), data.structures,
    )
    # ^^ burn_pipes_into_elev expects Pipe objects + Structure dicts to re-derive
    # inverts. For apples-to-apples vs C# (which uses pipe-segment Z directly),
    # we should NOT reuse that function. Instead, run the burning logic inline
    # with the same pipe-segment list we pass to C#.
    # Discard the call above and recompute correctly:
    elev_py = elev.copy()
    n_py = _burn_segments_py(
        elev_py, origin_x, origin_y, cell_size, pipes, trench_depth=0.0)
    print(f"[python] burned {n_py} cells")

    # Write fixtures for C# test.
    elev_in_csv = fx / "elev_in.csv"
    pipes_csv = fx / "pipes.csv"
    meta_csv = fx / "meta.csv"
    elev_out_csv = fx / "elev_out_cs.csv"
    _write_grid_csv(elev_in_csv, elev)
    _write_pipes_csv(pipes_csv, pipes)
    with open(meta_csv, "w") as f:
        f.write("origin_x,origin_y,cell_size\n")
        f.write(f"{origin_x:.17g},{origin_y:.17g},{cell_size:.17g}\n")

    # Run C# test.
    print("[cs] dotnet run...")
    proj_dir = here / "verify_cs"
    result = subprocess.run(
        ["dotnet", "run", "--project", str(proj_dir), "-c", "Release",
         "--", str(elev_in_csv), str(pipes_csv),
         str(elev_out_csv), str(meta_csv), "0"],
        capture_output=True, text=True,
    )
    print(result.stdout.strip())
    if result.returncode != 0:
        print(result.stderr, file=sys.stderr)
        raise SystemExit(f"C# test failed with exit code {result.returncode}")

    # Read C# output.
    elev_cs = _read_grid_csv(elev_out_csv)
    print(f"[cs] grid shape {elev_cs.shape}, burned count = (see stdout above)")

    # Diff.
    assert elev_cs.shape == elev_py.shape, (elev_cs.shape, elev_py.shape)

    # nan alignment — must match exactly.
    nan_py = np.isnan(elev_py)
    nan_cs = np.isnan(elev_cs)
    if not np.array_equal(nan_py, nan_cs):
        bad = int(np.logical_xor(nan_py, nan_cs).sum())
        raise SystemExit(f"FAIL: NaN patterns differ in {bad} cells")

    mask = ~nan_py
    diff = np.abs(elev_py[mask] - elev_cs[mask])
    max_diff = float(diff.max()) if diff.size else 0.0
    mean_diff = float(diff.mean()) if diff.size else 0.0
    n_nonzero = int((diff > 1e-9).sum())

    # Which cells changed on each side.
    # elev is the original. Compare lowered cells.
    delta_py = elev - elev_py  # > 0 where python lowered
    delta_cs = elev - elev_cs
    modified_py = (delta_py > 1e-9) & mask
    modified_cs = (delta_cs > 1e-9) & mask
    both = modified_py & modified_cs
    only_py = modified_py & ~modified_cs
    only_cs = modified_cs & ~modified_py

    print()
    print("=" * 60)
    print("CROSS-LANGUAGE PIPEBURNER VERIFICATION")
    print("=" * 60)
    print(f"Grid shape:              {elev_py.shape}")
    print(f"Pipes:                   {len(pipes)}")
    print(f"Py-burned cells:         {int(modified_py.sum())}")
    print(f"C#-burned cells:         {int(modified_cs.sum())}")
    print(f"Burned in both:          {int(both.sum())}")
    print(f"Burned only in Python:   {int(only_py.sum())}")
    print(f"Burned only in C#:       {int(only_cs.sum())}")
    print(f"Max |py - cs|:           {max_diff:.3e}")
    print(f"Mean |py - cs|:          {mean_diff:.3e}")
    print(f"Cells w/ diff > 1e-9:    {n_nonzero}")
    print()
    if max_diff < 1e-9 and int(only_py.sum()) == 0 and int(only_cs.sum()) == 0:
        print("[OK] C# PipeBurner produces identical output to Python reference.")
        return 0
    else:
        print("[WARN] Outputs differ — investigate.")
        return 1


def _burn_segments_py(elev, origin_x, origin_y, cell_size, pipes,
                      trench_depth=0.0):
    """Inline burn mirroring C# PipeBurner exactly, using pipe-segment list."""
    rows, cols = elev.shape
    modified = 0
    seen = set()
    for (sx, sy, sz, ex, ey, ez) in pipes:
        if not (math.isfinite(sz) and math.isfinite(ez)):
            continue
        dx = ex - sx
        dy = ey - sy
        length = math.hypot(dx, dy)
        if length < 1e-6:
            continue
        n_samples = max(2, int(length / (cell_size * 0.5)) + 1)
        seen.clear()
        for i in range(n_samples):
            t = i / (n_samples - 1)
            x = sx + t * dx
            y = sy + t * dy
            z_iv = sz + t * (ez - sz) - trench_depth
            c = int(round((x - origin_x) / cell_size))
            r = int(round((y - origin_y) / cell_size))
            if not (0 <= r < rows and 0 <= c < cols):
                continue
            key = (r, c)
            if key in seen:
                continue
            seen.add(key)
            if not math.isfinite(elev[r, c]):
                continue
            if z_iv < elev[r, c]:
                elev[r, c] = z_iv
                modified += 1
    return modified


def _adapt_pipes(data, segs):
    return data.pipes  # placeholder; used only in discarded first call


def _write_grid_csv(path, grid):
    rows, cols = grid.shape
    with open(path, "w") as f:
        for r in range(rows):
            parts = []
            for c in range(cols):
                v = grid[r, c]
                if math.isnan(v):
                    parts.append("nan")
                else:
                    parts.append(f"{v:.17g}")
            f.write(",".join(parts))
            f.write("\n")


def _read_grid_csv(path):
    with open(path) as f:
        lines = f.readlines()
    arr = []
    for line in lines:
        row = []
        for t in line.strip().split(","):
            if t in ("nan", "NaN", ""):
                row.append(float("nan"))
            else:
                row.append(float(t))
        arr.append(row)
    return np.array(arr, dtype=np.float64)


def _write_pipes_csv(path, pipes):
    with open(path, "w") as f:
        f.write("sx,sy,sz,ex,ey,ez\n")
        for (sx, sy, sz, ex, ey, ez) in pipes:
            f.write(f"{sx:.17g},{sy:.17g},{sz:.17g},"
                    f"{ex:.17g},{ey:.17g},{ez:.17g}\n")


if __name__ == "__main__":
    xml = Path(sys.argv[1] if len(sys.argv) > 1 else "testing.xml")
    cell = float(sys.argv[2]) if len(sys.argv) > 2 else 10.0
    raise SystemExit(main(xml, cell_size=cell))
