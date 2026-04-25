"""Pure-Python re-implementation of CATCHMENTTIN's TinWalker + BasinHierarchy.

Mirrors the C# logic in CSharp/Services/TinWalker.cs and
CSharp/Services/BasinHierarchy.cs so we can test Phase 1 logic against
real LandXML data without needing Civil 3D.
"""
from __future__ import annotations

import heapq
import math
import time
from dataclasses import dataclass, field
from enum import Enum
from typing import Dict, List, Optional, Tuple

import numpy as np
from scipy.spatial import cKDTree


class Resolution(Enum):
    DIRECT = "Direct"
    SPILLOVER = "Spillover"
    OFF_SURFACE = "OffSurface"
    ORPHAN = "Orphan"


@dataclass
class TraceResult:
    inlet_id: int = -1
    step_count: int = 0
    final_x: float = 0.0
    final_y: float = 0.0
    how_resolved: Resolution = Resolution.DIRECT
    path: Optional[List[Tuple[float, float]]] = None


@dataclass
class Tin:
    """Fast TIN with triangle adjacency + spatial index."""

    # per-vertex (point id maps to row index in vertex_xyz)
    vertex_xyz: np.ndarray  # (n_pts, 3)
    id_to_idx: Dict[int, int]

    # per-triangle
    tri_verts: np.ndarray  # (n_tri, 3) int indices into vertex_xyz
    tri_centroid_xy: np.ndarray  # (n_tri, 2)
    tri_bbox: np.ndarray  # (n_tri, 4) xmin, ymin, xmax, ymax
    tri_neighbors: np.ndarray  # (n_tri, 3) index of neighbor tri across each edge, -1 if none

    # spatial index
    centroid_tree: cKDTree

    bounds: Tuple[float, float, float, float]  # xmin, ymin, xmax, ymax


def build_tin(points: Dict[int, Tuple[float, float, float]],
              triangles: List[Tuple[int, int, int]]) -> Tin:
    # Map point ids to array rows.
    ids = sorted(points.keys())
    id_to_idx = {pid: i for i, pid in enumerate(ids)}
    vertex_xyz = np.array([points[pid] for pid in ids], dtype=np.float64)

    tri_verts = np.array(
        [(id_to_idx[a], id_to_idx[b], id_to_idx[c]) for a, b, c in triangles],
        dtype=np.int64,
    )
    n_tri = len(tri_verts)

    v0 = vertex_xyz[tri_verts[:, 0], :2]
    v1 = vertex_xyz[tri_verts[:, 1], :2]
    v2 = vertex_xyz[tri_verts[:, 2], :2]

    tri_centroid_xy = (v0 + v1 + v2) / 3.0
    xmin = np.minimum(np.minimum(v0[:, 0], v1[:, 0]), v2[:, 0])
    xmax = np.maximum(np.maximum(v0[:, 0], v1[:, 0]), v2[:, 0])
    ymin = np.minimum(np.minimum(v0[:, 1], v1[:, 1]), v2[:, 1])
    ymax = np.maximum(np.maximum(v0[:, 1], v1[:, 1]), v2[:, 1])
    tri_bbox = np.stack([xmin, ymin, xmax, ymax], axis=1)

    # Triangle adjacency. Edges keyed by sorted (vid_a, vid_b).
    edge_map: Dict[Tuple[int, int], List[int]] = {}
    for ti in range(n_tri):
        a, b, c = tri_verts[ti]
        for u, v in ((a, b), (b, c), (c, a)):
            key = (min(u, v), max(u, v))
            edge_map.setdefault(key, []).append(ti)

    tri_neighbors = np.full((n_tri, 3), -1, dtype=np.int64)
    for ti in range(n_tri):
        a, b, c = tri_verts[ti]
        for slot, (u, v) in enumerate(((a, b), (b, c), (c, a))):
            key = (min(u, v), max(u, v))
            pair = edge_map[key]
            if len(pair) == 2:
                tri_neighbors[ti, slot] = pair[0] if pair[1] == ti else pair[1]

    centroid_tree = cKDTree(tri_centroid_xy)

    bounds = (
        float(vertex_xyz[:, 0].min()),
        float(vertex_xyz[:, 1].min()),
        float(vertex_xyz[:, 0].max()),
        float(vertex_xyz[:, 1].max()),
    )

    return Tin(
        vertex_xyz=vertex_xyz,
        id_to_idx=id_to_idx,
        tri_verts=tri_verts,
        tri_centroid_xy=tri_centroid_xy,
        tri_bbox=tri_bbox,
        tri_neighbors=tri_neighbors,
        centroid_tree=centroid_tree,
        bounds=bounds,
    )


