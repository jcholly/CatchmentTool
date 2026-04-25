"""Ship-quality pipeline with the four fixes:
  1. Hull-vs-hole classification of off-surface cells.
  2. Orphan islands that are fully enclosed by holes -> nearest inlet.
     Orphan regions touching the hull stay unlabelled (drain off-site).
  3. Cell-size safety check.
  4. Per-connected-component catchments (drop fragments < min-size).

Outputs (under out/):
  ship_before_<tag>.png   current method (orphan cells shown as black gaps)
  ship_after_<tag>.png    new method (islands filled, hull rim excluded)
  ship_summary_<tag>.txt  key counts before/after
"""
from __future__ import annotations

import math
import sys
import time
from pathlib import Path

import numpy as np
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection

from parse_landxml import parse
from tin_harness import (
    Inlet, Resolution, _sample_elev_grid, _flood_from_inlets,
    burn_pipes_into_elev, build_tin, elevation_at_xy,
    classify_off_surface, resolve_orphan_islands,
    filter_small_components, component_count_per_inlet,
    exclude_cells_far_from_inlet, light_condition_grid,
    trace_d8,
)


CELL_SAFETY_LIMIT = 5_000_000  # beyond this, warn the user


def run(xml_path: Path, cell_size: float = 10.0,
        min_catchment_sqft: float = 500.0,
        max_cell_to_inlet_ft: float = 500.0) -> None:
    tag = xml_path.stem
    out = xml_path.parent / "out"
    out.mkdir(exist_ok=True)

    print(f"Loading {xml_path}, building TIN...")
    t0 = time.time()
    data = parse(xml_path)
    tin = build_tin(data.surface.points, data.surface.triangles)
    print(f"  TIN: {len(data.surface.points):,} pts, "
          f"{len(data.surface.triangles):,} tris  ({time.time()-t0:.1f}s)")

    # Cell-size safety check (fix 4).
    xmin, ymin, xmax, ymax = tin.bounds
    approx_cols = int((xmax - xmin) / cell_size) + 1
    approx_rows = int((ymax - ymin) / cell_size) + 1
    approx_cells = approx_rows * approx_cols
    if approx_cells > CELL_SAFETY_LIMIT:
        print(f"  WARNING: grid will be {approx_cells:,} cells at "
              f"cell_size={cell_size:.1f}. This may take a long time. "
              f"Consider a larger cell size.")

    # Inlets from structures.
    inlets = []
    for i, (name, s) in enumerate(sorted(data.structures.items())):
        z_surf, _ = elevation_at_xy(tin, s.x, s.y)
        z = z_surf if np.isfinite(z_surf) else s.z_rim
        inlets.append(Inlet(id=i, x=s.x, y=s.y, z=z, name=name))
    print(f"  Inlets: {len(inlets)}  "
          f"(pipes: {len(data.pipes)}, structures: {len(data.structures)})")

    # Sample grid, burn pipes, light-condition, flood.
    print("Sampling grid, burning pipes, light-conditioning, "
          "priority-flood...")
    t0 = time.time()
    elev, ox, oy, rows, cols = _sample_elev_grid(tin, cell_size)
    n_burn = burn_pipes_into_elev(elev, ox, oy, cell_size,
                                  data.pipes, data.structures)
    # Light conditioning: fill micro-sinks below 0.5 ft. Skip-near-inlet
    # uses 2 * snap tolerance (5 ft default -> 10 ft) so designed low
    # points stay sinks.
    elev, n_raised = light_condition_grid(
        elev, inlets, ox, oy, cell_size,
        max_fill_depth=0.5, skip_radius=10.0)
    # Walker pass on the conditioned grid (D8 steepest-descent). Cells
    # where the walker physically reaches an inlet are labelled "Direct";
    # the priority-flood spillover handles the rest as before.
    print(f"  Walker (D8 on conditioned grid)...", end=" ", flush=True)
    walker_labels = np.full((rows, cols), -1, dtype=np.int64)
    n_direct = 0
    n_walker_attempts = 0
    for r in range(rows):
        y = oy + r * cell_size
        for c in range(cols):
            if np.isnan(elev[r, c]):
                continue
            x = ox + c * cell_size
            n_walker_attempts += 1
            res = trace_d8(elev, ox, oy, cell_size, x, y, inlets,
                           snap_tol=5.0, max_steps=2000)
            if res.inlet_id >= 0:
                walker_labels[r, c] = res.inlet_id
                n_direct += 1
    walker_pct = (100.0 * n_direct / max(1, n_walker_attempts))
    print(f"{n_direct:,}/{n_walker_attempts:,} direct ({walker_pct:.1f}%)")

    labels = _flood_from_inlets(elev, inlets, ox, oy, cell_size)
    # Walker results overlay priority-flood: walker is more physically
    # accurate where it succeeds, so prefer it.
    walker_mask = walker_labels >= 0
    labels[walker_mask] = walker_labels[walker_mask]
    print(f"  Grid: {rows}x{cols} = {rows*cols:,} cells")
    print(f"  Pipe-burned cells: {n_burn}")
    print(f"  Light-conditioning raised {n_raised:,} cells "
          f"(<0.5 ft micro-sinks)")
    print(f"  Flood: {time.time()-t0:.1f}s")

    # --- STATISTICS: BEFORE ----
    inside = np.isfinite(elev)
    labels_before = labels.copy()
    stat_before = _stats("BEFORE", labels_before, inside)

    # --- FIX 1+2: hull/hole classification + orphan resolution ---
    outside_hull, interior_holes = classify_off_surface(elev)
    print(f"Off-surface breakdown: {int(outside_hull.sum()):,} outside hull, "
          f"{int(interior_holes.sum()):,} interior holes")
    n_cc, n_assigned, n_excluded = resolve_orphan_islands(
        labels, elev, inlets, outside_hull, ox, oy, cell_size)
    print(f"Orphan resolution: {n_cc} components, "
          f"{n_assigned:,} cells reassigned (interior islands), "
          f"{n_excluded:,} cells excluded (hull-touching)")

    # Distance cap is disabled by default — on small sites it over-prunes;
    # on large sites the user-reviewable "dominant inlet" share in the log
    # tells them if coverage is a problem. If a cap > 0 is supplied, apply.
    if max_cell_to_inlet_ft > 0:
        n_far = exclude_cells_far_from_inlet(
            labels, inlets, ox, oy, cell_size, max_cell_to_inlet_ft)
        print(f"Distance cap: {n_far:,} cells excluded "
              f"(> {max_cell_to_inlet_ft:.0f} ft from assigned inlet)")

    # --- FIX 3: merge tiny components into neighbors ---
    min_cells = max(1, int(min_catchment_sqft / (cell_size * cell_size)))
    kept, merged = filter_small_components(labels, min_cells)
    print(f"Component filter: kept {kept} catchments, "
          f"merged {merged} small fragments "
          f"(< {min_catchment_sqft:.0f} sqft = {min_cells} cells)")

    cc_counts = component_count_per_inlet(labels)
    total_catchments = sum(cc_counts.values())
    print(f"Per-inlet components: {len(cc_counts)} inlets, "
          f"{total_catchments} total catchments")

    # --- STATISTICS: AFTER ---
    stat_after = _stats("AFTER", labels, inside)

    # Write summary.
    with open(out / f"ship_summary_{tag}.txt", "w") as f:
        f.write(f"Site: {tag}  cell={cell_size}  min={min_catchment_sqft}sqft\n\n")
        f.write(stat_before + "\n")
        f.write(stat_after + "\n")
        f.write(f"\nOrphan resolution: {n_cc} CCs, "
                f"{n_assigned:,} reassigned, {n_excluded:,} excluded\n")
        f.write(f"Component filter: kept {kept}, merged {merged}\n")
        f.write(f"Final catchments: {total_catchments} "
                f"({len(cc_counts)} inlets)\n")

    # Render before/after PNGs.
    print("Rendering PNGs...")
    _render(labels_before, inlets, elev, outside_hull, interior_holes,
            ox, oy, cell_size, out / f"ship_before_{tag}.png",
            f"BEFORE (baseline method) — {tag}")
    _render(labels, inlets, elev, outside_hull, interior_holes,
            ox, oy, cell_size, out / f"ship_after_{tag}.png",
            f"AFTER (hull-aware + per-component) — {tag}")
    print(f"Saved PNGs under {out}\n")


