"""Render the burned-hierarchy label grid as what Civil 3D will draw:
filled catchment polygons with black outlines and inlet labels.

Uses out/run_arrays.npz from the most recent run_test.py invocation, so
rerun run_test.py first if inputs changed.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection
from matplotlib.patches import Patch

from parse_landxml import parse


def main(arr_path: Path, xml_path: Path, out_path: Path,
         min_cells: int = 0, annotate: bool = True) -> None:
    data = np.load(arr_path)
    labels = data["labels_burned"]
    origin_x = float(data["origin_x"])
    origin_y = float(data["origin_y"])
    cell_size = float(data["cell_size"])
    rows, cols = labels.shape

    xml = parse(xml_path)
    inlets = []
    for i, (name, s) in enumerate(sorted(xml.structures.items())):
        inlets.append({"id": i, "name": name, "x": s.x, "y": s.y})
    id_to_name = {inl["id"]: inl["name"] for inl in inlets}

    # Optional: zero-out catchments smaller than min_cells to hide noise.
    if min_cells > 0:
        uniq, cnt = np.unique(labels[labels >= 0], return_counts=True)
        drop = {int(u) for u, c in zip(uniq, cnt) if c < min_cells}
        if drop:
            mask = np.isin(labels, list(drop))
            labels = labels.copy()
            labels[mask] = -1

    # Filled palette per inlet. Use a distinct color per id via HSV spread.
    unique_ids = sorted(int(v) for v in np.unique(labels) if v >= 0)
    n = len(unique_ids)
    hsv = np.stack(
        [np.linspace(0, 1, n, endpoint=False),
         np.full(n, 0.60),
         np.full(n, 0.85)],
        axis=1,
    )
    from matplotlib.colors import hsv_to_rgb
    palette_rgb = hsv_to_rgb(hsv)
    id_to_color = dict(zip(unique_ids, palette_rgb))

    rgb = np.ones((rows, cols, 3), dtype=np.float32)  # white background
    for iid, color in id_to_color.items():
        rgb[labels == iid] = color

    # Build black boundary segments where neighboring cells have different
    # labels (and where a labelled cell borders the grid edge).
    segments = []
    for r in range(rows):
        for c in range(cols):
            lab = labels[r, c]
            if lab < 0:
                continue
            x0 = origin_x + c * cell_size
            y0 = origin_y + r * cell_size
            x1 = x0 + cell_size
            y1 = y0 + cell_size
            # bottom edge
            if r == 0 or labels[r - 1, c] != lab:
                segments.append([(x0, y0), (x1, y0)])
            # top edge
            if r == rows - 1 or labels[r + 1, c] != lab:
                segments.append([(x0, y1), (x1, y1)])
            # left edge
            if c == 0 or labels[r, c - 1] != lab:
                segments.append([(x0, y0), (x0, y1)])
            # right edge
            if c == cols - 1 or labels[r, c + 1] != lab:
                segments.append([(x1, y0), (x1, y1)])

    extent = (
        origin_x, origin_x + cols * cell_size,
        origin_y, origin_y + rows * cell_size,
    )

    fig, ax = plt.subplots(figsize=(12, 12))
    ax.imshow(rgb, origin="lower", extent=extent, interpolation="nearest")
    lc = LineCollection(segments, colors="black", linewidths=0.6, alpha=0.85)
    ax.add_collection(lc)

    # Mark each inlet and label it with the short structure number.
    for inl in inlets:
        ax.plot(inl["x"], inl["y"], "o", markersize=4,
                markerfacecolor="white", markeredgecolor="black",
                markeredgewidth=0.8, zorder=5)
        if annotate:
            # Parse "Structure - (691) (InfoDrainage)" → "691"
            short = inl["name"]
            lpar = short.find("(")
            rpar = short.find(")", lpar)
            if lpar >= 0 and rpar > lpar:
                short = short[lpar + 1:rpar]
            ax.annotate(short, (inl["x"], inl["y"]),
                        xytext=(3, 3), textcoords="offset points",
                        fontsize=6.5, color="black",
                        bbox=dict(boxstyle="round,pad=0.15", fc="white",
                                  ec="none", alpha=0.75),
                        zorder=6)

    ax.set_title(
        f"Catchment polygons (cell={cell_size:.0f} ft, "
        f"pipe burning on) — {len(unique_ids)} catchments, "
        f"{len(inlets)} inlets",
        fontsize=11,
    )
    ax.set_xlabel("X")
    ax.set_ylabel("Y")
    ax.set_aspect("equal")
    fig.tight_layout()
    fig.savefig(out_path, dpi=160)
    plt.close(fig)
    print(f"Saved {out_path}")


if __name__ == "__main__":
    here = Path(__file__).parent
    arr = here / "out" / "run_arrays.npz"
    xml = here / (sys.argv[1] if len(sys.argv) > 1 else "testing.xml")
    out = here / "out" / "catchments.png"
    min_cells = int(sys.argv[2]) if len(sys.argv) > 2 else 0
    main(arr, xml, out, min_cells=min_cells)