# --- Triangle lookup --------------------------------------------------------

def _bary(p: np.ndarray, v0: np.ndarray, v1: np.ndarray, v2: np.ndarray
          ) -> Tuple[float, float, float]:
    """Barycentric coords of p in triangle (v0, v1, v2). 2D only."""
    dx1 = v1[0] - v0[0]; dy1 = v1[1] - v0[1]
    dx2 = v2[0] - v0[0]; dy2 = v2[1] - v0[1]
    denom = dx1 * dy2 - dx2 * dy1
    if abs(denom) < 1e-18:
        return (-1.0, -1.0, -1.0)
    dpx = p[0] - v0[0]; dpy = p[1] - v0[1]
    b1 = (dpx * dy2 - dx2 * dpy) / denom
    b2 = (dx1 * dpy - dpx * dy1) / denom
    b0 = 1.0 - b1 - b2
    return (b0, b1, b2)


def find_triangle_at_xy(tin: Tin, x: float, y: float,
                        hint: int = -1) -> int:
    """Return triangle index containing (x,y), or -1 if outside surface."""
    p = np.array([x, y])

    # Fast path: walk from hint via adjacency.
    if hint >= 0:
        ti = hint
        for _ in range(32):
            vidx = tin.tri_verts[ti]
            v0 = tin.vertex_xyz[vidx[0], :2]
            v1 = tin.vertex_xyz[vidx[1], :2]
            v2 = tin.vertex_xyz[vidx[2], :2]
            b0, b1, b2 = _bary(p, v0, v1, v2)
            if b0 >= -1e-9 and b1 >= -1e-9 and b2 >= -1e-9:
                return ti
            # Step into the neighbor opposite the most-negative bary edge.
            bs = [b0, b1, b2]
            neg_slot = min(range(3), key=lambda k: bs[k])
            # slot 0 (b0) opposite edge (v1,v2) which is slot index 1 in neighbors
            # slot layout matches edge order (a,b),(b,c),(c,a) so bary slot k
            # is opposite edge slot (k+1) % 3
            nbr = tin.tri_neighbors[ti, (neg_slot + 1) % 3]
            if nbr < 0:
                break
            ti = nbr

    # Fallback: k-nearest centroids, then linear check.
    dists, idx = tin.centroid_tree.query([x, y], k=min(32, len(tin.tri_verts)))
    for ti in np.atleast_1d(idx):
        ti = int(ti)
        bb = tin.tri_bbox[ti]
        if x < bb[0] - 1e-9 or x > bb[2] + 1e-9:
            continue
        if y < bb[1] - 1e-9 or y > bb[3] + 1e-9:
            continue
        vidx = tin.tri_verts[ti]
        v0 = tin.vertex_xyz[vidx[0], :2]
        v1 = tin.vertex_xyz[vidx[1], :2]
        v2 = tin.vertex_xyz[vidx[2], :2]
        b0, b1, b2 = _bary(p, v0, v1, v2)
        if b0 >= -1e-9 and b1 >= -1e-9 and b2 >= -1e-9:
            return ti
    return -1


