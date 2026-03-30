"""
Validation Utilities for Civil 3D Catchment Delineation Tool

Handles:
- Snap distance auto-calibration from DEM cell size
- Pour point extent validation (against DEM bounds)
- Post-snap quality checks
- Unit-aware area threshold conversion
"""

import logging
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import rasterio
import geopandas as gpd
from shapely.geometry import box, Point

logger = logging.getLogger(__name__)


# ============================================================================
# Data classes for structured validation results
# ============================================================================

@dataclass
class SkippedPoint:
    """A pour point that was skipped during validation."""
    index: int
    label: str
    x: float
    y: float
    reason: str


@dataclass
class ValidationResult:
    """Result of pour point validation against DEM extent."""
    valid_indices: list[int] = field(default_factory=list)
    skipped: list[SkippedPoint] = field(default_factory=list)

    @property
    def valid_count(self) -> int:
        return len(self.valid_indices)

    @property
    def skipped_count(self) -> int:
        return len(self.skipped)


@dataclass
class SnapCheckResult:
    """Result of post-snap quality checking."""
    warnings: list[str] = field(default_factory=list)
    collisions: list[tuple[int, int]] = field(default_factory=list)  # pairs of indices that share a cell


# ============================================================================
# Snap distance auto-calibration
# ============================================================================

def compute_snap_distance(dem_path: str | Path, multiplier: float = 5.0) -> float:
    """
    Auto-calibrate snap distance based on DEM cell size.

    WhiteboxTools internally computes search radius as:
        search_radius_cells = floor((snap_dist / cell_x) / 2)

    With multiplier=5: floor((5*cell/cell)/2) = 2 cells -> 5x5 search window.
    With multiplier=3: floor((3*cell/cell)/2) = 1 cell  -> 3x3 window (minimum useful).

    Args:
        dem_path: Path to DEM GeoTIFF/BIL
        multiplier: Number of cell widths for snap distance (default 5.0)

    Returns:
        snap_distance in map units
    """
    with rasterio.open(dem_path) as src:
        cell_x, cell_y = src.res
        cell_size = max(cell_x, cell_y)

    snap_dist = cell_size * multiplier

    # Compute effective search window for logging
    search_radius = int((snap_dist / cell_x) / 2)
    window_size = search_radius * 2 + 1

    logger.info(f"DEM cell size: {cell_size:.2f} map units")
    logger.info(
        f"Auto snap distance: {snap_dist:.2f} map units "
        f"({multiplier}x cell size, "
        f"{window_size}x{window_size} cell search window)"
    )

    # Sanity checks
    if search_radius < 1:
        new_snap = cell_size * 3.0
        logger.warning(
            f"Snap distance {snap_dist:.2f} yields search radius of 0 cells. "
            f"Increasing to {new_snap:.2f} (3x cell size)."
        )
        snap_dist = new_snap
    elif search_radius > 20:
        logger.warning(
            f"Snap distance covers {search_radius} cells — "
            f"risk of snapping pour points to the wrong stream. "
            f"Consider reducing snap_multiplier."
        )

    return snap_dist


# ============================================================================
# Pour point extent validation
# ============================================================================

def validate_pour_points(
    dem_path: str | Path,
    pour_points_gdf: gpd.GeoDataFrame,
    snap_dist: float,
    label_column: str | None = None,
) -> ValidationResult:
    """
    Validate pour points against DEM extent and NoData.

    Checks:
    1. Point falls within DEM bounding box
    2. Point is not within snap_dist of the DEM edge (search window would be truncated)
    3. The DEM cell at the point location is not NoData

    Args:
        dem_path: Path to the DEM raster
        pour_points_gdf: GeoDataFrame of pour points
        snap_dist: Snap distance in map units
        label_column: Column name to use for point labels in warnings

    Returns:
        ValidationResult with valid indices and skipped points
    """
    result = ValidationResult()

    with rasterio.open(dem_path) as src:
        bounds = src.bounds
        nodata = src.nodata

        dem_box = box(bounds.left, bounds.bottom, bounds.right, bounds.top)
        inner_box = box(
            bounds.left + snap_dist,
            bounds.bottom + snap_dist,
            bounds.right - snap_dist,
            bounds.top - snap_dist,
        )

        for idx, row in pour_points_gdf.iterrows():
            pt = row.geometry
            label = _get_point_label(row, idx, label_column)

            # Check 1: Outside DEM extent entirely
            if not dem_box.contains(pt):
                result.skipped.append(SkippedPoint(
                    index=idx, label=label, x=pt.x, y=pt.y,
                    reason="outside DEM extent"
                ))
                continue

            # Check 2: Near edge — warn but still include
            if not inner_box.contains(pt):
                logger.warning(
                    f"Point '{label}' ({pt.x:.2f}, {pt.y:.2f}) is within "
                    f"{snap_dist:.1f} units of DEM edge — snap window may be truncated"
                )

            # Check 3: DEM cell value is NoData
            try:
                row_idx, col_idx = src.index(pt.x, pt.y)
                if 0 <= row_idx < src.height and 0 <= col_idx < src.width:
                    window = rasterio.windows.Window(col_idx, row_idx, 1, 1)
                    value = src.read(1, window=window).flat[0]
                    if nodata is not None and value == nodata:
                        result.skipped.append(SkippedPoint(
                            index=idx, label=label, x=pt.x, y=pt.y,
                            reason="falls on NoData cell in DEM"
                        ))
                        continue
            except Exception:
                # If we can't read the cell, still try to proceed
                pass

            result.valid_indices.append(idx)

    # Report results
    if result.skipped:
        logger.warning(f"{result.skipped_count} pour point(s) skipped:")
        for sp in result.skipped:
            logger.warning(f"  {sp.label}: {sp.reason} ({sp.x:.2f}, {sp.y:.2f})")
    else:
        logger.info(f"All {result.valid_count} pour points are within DEM extent.")

    if result.valid_count == 0:
        raise ValueError(
            "No valid pour points remain after validation. "
            "Check that the DEM covers the pipe network extent. "
            f"DEM bounds: ({bounds.left:.1f}, {bounds.bottom:.1f}) to "
            f"({bounds.right:.1f}, {bounds.top:.1f})"
        )

    return result


