"""Render just the TIN shape — no catchment algorithm.

Two views:
  tin_raw_<tag>.png      solid fill of every cell where the TIN has elevation.
                          Tells us whether the TIN is actually shaped weird.
  tin_triangles_<tag>.png actual triangles drawn as thin lines — no grid.
                          Tells us whether grid sampling is causing artifacts.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection

from parse_landxml import parse
from tin_harness import _sample_elev_grid, build_tin


def main(xml_path: Path, cell_size: float = 10.0) -> None:
    tag = xml_path.stem
    out = xml_path.parent / "out"
    out.mkdir(exist_ok=True)

    print(f"Loading {xml_path}, building TIN...")
    data = parse(xml_path)
    tin = build_tin(data.surface.points, data.surface.triangles)
    print(f"  {len(data.surface.points):,} pts, "
          f"{len(data.surface.triangles):,} tris")

    # --- View 1: sampled elevation grid (what the algorithm sees) ---
    elev, ox, oy, rows, cols = _sample_elev_grid(tin, cell_size)
    inside = np.isfinite(elev)
    extent = (ox, ox + cols * cell_size, oy, oy + rows * cell_size)

    rgb = np.ones((rows, cols, 3), dtype=np.float32) * 0.97  # off-surface pale
    rgb[inside] = (0.2, 0.5, 0.8)  # inside TIN = blue

    fig, ax = plt.subplots(figsize=(12, 12))
    ax.imshow(rgb, origin="lower", extent=extent, interpolation="nearest")
    ax.set_title(f"Raw sampled grid (cell={cell_size:.0f} ft) — {tag}\n"
                 f"blue = inside TIN, gray = outside / TIN hole\n"
                 f"{int(inside.sum()):,} inside-TIN cells of {rows*cols:,} total",
                 fontsize=11)
    ax.set_aspect("equal")
    out1 = out / f"tin_raw_{tag}.png"
    fig.tight_layout()
    fig.savefig(out1, dpi=150)
    plt.close(fig)
    print(f"  Saved {out1.name}")

    # --- View 2: actual triangles drawn (what the TIN really is) ---
    print(f"  Drawing {len(tin.tri_verts):,} triangles (may take a moment)...")
    segments = []
    # Only draw the triangle EDGES — too many to fill.
    for tvi in range(len(tin.tri_verts)):
        a, b, c = tin.tri_verts[tvi]
        pa = tin.vertex_xyz[a, :2]
        pb = tin.vertex_xyz[b, :2]
        pc = tin.vertex_xyz[c, :2]
        segments.append([pa, pb])
        segments.append([pb, pc])
        segments.append([pc, pa])

    fig, ax = plt.subplots(figsize=(12, 12))
    lc = LineCollection(segments, colors=(0.15, 0.3, 0.55), linewidths=0.15,
                        alpha=0.7)
    ax.add_collection(lc)
    xs = tin.vertex_xyz[:, 0]
    ys = tin.vertex_xyz[:, 1]
    ax.set_xlim(xs.min() - 50, xs.max() + 50)
    ax.set_ylim(ys.min() - 50, ys.max() + 50)
    ax.set_title(f"Actual TIN triangles — {tag}\n"
                 f"{len(tin.tri_verts):,} triangles; holes = buildings / gaps",
                 fontsize=11)
    ax.set_aspect("equal")
    out2 = out / f"tin_triangles_{tag}.png"
    fig.tight_layout()
    fig.savefig(out2, dpi=150)
    plt.close(fig)
    print(f"  Saved {out2.name}")


if __name__ == "__main__":
    args = sys.argv[1:]
    cell = 10.0
    paths = []
    for a in args:
        if a.startswith("cell="):
            cell = float(a.split("=", 1)[1])
        else:
            paths.append(Path(a))
    if not paths:
        paths = [Path("testing.xml"), Path("testing2.xml")]
    for p in paths:
        if p.exists():
            main(p, cell_size=cell)
        else:
            print(f"skip {p} (missing)")
