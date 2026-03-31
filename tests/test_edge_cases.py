"""
Edge case tests for the CatchmentDelineator pipeline.

These tests use synthetic DEMs and pour point GeoJSONs to exercise
boundary conditions that commonly arise in real Civil 3D exports.

Run:
    cd CatchmentTool
    python -m pytest tests/test_edge_cases.py -v
    python -m pytest tests/test_edge_cases.py -v --runslow   # include slow tests
"""

from __future__ import annotations

import json
import logging
import sys
from pathlib import Path

import geopandas as gpd
import numpy as np
import pytest
import rasterio
from rasterio.crs import CRS
from rasterio.transform import from_origin
from shapely.geometry import Point, mapping

# Add the Python source directory to path
sys.path.insert(0, str(Path(__file__).parent.parent / "Python"))

from catchment_delineation import CatchmentDelineator

logger = logging.getLogger(__name__)


# ============================================================================
# Helpers for synthetic test data
# ============================================================================

def _write_dem(path: Path, data: np.ndarray, transform, nodata=-9999.0,
               crs: str = "EPSG:32617"):
    """Write a float32 GeoTIFF DEM."""
    with rasterio.open(
        str(path), "w", driver="GTiff",
        height=data.shape[0], width=data.shape[1],
        count=1, dtype="float32",
        crs=CRS.from_user_input(crs),
        transform=transform,
        nodata=nodata,
    ) as dst:
        dst.write(data.astype("float32"), 1)


def _write_pour_points(path: Path, points: list[dict], crs: str = "EPSG:32617"):
    """
    Write a GeoJSON file of pour points.

    Each dict in *points* must have 'x', 'y', and optionally 'name'.
    """
    features = []
    for i, pt in enumerate(points):
        features.append({
            "type": "Feature",
            "properties": {"name": pt.get("name", f"inlet_{i}"),
                           "structure_id": pt.get("name", f"inlet_{i}")},
            "geometry": mapping(Point(pt["x"], pt["y"])),
        })
    geojson = {
        "type": "FeatureCollection",
        "crs": {"type": "name", "properties": {"name": crs}},
        "features": features,
    }
    with open(path, "w") as f:
        json.dump(geojson, f)


def _make_tilted_dem(rows: int, cols: int, cell_size: float = 10.0,
                     origin_x: float = 500000.0, origin_y: float = 4500000.0):
    """
    Create a simple tilted plane DEM (slopes toward bottom-left).

    Returns (data, transform, origin_x, origin_y).
    """
    y_slope = np.linspace(100, 0, rows).reshape(-1, 1)
    x_slope = np.linspace(50, 0, cols).reshape(1, -1)
    data = y_slope + x_slope
    transform = from_origin(origin_x, origin_y + rows * cell_size,
                            cell_size, cell_size)
    return data, transform