# ============================================================================
# Post-snap quality checks
# ============================================================================

def check_snap_results(
    original_gdf: gpd.GeoDataFrame,
    snapped_path: str | Path,
    cell_size: float,
    snap_dist: float,
    label_column: str | None = None,
) -> SnapCheckResult:
    """
    Compare original and snapped pour points to detect issues.

    Checks:
    1. Points that didn't move (may not have reached a flow path)
    2. Points that moved the maximum distance (may have jumped to wrong stream)
    3. Multiple points that snapped to the same cell (collision — last-write-wins in WhiteboxTools)

    Args:
        original_gdf: Original pour points GeoDataFrame
        snapped_path: Path to snapped pour points shapefile
        cell_size: DEM cell size in map units
        snap_dist: Snap distance used
        label_column: Column for labels

    Returns:
        SnapCheckResult with warnings and detected collisions
    """
    result = SnapCheckResult()
    snapped_gdf = gpd.read_file(snapped_path)

    if len(original_gdf) != len(snapped_gdf):
        result.warnings.append(
            f"Point count mismatch: {len(original_gdf)} input vs "
            f"{len(snapped_gdf)} snapped — some points may have been dropped"
        )
        return result

    # Check movement distances
    snapped_cells = {}  # (cell_row, cell_col) -> list of point indices

    for i in range(len(original_gdf)):
        orig_pt = original_gdf.geometry.iloc[i]
        snap_pt = snapped_gdf.geometry.iloc[i]
        label = _get_point_label(original_gdf.iloc[i], i, label_column)

        dx = abs(orig_pt.x - snap_pt.x)
        dy = abs(orig_pt.y - snap_pt.y)
        dist_moved = (dx ** 2 + dy ** 2) ** 0.5

        # Didn't move at all
        if dist_moved < cell_size * 0.01:
            result.warnings.append(
                f"'{label}' did not snap — may not be near a flow path. "
                f"Check DEM coverage and depression filling at ({orig_pt.x:.2f}, {orig_pt.y:.2f})"
            )

        # Moved nearly the full distance
        elif dist_moved > snap_dist * 0.9:
            result.warnings.append(
                f"'{label}' snapped {dist_moved:.1f} units (near max {snap_dist:.1f}). "
                f"May have snapped to wrong stream — consider increasing snap distance."
            )

        # Track cell positions for collision detection
        cell_key = (round(snap_pt.x / cell_size), round(snap_pt.y / cell_size))
        if cell_key not in snapped_cells:
            snapped_cells[cell_key] = []
        snapped_cells[cell_key].append(i)

    # Detect collisions
    for cell_key, indices in snapped_cells.items():
        if len(indices) > 1:
            labels = [
                _get_point_label(original_gdf.iloc[i], i, label_column)
                for i in indices
            ]
            result.collisions.append(tuple(indices))
            result.warnings.append(
                f"COLLISION: Points {', '.join(labels)} snapped to the same cell. "
                f"Only the last one will produce a watershed. "
                f"Consider moving structures apart or reducing snap distance."
            )

    for w in result.warnings:
        logger.warning(w)

    return result


# ============================================================================
# Unit-aware area threshold
# ============================================================================

# Conversion factors to square map units
_AREA_TO_SQ_FT = {
    "acres": 43560.0,
    "sq_ft": 1.0,
    "hectares": 107639.104,  # 1 hectare in sq ft
    "sq_m": 10.7639,
}

_AREA_TO_SQ_M = {
    "acres": 4046.8564,
    "sq_ft": 0.09290304,
    "hectares": 10000.0,
    "sq_m": 1.0,
}


def convert_area_threshold(
    min_area: float,
    area_unit: str,
    drawing_units: str = "ft",
) -> float:
    """
    Convert a minimum catchment area to square drawing units.

    Args:
        min_area: Area value (e.g., 0.5)
        area_unit: Unit of the area value ('acres', 'hectares', 'sq_ft', 'sq_m')
        drawing_units: Drawing linear units ('ft', 'm')

    Returns:
        Area threshold in square drawing units
    """
    is_metric = drawing_units.lower() in ("m", "meters", "metre")
    table = _AREA_TO_SQ_M if is_metric else _AREA_TO_SQ_FT

    if area_unit not in table:
        raise ValueError(
            f"Unknown area unit '{area_unit}'. "
            f"Supported: {list(table.keys())}"
        )

    sq_units = min_area * table[area_unit]
    unit_label = "sq m" if is_metric else "sq ft"
    logger.info(f"Min catchment area: {min_area} {area_unit} = {sq_units:,.1f} {unit_label}")
    return sq_units


# ============================================================================
# Helpers
# ============================================================================

def _get_point_label(row, index: int, label_column: str | None) -> str:
    """Extract a human-readable label for a pour point."""
    if label_column and label_column in row.index:
        val = row[label_column]
        if val is not None and str(val).strip():
            return str(val)

    # Try common column names
    for col in ('name', 'label', 'structure_id', 'struct_nam', 'structure_', 'id'):
        if col in row.index:
            val = row[col]
            if val is not None and str(val).strip():
                return str(val)

    return f"point_{index}"
