"""Diagnose why the walker is returning -1 for most cells."""
from pathlib import Path
import numpy as np
from parse_landxml import parse
from tin_harness import (
    Inlet, Resolution, build_tin, elevation_at_xy,
    find_triangle_at_xy, trace,
)

data = parse(Path("testing.xml"))
tin = build_tin(data.surface.points, data.surface.triangles)

inlets = []
for i, (name, s) in enumerate(sorted(data.structures.items())):
    z_surf, _ = elevation_at_xy(tin, s.x, s.y)
    z = z_surf if np.isfinite(z_surf) else s.z_rim
    inlets.append(Inlet(id=i, x=s.x, y=s.y, z=z, name=name))

# Sample a handful of points near inlets and far from them.
# What's the actual walker behavior?
print(f"{'start':>12} {'dist_to_inlet':>14} {'result':>12} {'steps':>6} "
      f"{'final_dist':>12} {'cur_z':>8} {'end_z':>8}")

import random
rng = random.Random(1)
for _ in range(30):
    r_inlet = rng.choice(inlets)
    # point somewhere within 50 ft of this inlet
    dx = rng.uniform(-30, 30)
    dy = rng.uniform(-30, 30)
    sx, sy = r_inlet.x + dx, r_inlet.y + dy
    z_start, ti = elevation_at_xy(tin, sx, sy)
    if not np.isfinite(z_start):
        continue
    res = trace(tin, sx, sy, inlets, snap_tol=5.0, max_steps=2000)
    z_end, _ = elevation_at_xy(tin, res.final_x, res.final_y)
    # distance to any inlet at end
    dmin = min(np.hypot(i.x - res.final_x, i.y - res.final_y) for i in inlets)
    d_start = np.hypot(dx, dy)
    print(f"{sx:7.1f},{sy:7.1f} {d_start:14.1f} {res.how_resolved.name:>12} "
          f"{res.step_count:6d} {dmin:12.2f} {z_start:8.2f} {z_end:8.2f}")
