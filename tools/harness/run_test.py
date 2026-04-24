"""Run the CATCHMENTTIN Phase 1+2 algorithms against real LandXML data.

A/B compares hierarchy without vs with pipe burning, plus diagnostic plots.

Outputs (under xml_path.parent/out):
  - run_arrays.npz           raw grids for downstream analysis
  - resolution_stats.csv     per-inlet cell counts (burned hierarchy)
  - inlet_share.csv          side-by-side counts: unburned vs burned
  - diagnostics.png          resolution map (walker/spillover/orphan/off)
  - labels_unburned.png      watershed labels from raw-surface hierarchy
  - labels_burned.png        watershed labels from pipe-burned hierarchy
  - labels_diff.png          cells whose inlet assignment changed
  - walker_vs_hier.png       green=walker-direct, orange=hierarchy-filled
  - step_count.png           walker step-count heatmap
"""
from __future__ import annotations

import sys
import time
from pathlib import Path

import numpy as np

from parse_landxml import parse
from tin_harness import (
    Inlet, Resolution, build_basin_hierarchy, build_tin,
    elevation_at_xy, inlet_for_xy, trace,
)


def main(xml_path: Path, cell_size: float = 10.0, snap_tol: float = 5.0,
         trench_depth: float = 0.0) -> None:
    print(f"Loading {xml_path}...")
    t0 = time.time()
    data = parse(xml_path)
    print(f"  Parsed in {time.time() - t0:.2f}s")
    print(f"  Surface: {len(data.surface.points):,} pts, "
          f"{len(data.surface.triangles):,} tris")
    print(f"  {len(data.structures)} structures, {len(data.pipes)} pipes")
    print()

    print("Building TIN index...")
    t0 = time.time()
    tin = build_tin(data.surface.points, data.surface.triangles)
    print(f"  Built in {time.time() - t0:.2f}s")
    print(f"  Bounds: {tin.bounds}")
    print()

    inlets: list[Inlet] = []
    for i, (name, s) in enumerate(sorted(data.structures.items())):
        z_surf, _ = elevation_at_xy(tin, s.x, s.y)
        z = z_surf if np.isfinite(z_surf) else s.z_rim
        inlets.append(Inlet(id=i, x=s.x, y=s.y, z=z, name=name))
    print(f"Placed {len(inlets)} inlets on surface")
    on_surface = sum(
        1 for inl in inlets
        if np.isfinite(elevation_at_xy(tin, inl.x, inl.y)[0])
    )
    print(f"  {on_surface}/{len(inlets)} fall inside TIN boundary")
    print()

    print(f"Building BasinHierarchy [unburned] (cell={cell_size:.1f})...")
    hier_u = build_basin_hierarchy(tin, inlets, cell_size)
    print(f"  Grid: {hier_u.cols} x {hier_u.rows} = "
          f"{hier_u.cols * hier_u.rows:,} cells")
    print(f"  Labelled: {hier_u.labelled_cells:,}  "
          f"Orphan: {hier_u.orphan_cells:,}  "
          f"Time: {hier_u.build_time_s:.1f}s")
    print()

    print(f"Building BasinHierarchy [burned]   (trench={trench_depth:.1f})...")
    hier_b = build_basin_hierarchy(
        tin, inlets, cell_size,
        pipes=data.pipes, structures=data.structures,
        trench_depth=trench_depth,
    )
    print(f"  Labelled: {hier_b.labelled_cells:,}  "
          f"Orphan: {hier_b.orphan_cells:,}  "
          f"Time: {hier_b.build_time_s:.1f}s")
    n_burned = int(np.sum(hier_b.elev < hier_u.elev - 1e-6))
    print(f"  Pipe-burned cells: {n_burned:,}")
    print()

    print(f"Walking every cell (snap_tol={snap_tol:.1f})...")
    t0 = time.time()
    rows, cols = hier_b.rows, hier_b.cols
    walker_labels = np.full((rows, cols), -1, dtype=np.int64)
    resolution = np.zeros((rows, cols), dtype=np.uint8)
    RCODE = {
        Resolution.OFF_SURFACE: 0,
        Resolution.DIRECT: 1,
        Resolution.SPILLOVER: 2,
        Resolution.ORPHAN: 3,
    }
    step_count = np.zeros((rows, cols), dtype=np.int32)

    total = rows * cols
    done = 0
    last_report = 0
    for r in range(rows):
        y = hier_b.origin_y + r * hier_b.cell_size
        for c in range(cols):
            x = hier_b.origin_x + c * hier_b.cell_size
            if not np.isfinite(hier_b.elev[r, c]) and \
                    not np.isfinite(hier_u.elev[r, c]):
                resolution[r, c] = RCODE[Resolution.OFF_SURFACE]
                done += 1
                continue
            res = trace(tin, x, y, inlets, snap_tol=snap_tol,
                        max_steps=2000, capture_path=False)
            step_count[r, c] = res.step_count
            if res.inlet_id >= 0:
                walker_labels[r, c] = res.inlet_id
                resolution[r, c] = RCODE[Resolution.DIRECT]
            else:
                spill = inlet_for_xy(hier_b, x, y)
                if spill >= 0:
                    walker_labels[r, c] = spill
                    resolution[r, c] = RCODE[Resolution.SPILLOVER]
                else:
                    resolution[r, c] = RCODE[Resolution.ORPHAN]
            done += 1

        pct = int(done * 100 / total)
        if pct >= last_report + 5:
            print(f"  {pct}%  ({done:,}/{total:,}) "
                  f"elapsed={time.time()-t0:.0f}s", flush=True)
            last_report = pct

    walker_secs = time.time() - t0
    print(f"  Walker done in {walker_secs:.1f}s")
    print()

    direct = int((resolution == RCODE[Resolution.DIRECT]).sum())
    spillover = int((resolution == RCODE[Resolution.SPILLOVER]).sum())
    orphan = int((resolution == RCODE[Resolution.ORPHAN]).sum())
    off_surface = int((resolution == RCODE[Resolution.OFF_SURFACE]).sum())
    resolved = direct + spillover
    inside = resolved + orphan

    print("=" * 60)
    print(f"RESOLUTION BREAKDOWN (cell={cell_size:.1f}, snap={snap_tol:.1f}, "
          f"trench={trench_depth:.1f})")
    print("=" * 60)
    print(f"Total cells:          {total:,}")
    print(f"Inside TIN:           {inside:,}")
    print(f"Off-surface:          {off_surface:,}")
    print()
    if inside > 0:
        print(f"Direct (walker):      {direct:,}  "
              f"({100*direct/inside:.1f}%)")
        print(f"Spillover (burned):   {spillover:,}  "
              f"({100*spillover/inside:.1f}%)")
        print(f"Orphan (no inlet):    {orphan:,}  "
              f"({100*orphan/inside:.1f}%)")
    print()

    # A/B impact of pipe burning on spillover labels.
    u_mask = hier_u.labels >= 0
    b_mask = hier_b.labels >= 0
    both = u_mask & b_mask
    changed = both & (hier_u.labels != hier_b.labels)
    print(f"Hierarchy label changes from burning: "
          f"{int(changed.sum()):,} / {int(both.sum()):,} cells "
          f"({100*changed.sum()/max(1,both.sum()):.1f}%)")
    print()

    # Per-inlet shares, unburned vs burned.
    print("Top inlets by hierarchy share:")
    uniq_u, cnt_u = np.unique(
        hier_u.labels[u_mask], return_counts=True)
    uniq_b, cnt_b = np.unique(
        hier_b.labels[b_mask], return_counts=True)
    u_share = dict(zip(uniq_u.tolist(), cnt_u.tolist()))
    b_share = dict(zip(uniq_b.tolist(), cnt_b.tolist()))
    all_ids = sorted(set(u_share) | set(b_share))
    # Sort by burned descending, show top 15.
    all_ids.sort(key=lambda i: -b_share.get(i, 0))
    tot_u = max(1, sum(u_share.values()))
    tot_b = max(1, sum(b_share.values()))
    print(f"  {'inlet':>20}  {'unburned':>12}  {'burned':>12}")
    for iid in all_ids[:15]:
        u_c = u_share.get(iid, 0)
        b_c = b_share.get(iid, 0)
        name = inlets[int(iid)].name
        print(f"  {name[:20]:>20}  "
              f"{u_c:6d} ({100*u_c/tot_u:4.1f}%)  "
              f"{b_c:6d} ({100*b_c/tot_b:4.1f}%)")
    print()

    out_dir = xml_path.parent / "out"
    out_dir.mkdir(exist_ok=True)
    np.savez(
        out_dir / "run_arrays.npz",
        elev_unburned=hier_u.elev, elev_burned=hier_b.elev,
        labels_unburned=hier_u.labels, labels_burned=hier_b.labels,
        walker_labels=walker_labels, resolution=resolution,
        step_count=step_count,
        origin_x=hier_b.origin_x, origin_y=hier_b.origin_y,
        cell_size=hier_b.cell_size,
    )
    print(f"Saved arrays to {out_dir / 'run_arrays.npz'}")

    # Per-inlet CSVs.
    csv_path = out_dir / "resolution_stats.csv"
    uniq, counts = np.unique(
        walker_labels[walker_labels >= 0], return_counts=True)
    with open(csv_path, "w") as f:
        f.write("inlet_id,inlet_name,cell_count,approx_area_sqft\n")
        for inlet_id, cnt in zip(uniq, counts):
            nm = inlets[int(inlet_id)].name
            area = int(cnt) * hier_b.cell_size * hier_b.cell_size
            f.write(f"{int(inlet_id)},{nm},{int(cnt)},{area:.0f}\n")
    print(f"Saved per-inlet stats to {csv_path}")

    share_csv = out_dir / "inlet_share.csv"
    with open(share_csv, "w") as f:
        f.write("inlet_id,inlet_name,unburned_cells,burned_cells,delta\n")
        for iid in all_ids:
            u_c = u_share.get(iid, 0)
            b_c = b_share.get(iid, 0)
            nm = inlets[int(iid)].name
            f.write(f"{int(iid)},{nm},{u_c},{b_c},{b_c - u_c}\n")
    print(f"Saved inlet share comparison to {share_csv}")

    try:
        _make_plots(hier_u, hier_b, walker_labels, resolution, step_count,
                    inlets, out_dir)
    except Exception as ex:
        import traceback
        print(f"Plot step failed: {ex}")
        traceback.print_exc()