def elevation_at_xy(tin: Tin, x: float, y: float, hint: int = -1
                    ) -> Tuple[float, int]:
    """Return (z, tri_index). z is NaN if off-surface."""
    ti = find_triangle_at_xy(tin, x, y, hint=hint)
    if ti < 0:
        return (float("nan"), -1)
    vidx = tin.tri_verts[ti]
    v0 = tin.vertex_xyz[vidx[0]]
    v1 = tin.vertex_xyz[vidx[1]]
    v2 = tin.vertex_xyz[vidx[2]]
    b0, b1, b2 = _bary(np.array([x, y]), v0[:2], v1[:2], v2[:2])
    z = b0 * v0[2] + b1 * v1[2] + b2 * v2[2]
    return (float(z), ti)


# --- Walker (mirror of TinWalker.cs) ---------------------------------------

@dataclass
class Inlet:
    id: int
    x: float
    y: float
    z: float
    name: str


def _compute_gradient(v0: np.ndarray, v1: np.ndarray, v2: np.ndarray
                      ) -> Tuple[float, float]:
    """Steepest-ascent gradient (dz/dx, dz/dy) of the plane through 3 3D pts."""
    e1 = v1 - v0
    e2 = v2 - v0
    nz = e1[0] * e2[1] - e1[1] * e2[0]
    if abs(nz) < 1e-12:
        return (0.0, 0.0)
    nx = e1[1] * e2[2] - e1[2] * e2[1]
    ny = e1[2] * e2[0] - e1[0] * e2[2]
    return (-nx / nz, -ny / nz)


def _nearest_inlet(x: float, y: float, inlets: List[Inlet], snap: float
                   ) -> int:
    best_d2 = snap * snap
    best = -1
    for inl in inlets:
        dx = x - inl.x; dy = y - inl.y
        d2 = dx * dx + dy * dy
        if d2 < best_d2:
            best_d2 = d2
            best = inl.id
    return best


def _segment_inlet_hit(sx: float, sy: float, ex: float, ey: float,
                       inlets: List[Inlet], snap: float
                       ) -> Optional[Tuple[int, float, float]]:
    """Does the segment s→e pass within `snap` of any inlet?

    If multiple inlets qualify, return the one with the smallest parameter t
    (the inlet the drop reaches first along its path), not the nearest in
    distance — that matches real-world inlet interception: a drop falls into
    the first grate it crosses, even if a closer grate is further along.

    Returns (inlet_id, hit_x, hit_y) or None.
    """
    dx = ex - sx
    dy = ey - sy
    seg_len2 = dx * dx + dy * dy
    if seg_len2 < 1e-18:
        return None
    snap2 = snap * snap
    best_t = 2.0
    best_id = -1
    best_hx = 0.0
    best_hy = 0.0
    for inl in inlets:
        t = ((inl.x - sx) * dx + (inl.y - sy) * dy) / seg_len2
        if t < 0.0:
            t = 0.0
        elif t > 1.0:
            t = 1.0
        hx = sx + t * dx
        hy = sy + t * dy
        ddx = inl.x - hx
        ddy = inl.y - hy
        d2 = ddx * ddx + ddy * ddy
        if d2 < snap2 and t < best_t:
            best_t = t
            best_id = inl.id
            best_hx = hx
            best_hy = hy
    if best_id >= 0:
        return (best_id, best_hx, best_hy)
    return None