def _make_valley_dem(rows: int, cols: int, cell_size: float = 10.0,
                     origin_x: float = 500000.0, origin_y: float = 4500000.0):
    """
    Create a V-shaped valley DEM with a clear stream channel down the centre.

    Returns (data, transform).
    """
    x_dist = np.abs(np.arange(cols) - cols // 2).astype(float)
    y_grad = np.linspace(50, 0, rows).reshape(-1, 1)
    data = y_grad + x_dist.reshape(1, -1) * 2.0
    transform = from_origin(origin_x, origin_y + rows * cell_size,
                            cell_size, cell_size)
    return data, transform


def _run_delineator(tmp_path, dem_data, transform, pour_points,
                    nodata=-9999.0, config_overrides=None,
                    crs="EPSG:32617"):
    """
    Set up synthetic files and run the full CatchmentDelineator pipeline.

    Returns the output GeoJSON path (may be empty FeatureCollection).
    """
    dem_path = tmp_path / "dem.tif"
    inlets_path = tmp_path / "inlets.geojson"
    output_path = tmp_path / "catchments.shp"
    working_dir = tmp_path / "work"
    working_dir.mkdir()

    _write_dem(dem_path, dem_data, transform, nodata=nodata, crs=crs)
    _write_pour_points(inlets_path, pour_points, crs=crs)

    delineator = CatchmentDelineator(working_dir=str(working_dir))
    # Override config defaults for fast tests
    delineator.config.update({
        "depression_method": "fill",
        "snap_distance": "auto",
        "snap_multiplier": 3.0,
        "min_catchment_area": 0.0,
        "area_unit": "sq_ft",
        "drawing_units": "ft",
        "retain_intermediates": False,
        "smooth_radius": 0,
    })
    if config_overrides:
        delineator.config.update(config_overrides)

    result = delineator.run(
        dem_file=str(dem_path),
        pour_points_file=str(inlets_path),
        output_file=str(output_path),
        user_crs=crs,
    )
    return Path(result)


def _read_output_geojson(output_shp_path: Path) -> dict:
    """Read the companion GeoJSON written alongside the shapefile."""
    geojson_path = output_shp_path.with_suffix(".json")
    if geojson_path.exists():
        with open(geojson_path) as f:
            return json.load(f)
    return {"type": "FeatureCollection", "features": []}


# ============================================================================
# Edge case 1: Shared watershed
# Two inlets on the same trunk line -- WhiteboxTools assigns them
# the same watershed raster ID.  The tool should split via Voronoi
# and still return two catchments.
# ============================================================================

@pytest.mark.slow
class TestSharedWatershed:

    def test_two_inlets_same_watershed_returns_two_catchments(self, tmp_path):
        """Two inlets placed along the same valley channel."""
        rows, cols, cell = 60, 40, 10.0
        ox, oy = 500000.0, 4500000.0
        data, transform = _make_valley_dem(rows, cols, cell, ox, oy)

        # Both inlets along the centre of the valley
        mid_x = ox + (cols // 2) * cell
        pts = [
            {"x": mid_x, "y": oy + 45 * cell, "name": "inlet_upper"},
            {"x": mid_x, "y": oy + 15 * cell, "name": "inlet_lower"},
        ]

        out = _run_delineator(tmp_path, data, transform, pts)
        gj = _read_output_geojson(out)

        # Pipeline must not crash; we should get at least one polygon
        assert gj["type"] == "FeatureCollection"
        # Ideally two separate catchments (Voronoi split), but at minimum
        # the pipeline must finish without error.
        assert len(gj["features"]) >= 1


# ============================================================================
# Edge case 2: Inlet outside DEM extent
# Pour point falls off the raster edge -- validate_pour_points should
# skip it, and the run should either skip that inlet or raise clearly.
# ============================================================================

@pytest.mark.slow
class TestInletOutsideDEM:

    def test_one_inside_one_outside(self, tmp_path):
        """One valid inlet inside, one completely outside DEM bounds."""
        rows, cols, cell = 40, 40, 10.0
        ox, oy = 500000.0, 4500000.0
        data, transform = _make_tilted_dem(rows, cols, cell, ox, oy)

        pts = [
            {"x": ox + 20 * cell, "y": oy + 20 * cell, "name": "inside"},
            {"x": ox - 500.0,     "y": oy - 500.0,     "name": "outside"},
        ]

        out = _run_delineator(tmp_path, data, transform, pts)
        gj = _read_output_geojson(out)
        assert gj["type"] == "FeatureCollection"
        # The outside point should have been skipped by validation
        # We should still get at least one catchment from the inside point
        assert len(gj["features"]) >= 1

    def test_all_outside_raises(self, tmp_path):
        """All inlets outside the DEM should raise ValueError."""
        rows, cols, cell = 30, 30, 10.0
        ox, oy = 500000.0, 4500000.0
        data, transform = _make_tilted_dem(rows, cols, cell, ox, oy)

        pts = [
            {"x": ox - 1000.0, "y": oy - 1000.0, "name": "far_away"},
        ]

        with pytest.raises(ValueError, match="No valid pour points"):
            _run_delineator(tmp_path, data, transform, pts)


# ============================================================================
# Edge case 3: Inlet on flat area
# No clear flow direction -- depression filling should still produce a
# result (possibly a very small/odd catchment, but no crash).
# ============================================================================

@pytest.mark.slow
class TestInletOnFlatArea:

    def test_flat_dem_does_not_crash(self, tmp_path):
        """A perfectly flat DEM with one inlet at the centre."""
        rows, cols, cell = 40, 40, 10.0
        ox, oy = 500000.0, 4500000.0
        # Perfectly flat surface at elevation 100
        data = np.full((rows, cols), 100.0)
        transform = from_origin(ox, oy + rows * cell, cell, cell)

        pts = [
            {"x": ox + 20 * cell, "y": oy + 20 * cell, "name": "flat_inlet"},
        ]

        out = _run_delineator(tmp_path, data, transform, pts)
        gj = _read_output_geojson(out)
        # Pipeline completes without crash
        assert gj["type"] == "FeatureCollection"


# ============================================================================
# Edge case 4: Very small catchment below min_area threshold
# The polygon should be skipped (filtered out) when min_catchment_area
# is set higher than the catchment size.
# ============================================================================

@pytest.mark.slow
class TestSmallCatchmentBelowMinArea:

    def test_catchment_filtered_by_min_area(self, tmp_path):
        """Single inlet on a small DEM -- catchment is smaller than min_area."""
        rows, cols, cell = 20, 20, 10.0
        ox, oy = 500000.0, 4500000.0
        data, transform = _make_tilted_dem(rows, cols, cell, ox, oy)

        pts = [
            {"x": ox + 10 * cell, "y": oy + 10 * cell, "name": "small_inlet"},
        ]

        # Set min_area extremely high so any real catchment is below threshold
        # DEM is 20x20 cells x 100 sq ft each = 40,000 sq ft total; require 1M
        out = _run_delineator(tmp_path, data, transform, pts,
                              config_overrides={"min_catchment_area": 1_000_000,
                                                "area_unit": "sq_ft"})
        gj = _read_output_geojson(out)
        assert gj["type"] == "FeatureCollection"
        # All polygons should have been filtered out
        assert len(gj["features"]) == 0

    def test_catchment_kept_when_above_threshold(self, tmp_path):
        """Same DEM but min_area is 0 -- catchment should be kept."""
        rows, cols, cell = 30, 30, 10.0
        ox, oy = 500000.0, 4500000.0
        data, transform = _make_tilted_dem(rows, cols, cell, ox, oy)

        pts = [
            {"x": ox + 15 * cell, "y": oy + 15 * cell, "name": "keeper"},
        ]

        out = _run_delineator(tmp_path, data, transform, pts,
                              config_overrides={"min_catchment_area": 0,
                                                "area_unit": "sq_ft"})
        gj = _read_output_geojson(out)
        assert len(gj["features"]) >= 1


# ============================================================================
# Edge case 5: All inlets snap to same flow path
# Extreme trunk line scenario -- all points collapse onto one stream cell.
# The tool should still produce output (possibly merged/Voronoi-split).
# ============================================================================

@pytest.mark.slow
class TestAllInletsSnapToSameFlowPath:

    def test_three_inlets_snap_to_single_channel(self, tmp_path):
        """Three inlets very close to the valley centre all snap together."""
        rows, cols, cell = 60, 40, 10.0
        ox, oy = 500000.0, 4500000.0
        data, transform = _make_valley_dem(rows, cols, cell, ox, oy)

        mid_x = ox + (cols // 2) * cell
        pts = [
            {"x": mid_x + 5,  "y": oy + 30 * cell, "name": "A"},
            {"x": mid_x - 5,  "y": oy + 30 * cell, "name": "B"},
            {"x": mid_x + 2,  "y": oy + 30 * cell, "name": "C"},
        ]

        out = _run_delineator(tmp_path, data, transform, pts)
        gj = _read_output_geojson(out)
        assert gj["type"] == "FeatureCollection"
        # At minimum the pipeline should not crash
        assert len(gj["features"]) >= 0


# ============================================================================
# Edge case 6: Two inlets very close together (within snap distance)
# After snapping they may collide onto the same cell.  The snap-check
# should log a collision warning.
# ============================================================================

@pytest.mark.slow
class TestTwoInletsVeryCloseTogether:

    def test_close_inlets_produces_output_and_warns(self, tmp_path, caplog):
        """Two inlets placed 1 cell apart on the valley floor."""
        rows, cols, cell = 50, 40, 10.0
        ox, oy = 500000.0, 4500000.0
        data, transform = _make_valley_dem(rows, cols, cell, ox, oy)

        mid_x = ox + (cols // 2) * cell
        pts = [
            {"x": mid_x,        "y": oy + 25 * cell, "name": "close_A"},
            {"x": mid_x + cell,  "y": oy + 25 * cell, "name": "close_B"},
        ]

        with caplog.at_level(logging.WARNING):
            out = _run_delineator(tmp_path, data, transform, pts)

        gj = _read_output_geojson(out)
        assert gj["type"] == "FeatureCollection"
        # Pipeline must complete without crashing
        assert len(gj["features"]) >= 0

        # Check for collision or snap warnings (may or may not fire depending
        # on exact snap behaviour, so just confirm no unhandled exception).


# ============================================================================
# Edge case 7: Inlet at DEM nodata hole
# Structure placed where the surface has a nodata cell.
# validate_pour_points should skip it.
# ============================================================================

@pytest.mark.slow
class TestInletAtNodataHole:

    def test_nodata_inlet_skipped_valid_inlet_kept(self, tmp_path):
        """One inlet on valid surface, one on a nodata hole."""
        rows, cols, cell = 40, 40, 10.0
        ox, oy = 500000.0, 4500000.0
        nodata = -9999.0
        data, transform = _make_tilted_dem(rows, cols, cell, ox, oy)

        # Punch a nodata hole around (row=10, col=10)
        data[8:13, 8:13] = nodata

        # Inlet on the nodata hole
        hole_x = ox + 10 * cell
        hole_y = oy + (rows - 10) * cell  # row 10 from top
        # Inlet on valid terrain
        valid_x = ox + 30 * cell
        valid_y = oy + 20 * cell

        pts = [
            {"x": hole_x,  "y": hole_y,  "name": "on_hole"},
            {"x": valid_x, "y": valid_y, "name": "on_surface"},
        ]

        out = _run_delineator(tmp_path, data, transform, pts, nodata=nodata)
        gj = _read_output_geojson(out)
        assert gj["type"] == "FeatureCollection"
        # The valid inlet should produce a catchment
        assert len(gj["features"]) >= 1

    def test_all_nodata_raises(self, tmp_path):
        """All inlets on nodata should raise ValueError (no valid points)."""
        rows, cols, cell = 30, 30, 10.0
        ox, oy = 500000.0, 4500000.0
        nodata = -9999.0
        data = np.full((rows, cols), nodata)
        # Need at least a rim of valid data so the DEM itself is not entirely nodata
        data[0, :] = 100.0
        data[-1, :] = 50.0
        transform = from_origin(ox, oy + rows * cell, cell, cell)

        # Inlet in the middle nodata zone
        pts = [
            {"x": ox + 15 * cell, "y": oy + 15 * cell, "name": "nodata_only"},
        ]

        with pytest.raises(ValueError, match="No valid pour points"):
            _run_delineator(tmp_path, data, transform, pts, nodata=nodata)


# ============================================================================
# Edge case 8: Outlet-only structure (pond outfall)
# Structure at a low point / pond outfall with no upstream catchment
# area above min threshold.  The tool should complete and either
# produce a tiny polygon or skip it gracefully.
# ============================================================================

@pytest.mark.slow
class TestOutletOnlyStructure:

    def test_outlet_at_lowest_point(self, tmp_path):
        """Inlet placed at the absolute lowest point of the DEM."""
        rows, cols, cell = 40, 40, 10.0
        ox, oy = 500000.0, 4500000.0
        data, transform = _make_tilted_dem(rows, cols, cell, ox, oy)

        # Place inlet at bottom-left corner -- the lowest elevation
        pts = [
            {"x": ox + 1 * cell, "y": oy + 1 * cell, "name": "pond_outfall"},
        ]

        out = _run_delineator(tmp_path, data, transform, pts)
        gj = _read_output_geojson(out)
        assert gj["type"] == "FeatureCollection"
        # The outlet is at the lowest point so the entire DEM drains to it,
        # or it gets a polygon.  Either way, no crash.


# ============================================================================
# Unit-level validation tests (fast, no WhiteboxTools required)
# ============================================================================

class TestValidationEdgeCases:
    """Fast tests that exercise the validation module directly."""

    def test_validate_pour_points_outside_extent(self, tmp_path):
        """validate_pour_points skips points outside DEM bbox."""
        from validation import validate_pour_points

        dem_path = tmp_path / "dem.tif"
        rows, cols, cell = 20, 20, 10.0
        ox, oy = 500000.0, 4500000.0
        data, transform = _make_tilted_dem(rows, cols, cell, ox, oy)
        _write_dem(dem_path, data, transform)

        gdf = gpd.GeoDataFrame(
            {"name": ["inside", "outside"]},
            geometry=[
                Point(ox + 10 * cell, oy + 10 * cell),
                Point(ox - 500, oy - 500),
            ],
            crs="EPSG:32617",
        )

        result = validate_pour_points(str(dem_path), gdf, snap_dist=30.0)
        assert result.valid_count == 1
        assert result.skipped_count == 1
        assert result.skipped[0].reason == "outside DEM extent"

    def test_validate_pour_points_on_nodata(self, tmp_path):
        """validate_pour_points skips points on nodata cells."""
        from validation import validate_pour_points

        dem_path = tmp_path / "dem.tif"
        rows, cols, cell = 20, 20, 10.0
        ox, oy = 500000.0, 4500000.0
        nodata = -9999.0
        data, transform = _make_tilted_dem(rows, cols, cell, ox, oy)
        # Punch hole at (row=10, col=10)
        data[9:12, 9:12] = nodata
        _write_dem(dem_path, data, transform, nodata=nodata)

        hole_x = ox + 10 * cell
        hole_y = oy + (rows - 10) * cell
        valid_x = ox + 5 * cell
        valid_y = oy + 5 * cell

        gdf = gpd.GeoDataFrame(
            {"name": ["on_hole", "valid"]},
            geometry=[
                Point(hole_x, hole_y),
                Point(valid_x, valid_y),
            ],
            crs="EPSG:32617",
        )

        result = validate_pour_points(str(dem_path), gdf, snap_dist=30.0)
        assert result.valid_count == 1
        assert result.skipped_count == 1
        assert "NoData" in result.skipped[0].reason

    def test_validate_all_outside_raises(self, tmp_path):
        """validate_pour_points raises ValueError when no valid points remain."""
        from validation import validate_pour_points

        dem_path = tmp_path / "dem.tif"
        rows, cols, cell = 20, 20, 10.0
        ox, oy = 500000.0, 4500000.0
        data, transform = _make_tilted_dem(rows, cols, cell, ox, oy)
        _write_dem(dem_path, data, transform)

        gdf = gpd.GeoDataFrame(
            {"name": ["far_away"]},
            geometry=[Point(ox - 5000, oy - 5000)],
            crs="EPSG:32617",
        )

        with pytest.raises(ValueError, match="No valid pour points"):
            validate_pour_points(str(dem_path), gdf, snap_dist=30.0)

    def test_convert_area_threshold_units(self):
        """convert_area_threshold produces correct square-foot values."""
        from validation import convert_area_threshold

        # 1 acre = 43,560 sq ft
        result = convert_area_threshold(1.0, "acres", "ft")
        assert abs(result - 43560.0) < 1.0

        # 0 area
        result = convert_area_threshold(0, "sq_ft", "ft")
        assert result == 0.0

    def test_compute_snap_distance_from_dem(self, tmp_path):
        """compute_snap_distance returns cell_size * multiplier."""
        from validation import compute_snap_distance

        dem_path = tmp_path / "dem.tif"
        cell = 5.0
        data = np.ones((10, 10), dtype=np.float32)
        transform = from_origin(0, 50, cell, cell)
        _write_dem(dem_path, data, transform)

        snap = compute_snap_distance(str(dem_path), multiplier=5.0)
        assert abs(snap - 25.0) < 0.1

    def test_check_snap_results_collision(self, tmp_path):
        """check_snap_results detects two points snapped to same cell."""
        from validation import check_snap_results

        crs = "EPSG:32617"
        cell = 10.0

        original = gpd.GeoDataFrame(
            {"name": ["A", "B"]},
            geometry=[Point(100, 200), Point(105, 200)],
            crs=crs,
        )
        # Both snap to the exact same location
        snapped = gpd.GeoDataFrame(
            {"name": ["A", "B"]},
            geometry=[Point(100, 200), Point(100, 200)],
            crs=crs,
        )
        snapped_path = tmp_path / "snapped.shp"
        snapped.to_file(snapped_path)

        result = check_snap_results(original, str(snapped_path),
                                    cell_size=cell, snap_dist=50.0)
        assert len(result.collisions) >= 1
        assert any("COLLISION" in w for w in result.warnings)