def _stats(label: str, labels: np.ndarray, inside: np.ndarray) -> str:
    labelled = int(((labels >= 0) & inside).sum())
    orphan = int(((labels < 0) & inside).sum())
    total_inside = int(inside.sum())
    uniq, cnt = np.unique(labels[labels >= 0], return_counts=True)
    top = sorted(zip(cnt.tolist(), uniq.tolist()), reverse=True)[:1]
    dom_pct = 100.0 * top[0][0] / labelled if labelled and top else 0.0
    out = (f"[{label}] inside={total_inside:,}  labelled={labelled:,}  "
           f"orphan={orphan:,} ({100*orphan/total_inside:.2f}%)  "
           f"inlets={len(uniq)}  dominant={dom_pct:.1f}%")
    print(f"  {out}")
    return out


def _render(labels: np.ndarray, inlets, elev: np.ndarray,
            outside_hull: np.ndarray, interior_holes: np.ndarray,
            origin_x: float, origin_y: float, cell_size: float,
            out_path: Path, title: str) -> None:
    from matplotlib.colors import hsv_to_rgb

    rows, cols = labels.shape
    extent = (origin_x, origin_x + cols * cell_size,
              origin_y, origin_y + rows * cell_size)

    # Build per-cell RGB.
    rgb = np.ones((rows, cols, 3), dtype=np.float32)
    # Off-surface outside hull: very light gray.
    rgb[outside_hull] = (0.95, 0.95, 0.95)
    # Interior holes (buildings etc.): dark gray.
    rgb[interior_holes] = (0.25, 0.25, 0.25)

    unique_ids = sorted(int(v) for v in np.unique(labels) if v >= 0)
    n = len(unique_ids)
    if n > 0:
        hsv = np.stack(
            [np.linspace(0, 1, n, endpoint=False),
             np.full(n, 0.60),
             np.full(n, 0.85)],
            axis=1,
        )
        palette = hsv_to_rgb(hsv)
        for iid, color in zip(unique_ids, palette):
            rgb[labels == iid] = color

    # Cells that are inside TIN but still unlabelled (hull-touching orphans):
    # render as RED so excluded areas are obvious.
    inside = np.isfinite(elev)
    excluded = inside & (labels < 0)
    rgb[excluded] = (0.92, 0.30, 0.25)

    # Black boundaries between different labels (and between labelled and not).
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
            if r == 0 or labels[r - 1, c] != lab:
                segments.append([(x0, y0), (x1, y0)])
            if r == rows - 1 or labels[r + 1, c] != lab:
                segments.append([(x0, y1), (x1, y1)])
            if c == 0 or labels[r, c - 1] != lab:
                segments.append([(x0, y0), (x0, y1)])
            if c == cols - 1 or labels[r, c + 1] != lab:
                segments.append([(x1, y0), (x1, y1)])

    fig, ax = plt.subplots(figsize=(12, 12))
    ax.imshow(rgb, origin="lower", extent=extent, interpolation="nearest")
    lc = LineCollection(segments, colors="black", linewidths=0.4, alpha=0.75)
    ax.add_collection(lc)

    ax.plot([inl.x for inl in inlets],
            [inl.y for inl in inlets],
            "o", markersize=3.5,
            markerfacecolor="white", markeredgecolor="black",
            markeredgewidth=0.6, zorder=5)

    ax.set_title(title, fontsize=11)
    ax.set_xlabel("X")
    ax.set_ylabel("Y")
    ax.set_aspect("equal")
    # Tiny legend.
    from matplotlib.patches import Patch
    legend = [
        Patch(color=(0.95, 0.95, 0.95), label="outside TIN hull"),
        Patch(color=(0.25, 0.25, 0.25), label="interior hole (building)"),
        Patch(color=(0.92, 0.30, 0.25), label="excluded (drains off-site)"),
    ]
    ax.legend(handles=legend, loc="lower right", fontsize=8, framealpha=0.85)
    fig.tight_layout()
    fig.savefig(out_path, dpi=150)
    plt.close(fig)
    print(f"  Saved {out_path.name}")


if __name__ == "__main__":
    args = sys.argv[1:]
    cell = 10.0
    min_sqft = 500.0
    max_dist = 500.0
    paths = []
    for a in args:
        if a.startswith("cell="):
            cell = float(a.split("=", 1)[1])
        elif a.startswith("min="):
            min_sqft = float(a.split("=", 1)[1])
        elif a.startswith("maxd="):
            max_dist = float(a.split("=", 1)[1])
        else:
            paths.append(Path(a))
    if not paths:
        paths = [Path("testing.xml"), Path("testing2.xml")]
    for p in paths:
        if p.exists():
            run(p, cell_size=cell, min_catchment_sqft=min_sqft,
                max_cell_to_inlet_ft=max_dist)
        else:
            print(f"skip {p} (missing)")