def trace(tin: Tin, start_x: float, start_y: float,
          inlets: List[Inlet], snap_tol: float = 5.0,
          max_steps: int = 5000, capture_path: bool = False
          ) -> TraceResult:
    cx, cy = start_x, start_y
    path = [(cx, cy)] if capture_path else None
    hint = -1

    for step in range(max_steps):
        # Arrived at an inlet?
        near = _nearest_inlet(cx, cy, inlets, snap_tol)
        if near >= 0:
            return TraceResult(
                inlet_id=near, step_count=step,
                final_x=cx, final_y=cy,
                how_resolved=Resolution.DIRECT, path=path,
            )

        ti = find_triangle_at_xy(tin, cx, cy, hint=hint)
        if ti < 0:
            return TraceResult(
                inlet_id=-1, step_count=step, final_x=cx, final_y=cy,
                how_resolved=Resolution.OFF_SURFACE, path=path,
            )
        hint = ti

        vidx = tin.tri_verts[ti]
        v0 = tin.vertex_xyz[vidx[0]]
        v1 = tin.vertex_xyz[vidx[1]]
        v2 = tin.vertex_xyz[vidx[2]]
        dx, dy = _compute_gradient(v0, v1, v2)
        g = math.hypot(dx, dy)
        if g < 1e-10:
            return TraceResult(
                inlet_id=-1, step_count=step, final_x=cx, final_y=cy,
                how_resolved=Resolution.ORPHAN, path=path,
            )

        # Median triangle edge length as step size (matches C# heuristic).
        e01 = math.hypot(v0[0] - v1[0], v0[1] - v1[1])
        e12 = math.hypot(v1[0] - v2[0], v1[1] - v2[1])
        e20 = math.hypot(v2[0] - v0[0], v2[1] - v0[1])
        edges = sorted([e01, e12, e20])
        step_size = edges[1]

        nx = -dx / g; ny = -dy / g
        new_x = cx + nx * step_size
        new_y = cy + ny * step_size

        # Verify we descended.
        bx, by, bz = _bary(
            np.array([cx, cy]), v0[:2], v1[:2], v2[:2],
        )
        cur_z = bx * v0[2] + by * v1[2] + bz * v2[2]
        new_z, _ = elevation_at_xy(tin, new_x, new_y, hint=hint)
        if not math.isfinite(new_z):
            return TraceResult(
                inlet_id=-1, step_count=step, final_x=cx, final_y=cy,
                how_resolved=Resolution.OFF_SURFACE, path=path,
            )
        if new_z >= cur_z - 1e-6:
            # Shrink step once; if still no descent, declare stuck.
            step_size *= 0.25
            new_x = cx + nx * step_size
            new_y = cy + ny * step_size
            new_z, _ = elevation_at_xy(tin, new_x, new_y, hint=hint)
            if not math.isfinite(new_z) or new_z >= cur_z - 1e-6:
                return TraceResult(
                    inlet_id=-1, step_count=step,
                    final_x=cx, final_y=cy,
                    how_resolved=Resolution.ORPHAN, path=path,
                )

        # Inlet interception: a drop is captured by any inlet grate its
        # path passes within snap_tol of, not just the step's endpoint.
        # Fixes the walker's 1%-direct rate on real TINs: drops routinely
        # skip past inlets and die in downstream micro-sinks.
        hit = _segment_inlet_hit(cx, cy, new_x, new_y, inlets, snap_tol)
        if hit is not None:
            inlet_id, hx, hy = hit
            if path is not None:
                path.append((hx, hy))
            return TraceResult(
                inlet_id=inlet_id, step_count=step,
                final_x=hx, final_y=hy,
                how_resolved=Resolution.DIRECT, path=path,
            )

        cx, cy = new_x, new_y
        if path is not None:
            path.append((cx, cy))

    # Max steps exceeded — classify as stuck.
    return TraceResult(
        inlet_id=-1, step_count=max_steps,
        final_x=cx, final_y=cy,
        how_resolved=Resolution.ORPHAN, path=path,
    )


# --- BasinHierarchy (mirror of BasinHierarchy.cs) --------------------------

@dataclass
class BasinHierarchy:
    rows: int
    cols: int
    origin_x: float
    origin_y: float
    cell_size: float
    labels: np.ndarray  # (rows, cols) inlet id per cell, -1 if unreachable
    elev: np.ndarray  # (rows, cols) elevation or NaN
    labelled_cells: int
    orphan_cells: int
    build_time_s: float


