"""A/B experiments on testing2.xml — compare inlet filters and burn modes.

Runs the hierarchy (no walker, faster) for each variant and reports:
  - labelled / orphan / dominant-inlet share
  - top-5 inlet shares
"""
from __future__ import annotations

import math
import time
from collections import Counter
from pathlib import Path

import numpy as np

from parse_landxml import parse
from tin_harness import (
    Inlet, _sample_elev_grid, _flood_from_inlets,
    burn_pipes_into_elev, build_tin, elevation_at_xy,
)


def run_variant(tag, tin, structs_to_use, pipes_dict, all_structs,
                cell_size, burn_mode="normal", clamp_depth=None):
    """burn_mode: 'none' = no burn; 'normal' = pipe burn; 'clamped' = clamp depth."""
    t0 = time.time()
    elev, ox, oy, rows, cols = _sample_elev_grid(tin, cell_size)

    inlets = []
    for i, (name, s) in enumerate(sorted(structs_to_use.items())):
        zs, _ = elevation_at_xy(tin, s.x, s.y)
        z = zs if math.isfinite(zs) else s.z_rim
        inlets.append(Inlet(id=i, x=s.x, y=s.y, z=z, name=name))

    n_burned = 0
    if burn_mode != "none":
        if burn_mode == "clamped" and clamp_depth is not None:
            # Clamp: cell cannot be lowered more than clamp_depth below its
            # original surface. Reference the original elev via a copy.
            orig = elev.copy()
            n_burned = burn_pipes_into_elev(
                elev, ox, oy, cell_size, pipes_dict, all_structs)
            # Apply clamp: if elev[r,c] < orig[r,c] - clamp_depth, raise it.
            floor = orig - clamp_depth
            mask = elev < floor
            elev[mask] = floor[mask]
            n_clamped = int(mask.sum())
        else:
            n_burned = burn_pipes_into_elev(
                elev, ox, oy, cell_size, pipes_dict, all_structs)
            n_clamped = 0
    else:
        n_clamped = 0

    labels = _flood_from_inlets(elev, inlets, ox, oy, cell_size)
    inside = int(np.isfinite(elev).sum())
    labelled = int((labels >= 0).sum())
    orphan = inside - labelled

    # Per-inlet counts
    uniq, cnt = np.unique(labels[labels >= 0], return_counts=True)
    shares = sorted(zip(cnt.tolist(), uniq.tolist()), reverse=True)

    dt = time.time() - t0
    print(f"--- {tag} ---")
    print(f"  inlets seeded: {len(inlets):>4}   "
          f"pipes burned: {n_burned:>4}  clamped: {n_clamped}")
    print(f"  inside TIN: {inside:>6,}   labelled: {labelled:>6,}   "
          f"orphan: {orphan:>5,}  ({100*orphan/inside:.2f}%)")
    if shares:
        top = shares[0]
        print(f"  dominant inlet: {inlets[top[1]].name}  "
              f"{top[0]:,} cells  ({100*top[0]/labelled:.1f}%)")
        # Top 5
        for i, (c, iid) in enumerate(shares[:5]):
            print(f"    #{i+1}: {inlets[iid].name[:28]:28s} "
                  f"{c:>6,}  ({100*c/labelled:.1f}%)")
    print(f"  elapsed: {dt:.1f}s")
    print()
    return labels, labelled, orphan


def main(xml_path: Path, cell_size: float = 10.0):
    print(f"Loading {xml_path}, building TIN...")
    t0 = time.time()
    data = parse(xml_path)
    tin = build_tin(data.surface.points, data.surface.triangles)
    print(f"  built in {time.time()-t0:.1f}s\n")

    all_structs = data.structures
    all_pipes = data.pipes

    # Classify structures
    from collections import Counter
    in_count = Counter()
    out_count = Counter()
    for p in all_pipes.values():
        out_count[p.start_struct] += 1
        in_count[p.end_struct] += 1

    def is_null(name): return "Null" in name
    def is_outfall(name):
        return in_count.get(name, 0) > 0 and out_count.get(name, 0) == 0

    # Variants
    # A: baseline (all structures, normal burn)
    # B: filter NullStructures
    # C: filter NullStructures + outfalls
    # D: filter nulls + outfalls, NO burn
    # E: filter nulls + outfalls, clamped burn (max 2 ft below surface)
    # F: no filter, NO burn

    nulls_filtered = {n: s for n, s in all_structs.items() if not is_null(n)}
    no_null_no_outfall = {n: s for n, s in all_structs.items()
                          if not is_null(n) and not is_outfall(n)}

    print(f"Structure sets:")
    print(f"  all:                    {len(all_structs):>4}")
    print(f"  no-null:                {len(nulls_filtered):>4}")
    print(f"  no-null-no-outfall:     {len(no_null_no_outfall):>4}")
    print()

    # Baseline
    run_variant("A: all structs + normal burn",
                tin, all_structs, all_pipes, all_structs, cell_size)

    # Filter nulls only
    run_variant("B: no-null + normal burn",
                tin, nulls_filtered, all_pipes, all_structs, cell_size)

    # Filter nulls + outfalls
    run_variant("C: no-null-no-outfall + normal burn",
                tin, no_null_no_outfall, all_pipes, all_structs, cell_size)

    # Unburned with nulls+outfalls filtered
    run_variant("D: no-null-no-outfall + NO burn",
                tin, no_null_no_outfall, all_pipes, all_structs, cell_size,
                burn_mode="none")

    # Clamped burn (max 2 ft)
    run_variant("E: no-null-no-outfall + clamped burn (2 ft max)",
                tin, no_null_no_outfall, all_pipes, all_structs, cell_size,
                burn_mode="clamped", clamp_depth=2.0)

    # All + no burn (pure topography baseline)
    run_variant("F: all structs + NO burn",
                tin, all_structs, all_pipes, all_structs, cell_size,
                burn_mode="none")


if __name__ == "__main__":
    import sys
    xml = Path(sys.argv[1] if len(sys.argv) > 1 else "testing2.xml")
    cell = float(sys.argv[2]) if len(sys.argv) > 2 else 10.0
    main(xml, cell_size=cell)
