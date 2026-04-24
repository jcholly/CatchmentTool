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