def _sample_elev_grid(tin: Tin, cell_size: float
                      ) -> Tuple[np.ndarray, float, float, int, int]:
    xmin, ymin, xmax, ymax = tin.bounds
    buffer = cell_size
    origin_x = xmin + buffer
    origin_y = ymin + buffer
    ext_x = (xmax - buffer) - origin_x
    ext_y = (ymax - buffer) - origin_y
    cols = max(1, int(ext_x / cell_size) + 1)
    rows = max(1, int(ext_y / cell_size) + 1)

    elev = np.full((rows, cols), np.nan, dtype=np.float64)
    hint = -1
    for r in range(rows):
        y = origin_y + r * cell_size
        for c in range(cols):
            x = origin_x + c * cell_size
            z, hint = elevation_at_xy(tin, x, y, hint=hint)
            elev[r, c] = z
    return elev, origin_x, origin_y, rows, cols


def _flood_from_inlets(elev: np.ndarray, inlets: List[Inlet],
                       origin_x: float, origin_y: float, cell_size: float
                       ) -> np.ndarray:
    rows, cols = elev.shape
    labels = np.full((rows, cols), -1, dtype=np.int64)
    enqueued = np.zeros((rows, cols), dtype=bool)
    heap: List[Tuple[float, int, int, int]] = []

    for inl in inlets:
        c = int(round((inl.x - origin_x) / cell_size))
        r = int(round((inl.y - origin_y) / cell_size))
        if not (0 <= r < rows and 0 <= c < cols):
            continue
        if not np.isfinite(elev[r, c]):
            continue
        if enqueued[r, c]:
            continue
        labels[r, c] = inl.id
        enqueued[r, c] = True
        heapq.heappush(heap, (float(elev[r, c]), r, c, inl.id))

    while heap:
        water_z, r, c, inlet_id = heapq.heappop(heap)
        for dr, dc in ((-1, 0), (1, 0), (0, -1), (0, 1)):
            nr, nc = r + dr, c + dc
            if not (0 <= nr < rows and 0 <= nc < cols):
                continue
            if enqueued[nr, nc]:
                continue
            if not np.isfinite(elev[nr, nc]):
                continue
            nw = max(float(elev[nr, nc]), water_z)
            labels[nr, nc] = inlet_id
            enqueued[nr, nc] = True
            heapq.heappush(heap, (nw, nr, nc, inlet_id))

    return labels


def _pipe_end_invert(struct, role: str) -> float:
    """role = 'out' (upstream of pipe) or 'in' (downstream of pipe).
    Prefer matching flow direction; fall back to min invert, then sump.
    """
    matches: List[float] = []
    others: List[float] = []
    for fd, z in struct.inverts:
        if not math.isfinite(z):
            continue
        fd_lower = (fd or "").lower()
        if role == "out" and fd_lower.startswith("out"):
            matches.append(z)
        elif role == "in" and fd_lower.startswith("in"):
            matches.append(z)
        else:
            others.append(z)
    if matches:
        return min(matches)
    if others:
        return min(others)
    if math.isfinite(struct.z_sump):
        return struct.z_sump
    return struct.z_rim


def burn_pipes_into_elev(elev: np.ndarray, origin_x: float, origin_y: float,
                         cell_size: float, pipes, structures,
                         trench_depth: float = 0.0
                         ) -> int:
    """Lower grid cells along pipe segments to interpolated invert + trench.
    Returns number of cells modified."""
    rows, cols = elev.shape
    modified = 0
    for pipe in pipes.values():
        start = structures.get(pipe.start_struct)
        end = structures.get(pipe.end_struct)
        if start is None or end is None:
            continue
        iv_start = _pipe_end_invert(start, "out")
        iv_end = _pipe_end_invert(end, "in")
        if not (math.isfinite(iv_start) and math.isfinite(iv_end)):
            continue
        x0, y0 = start.x, start.y
        x1, y1 = end.x, end.y
        length = math.hypot(x1 - x0, y1 - y0)
        if length < 1e-6:
            continue
        n_samples = max(2, int(length / (cell_size * 0.5)) + 1)
        seen: set = set()
        for i in range(n_samples):
            t = i / (n_samples - 1)
            x = x0 + t * (x1 - x0)
            y = y0 + t * (y1 - y0)
            z_iv = iv_start + t * (iv_end - iv_start) - trench_depth
            c = int(round((x - origin_x) / cell_size))
            r = int(round((y - origin_y) / cell_size))
            if not (0 <= r < rows and 0 <= c < cols):
                continue
            if (r, c) in seen:
                continue
            seen.add((r, c))
            if not math.isfinite(elev[r, c]):
                continue
            if z_iv < elev[r, c]:
                elev[r, c] = z_iv
                modified += 1
    return modified