def _make_plots(hier_u, hier_b, walker_labels, resolution, step_count,
                inlets, out_dir) -> None:
    import matplotlib.pyplot as plt
    from matplotlib.colors import ListedColormap

    extent = (
        hier_b.origin_x, hier_b.origin_x + hier_b.cols * hier_b.cell_size,
        hier_b.origin_y, hier_b.origin_y + hier_b.rows * hier_b.cell_size,
    )
    xs = [inl.x for inl in inlets]
    ys = [inl.y for inl in inlets]

    # Resolution map.
    res_colors = ListedColormap(
        ["#222222", "#2ecc71", "#f39c12", "#e74c3c"]
    )
    fig, ax = plt.subplots(figsize=(10, 10))
    ax.imshow(resolution, origin="lower", extent=extent, cmap=res_colors,
              vmin=0, vmax=3, interpolation="nearest")
    ax.scatter(xs, ys, s=12, c="white", edgecolor="black", linewidth=0.5,
               zorder=5)
    ax.set_title("Resolution (black=off-surface, green=Direct, "
                 "orange=Spillover, red=Orphan)")
    ax.set_aspect("equal")
    fig.tight_layout()
    fig.savefig(out_dir / "diagnostics.png", dpi=140)
    plt.close(fig)
    print(f"Saved {out_dir / 'diagnostics.png'}")

    # Watershed label maps (unburned and burned).
    rng = np.random.default_rng(42)
    n_inlets = len(inlets)
    palette = rng.random((n_inlets, 3))

    for tag, hier in (("unburned", hier_u), ("burned", hier_b)):
        rgb = np.zeros((hier.rows, hier.cols, 3), dtype=np.float32)
        mask = hier.labels >= 0
        rgb[mask] = palette[hier.labels[mask]]
        fig, ax = plt.subplots(figsize=(10, 10))
        ax.imshow(rgb, origin="lower", extent=extent, interpolation="nearest")
        ax.scatter(xs, ys, s=14, c="white", edgecolor="black", linewidth=0.5,
                   zorder=5)
        ax.set_title(f"Watershed labels ({tag} hierarchy); "
                     f"{n_inlets} inlets")
        ax.set_aspect("equal")
        fig.tight_layout()
        out_path = out_dir / f"labels_{tag}.png"
        fig.savefig(out_path, dpi=140)
        plt.close(fig)
        print(f"Saved {out_path}")

    # Difference between unburned and burned labels.
    both = (hier_u.labels >= 0) & (hier_b.labels >= 0)
    diff = np.zeros_like(hier_b.labels, dtype=np.uint8)
    diff[(~np.isfinite(hier_b.elev))] = 0          # off-surface
    diff[both & (hier_u.labels == hier_b.labels)] = 1  # same
    diff[both & (hier_u.labels != hier_b.labels)] = 2  # changed
    diff_colors = ListedColormap(["#222222", "#3b4a5a", "#f1c40f"])
    fig, ax = plt.subplots(figsize=(10, 10))
    ax.imshow(diff, origin="lower", extent=extent, cmap=diff_colors,
              vmin=0, vmax=2, interpolation="nearest")
    ax.scatter(xs, ys, s=14, c="white", edgecolor="black", linewidth=0.5,
               zorder=5)
    ax.set_title("Pipe-burn effect: yellow = inlet assignment changed")
    ax.set_aspect("equal")
    fig.tight_layout()
    fig.savefig(out_dir / "labels_diff.png", dpi=140)
    plt.close(fig)
    print(f"Saved {out_dir / 'labels_diff.png'}")

    # Walker-only vs hierarchy overlay.
    wh = np.zeros_like(resolution)
    wh[resolution == 1] = 1  # Direct
    wh[resolution == 2] = 2  # Spillover
    wh[resolution == 3] = 3  # Orphan
    wh_colors = ListedColormap(
        ["#222222", "#2ecc71", "#f39c12", "#e74c3c"]
    )
    fig, ax = plt.subplots(figsize=(10, 10))
    ax.imshow(wh, origin="lower", extent=extent, cmap=wh_colors,
              vmin=0, vmax=3, interpolation="nearest")
    ax.scatter(xs, ys, s=14, c="white", edgecolor="black", linewidth=0.5,
               zorder=5)
    ax.set_title("Walker-direct (green) vs hierarchy-filled (orange)")
    ax.set_aspect("equal")
    fig.tight_layout()
    fig.savefig(out_dir / "walker_vs_hier.png", dpi=140)
    plt.close(fig)
    print(f"Saved {out_dir / 'walker_vs_hier.png'}")

    # Step-count heatmap.
    sc = step_count.astype(np.float32)
    sc_display = np.where(sc > 0, sc, np.nan)
    fig, ax = plt.subplots(figsize=(10, 10))
    im = ax.imshow(sc_display, origin="lower", extent=extent,
                   cmap="viridis", interpolation="nearest")
    ax.scatter(xs, ys, s=14, c="white", edgecolor="black", linewidth=0.5,
               zorder=5)
    fig.colorbar(im, ax=ax, label="walker steps before stop")
    ax.set_title("Walker step count (high = long traces, "
                 "may indicate ridges/flats)")
    ax.set_aspect("equal")
    fig.tight_layout()
    fig.savefig(out_dir / "step_count.png", dpi=140)
    plt.close(fig)
    print(f"Saved {out_dir / 'step_count.png'}")


if __name__ == "__main__":
    xml = Path(sys.argv[1] if len(sys.argv) > 1 else "testing.xml")
    cell = float(sys.argv[2]) if len(sys.argv) > 2 else 10.0
    snap = float(sys.argv[3]) if len(sys.argv) > 3 else 5.0
    trench = float(sys.argv[4]) if len(sys.argv) > 4 else 0.0
    main(xml, cell_size=cell, snap_tol=snap, trench_depth=trench)