def build_basin_hierarchy(tin: Tin, inlets: List[Inlet], cell_size: float,
                          pipes=None, structures=None,
                          trench_depth: float = 0.0
                          ) -> BasinHierarchy:
    t0 = time.time()
    elev, origin_x, origin_y, rows, cols = _sample_elev_grid(tin, cell_size)

    if pipes and structures:
        burn_pipes_into_elev(elev, origin_x, origin_y, cell_size,
                             pipes, structures, trench_depth=trench_depth)

    labels = _flood_from_inlets(elev, inlets, origin_x, origin_y, cell_size)

    labelled = int((labels >= 0).sum())
    inside = int(np.isfinite(elev).sum())
    orphan = inside - labelled

    return BasinHierarchy(
        rows=rows, cols=cols, origin_x=origin_x, origin_y=origin_y,
        cell_size=cell_size, labels=labels, elev=elev,
        labelled_cells=labelled, orphan_cells=orphan,
        build_time_s=time.time() - t0,
    )


def inlet_for_xy(hierarchy: BasinHierarchy, x: float, y: float) -> int:
    c = int(round((x - hierarchy.origin_x) / hierarchy.cell_size))
    r = int(round((y - hierarchy.origin_y) / hierarchy.cell_size))
    if not (0 <= r < hierarchy.rows and 0 <= c < hierarchy.cols):
        return -1
    return int(hierarchy.labels[r, c])


# --- Post-processing: hull detection + orphan resolution ------------------

def classify_off_surface(elev: np.ndarray) -> Tuple[np.ndarray, np.ndarray]:
    """Separate off-surface cells into "outside the TIN hull" vs
    "interior holes inside the TIN" (e.g., building footprints cut from
    the surface). Flood-fills off-surface cells from the grid boundary;
    any NaN cell reached is outside-hull. The rest are interior holes.

    Returns (outside_hull_mask, interior_holes_mask) — both booleans.
    """
    rows, cols = elev.shape
    is_off = np.isnan(elev)
    outside = np.zeros_like(is_off, dtype=bool)
    stack: List[Tuple[int, int]] = []

    for c in range(cols):
        if is_off[0, c] and not outside[0, c]:
            outside[0, c] = True; stack.append((0, c))
        if is_off[rows - 1, c] and not outside[rows - 1, c]:
            outside[rows - 1, c] = True; stack.append((rows - 1, c))
    for r in range(rows):
        if is_off[r, 0] and not outside[r, 0]:
            outside[r, 0] = True; stack.append((r, 0))
        if is_off[r, cols - 1] and not outside[r, cols - 1]:
            outside[r, cols - 1] = True; stack.append((r, cols - 1))

    while stack:
        r, c = stack.pop()
        for dr, dc in ((-1, 0), (1, 0), (0, -1), (0, 1)):
            nr, nc = r + dr, c + dc
            if 0 <= nr < rows and 0 <= nc < cols:
                if is_off[nr, nc] and not outside[nr, nc]:
                    outside[nr, nc] = True
                    stack.append((nr, nc))

    interior = is_off & ~outside
    return outside, interior


def resolve_orphan_islands(labels: np.ndarray, elev: np.ndarray,
                           inlets: List[Inlet], outside_hull: np.ndarray,
                           origin_x: float, origin_y: float,
                           cell_size: float,
                           include_interior_holes: bool = True,
                           cover_full_bbox: bool = True,
                           bbox_margin_cells: int = 2,
                           ) -> Tuple[int, int, int]:
    """Assign every cell that isn't labelled to its Euclidean-nearest inlet.

    Default behavior (cover_full_bbox=True) covers the WHOLE grid bounding
    box, ignoring TIN coverage gaps for tessellation purposes. The TIN was
    sampled to give priority-flood real elevations, but on fragmented sites
    its narrow gaps create visible holes between catchments that aren't
    actually "sides of the surface" (roads, alleys, etc. that do drain to
    inlets). Just covering everything gives clean coherent polygons.

    Cells more than `bbox_margin_cells` away from any TIN cell are still
    excluded — those are the genuine "sides of the surface" outside the
    designed area.

    Returns (n_components_processed, n_assigned_cells, n_excluded_cells).
    Modifies `labels` in place.
    """
    from scipy.ndimage import label as cc_label, binary_dilation
    from scipy.spatial import cKDTree

    rows, cols = labels.shape

    if cover_full_bbox:
        # Compute "near any inlet" = cells whose Euclidean distance to the
        # nearest inlet is below a threshold. Anything farther is genuine
        # off-site (no designed drainage near it). Threshold defaults to
        # the diagonal of the TIN bbox / sqrt(n_inlets) * 4 — generous
        # enough to cover real catchments, small enough to exclude
        # truly-distant exterior.
        inside = np.isfinite(elev)
        if inside.any():
            rs_in, cs_in = np.where(inside)
            site_diag = math.hypot(
                (cs_in.max() - cs_in.min()) * cell_size,
                (rs_in.max() - rs_in.min()) * cell_size,
            )
            avg_radius = site_diag / max(1, math.sqrt(len(inlets)))
            max_dist = max(150.0, 4.0 * avg_radius)
        else:
            max_dist = 1e9

        # Mark every cell as needing a label if within max_dist of an inlet.
        rs_all, cs_all = np.meshgrid(
            np.arange(rows), np.arange(cols), indexing='ij')
        xs_all = origin_x + cs_all * cell_size
        ys_all = origin_y + rs_all * cell_size
        # Use KDTree once over all unlabeled cells.
        unlabeled = labels < 0
        if unlabeled.any():
            inlet_pts_all = np.array(
                [[inl.x, inl.y] for inl in inlets], dtype=np.float64)
            tree_all = cKDTree(inlet_pts_all)
            qpts = np.stack(
                [xs_all[unlabeled], ys_all[unlabeled]], axis=1)
            dists, _ = tree_all.query(qpts)
            near_mask = dists < max_dist
            # Build a boolean grid of "needs_label"
            needs_label = np.zeros_like(unlabeled)
            ulr, ulc = np.where(unlabeled)
            needs_label[ulr[near_mask], ulc[near_mask]] = True
        else:
            needs_label = np.zeros_like(unlabeled)
        excluded = (labels < 0) & ~needs_label
    else:
        needs_label = (labels < 0) & (~outside_hull)
        excluded = outside_hull & (labels < 0)
        if not include_interior_holes:
            inside = np.isfinite(elev)
            needs_label = needs_label & inside

    if not needs_label.any():
        return (0, 0, int(excluded.sum()))

    _, n_cc = cc_label(needs_label)

    inlet_pts = np.array([[inl.x, inl.y] for inl in inlets], dtype=np.float64)
    inlet_ids = np.array([inl.id for inl in inlets], dtype=np.int64)
    tree = cKDTree(inlet_pts)

    rs, cs = np.where(needs_label)
    xs = origin_x + cs * cell_size
    ys = origin_y + rs * cell_size
    query_pts = np.stack([xs, ys], axis=1)
    _, nearest_idx = tree.query(query_pts)
    labels[needs_label] = inlet_ids[nearest_idx]
    n_assigned = int(needs_label.sum())
    return (n_cc, n_assigned, int(excluded.sum()))


def filter_small_components(labels: np.ndarray, min_cells: int
                            ) -> Tuple[int, int]:
    """Per-inlet: find connected components of labelled cells. Components
    smaller than `min_cells` get merged into the dominant adjacent label.
    If no labeled neighbor exists, the component is left as-is (it's a
    genuine tiny isolated catchment). No cells are set to -1 — preserves
    tessellation.
    Returns (final_component_count, n_small_merged_away).
    """
    from scipy.ndimage import label as cc_label, binary_dilation

    merged_total = 0
    for _pass in range(5):
        small_any = False
        unique_ids = sorted(int(v) for v in np.unique(labels) if v >= 0)
        for iid in unique_ids:
            mask = (labels == iid)
            ccs, n = cc_label(mask)
            for cc_id in range(1, n + 1):
                cc_mask = (ccs == cc_id)
                sz = int(cc_mask.sum())
                if sz >= min_cells:
                    continue
                dilated = binary_dilation(cc_mask)
                perim = dilated & ~cc_mask
                neighbor_labels = labels[perim]
                # Include same-iid neighbors (diagonal-only) so the small
                # component just rejoins its parent inlet visually.
                neighbor_labels = neighbor_labels[neighbor_labels >= 0]
                if len(neighbor_labels) == 0:
                    continue  # leave tiny isolated catchment alone
                vals, counts = np.unique(neighbor_labels, return_counts=True)
                new_label = int(vals[counts.argmax()])
                if new_label != iid:
                    labels[cc_mask] = new_label
                    merged_total += 1
                    small_any = True
        if not small_any:
            break

    # Count final components.
    kept_total = 0
    unique_ids = sorted(int(v) for v in np.unique(labels) if v >= 0)
    for iid in unique_ids:
        _, n = cc_label(labels == iid)
        kept_total += n
    return kept_total, merged_total


def component_count_per_inlet(labels: np.ndarray) -> Dict[int, int]:
    """How many connected components does each inlet have?"""
    from scipy.ndimage import label as cc_label
    out: Dict[int, int] = {}
    for iid in sorted(int(v) for v in np.unique(labels) if v >= 0):
        _, n = cc_label(labels == iid)
        out[iid] = n
    return out


def exclude_cells_far_from_inlet(labels: np.ndarray, inlets: List[Inlet],
                                 origin_x: float, origin_y: float,
                                 cell_size: float, max_distance_ft: float
                                 ) -> int:
    """Exclude cells whose Euclidean distance to their assigned inlet
    exceeds `max_distance_ft`. These are cells priority-flood dragged in
    from far away because no closer inlet was reachable. In practice they
    represent "uncovered zones" — undeveloped area, no designed drainage —
    and shouldn't be lumped into the nearest inlet's catchment.

    Returns number of cells excluded.
    """
    rows, cols = labels.shape
    max_d2 = max_distance_ft * max_distance_ft
    inlet_xy = {inl.id: (inl.x, inl.y) for inl in inlets}

    # Precompute cell center coordinates.
    labelled = labels >= 0
    rs, cs = np.where(labelled)
    if len(rs) == 0:
        return 0

    xs = origin_x + cs * cell_size
    ys = origin_y + rs * cell_size
    cell_labels = labels[rs, cs]

    far = np.zeros(len(rs), dtype=bool)
    # Vectorize per-inlet (K inlets, N cells):
    for iid, (ix, iy) in inlet_xy.items():
        mask = cell_labels == iid
        if not mask.any():
            continue
        dx = xs[mask] - ix
        dy = ys[mask] - iy
        d2 = dx * dx + dy * dy
        far_mask = d2 > max_d2
        # Map back to the original index space.
        indices = np.where(mask)[0][far_mask]
        far[indices] = True

    n_excluded = int(far.sum())
    if n_excluded > 0:
        labels[rs[far], cs[far]] = -1
    return n_excluded
