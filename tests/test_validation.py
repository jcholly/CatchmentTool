"""
Tests for validation utility module.

Run:
    python -m pytest tests/test_validation.py -v
"""

import sys
from pathlib import Path

import numpy as np
import pytest
import rasterio
from rasterio.transform import from_origin
import geopandas as gpd
from shapely.geometry import Point

sys.path.insert(0, str(Path(__file__).parent.parent / "Python"))

from validation import (
    compute_snap_distance,
    validate_pour_points,
    check_snap_results,
    convert_area_threshold,
)


# ============================================================================
# Helpers
# ============================================================================

def _make_dem(tmp_path, rows=100, cols=100, cell_size=1.0, nodata=-9999):
    """Create a test DEM with a north-to-south slope."""
    path = tmp_path / "dem.tif"
    data = np.linspace(100, 0, rows).reshape(-1, 1).repeat(cols, axis=1).astype(np.float32)
    # Add some nodata cells
    data[0:5, 0:5] = nodata

    with rasterio.open(
        str(path), 'w', driver='GTiff', dtype='float32',
        width=cols, height=rows, count=1,
        transform=from_origin(0, rows * cell_size, cell_size, cell_size),
        nodata=nodata,
    ) as dst:
        dst.write(data, 1)
    return path


def _make_points(coords):
    """Create a GeoDataFrame of points from (x, y) tuples."""
    return gpd.GeoDataFrame(
        {'name': [f'Inlet_{i}' for i in range(len(coords))]},
        geometry=[Point(x, y) for x, y in coords],
    )


# ============================================================================
# Snap distance auto-calibration
# ============================================================================

class TestSnapDistance:

    def test_default_multiplier(self, tmp_path):
        dem = _make_dem(tmp_path, cell_size=1.0)
        dist = compute_snap_distance(str(dem), multiplier=5.0)
        assert dist == pytest.approx(5.0)

    def test_larger_cells(self, tmp_path):
        dem = _make_dem(tmp_path, cell_size=3.0)
        dist = compute_snap_distance(str(dem), multiplier=5.0)
        assert dist == pytest.approx(15.0)

    def test_very_coarse_dem_auto_adjusts(self, tmp_path):
        """With 30m cells and multiplier=1, should auto-increase to 3x."""
        dem = _make_dem(tmp_path, rows=10, cols=10, cell_size=30.0)
        dist = compute_snap_distance(str(dem), multiplier=1.0)
        # multiplier=1 gives snap_dist=30, search_radius=floor(30/30/2)=0
        # Should auto-increase to 3x = 90
        assert dist >= 30.0 * 3.0

    def test_fine_resolution(self, tmp_path):
        dem = _make_dem(tmp_path, cell_size=0.3)
        dist = compute_snap_distance(str(dem), multiplier=5.0)
        assert dist == pytest.approx(1.5)


# ============================================================================
# Pour point validation
# ============================================================================

class TestPourPointValidation:

    def test_all_points_inside(self, tmp_path):
        dem = _make_dem(tmp_path, rows=100, cols=100, cell_size=1.0)
        pts = _make_points([(50, 50), (25, 75), (75, 25)])
        result = validate_pour_points(str(dem), pts, snap_dist=5.0)
        assert result.valid_count == 3
        assert result.skipped_count == 0

    def test_point_outside_dem(self, tmp_path):
        dem = _make_dem(tmp_path, rows=100, cols=100, cell_size=1.0)
        pts = _make_points([(50, 50), (200, 200)])  # second point is outside
        result = validate_pour_points(str(dem), pts, snap_dist=5.0)
        assert result.valid_count == 1
        assert result.skipped_count == 1
        assert result.skipped[0].reason == "outside DEM extent"

    def test_point_on_nodata(self, tmp_path):
        dem = _make_dem(tmp_path, rows=100, cols=100, cell_size=1.0)
        # NoData is at rows 0-4, cols 0-4 -> y=96-100, x=0-4
        # Include a valid point so we don't trigger the "zero valid" error
        pts = _make_points([(2, 98), (50, 50)])
        result = validate_pour_points(str(dem), pts, snap_dist=5.0)
        assert result.skipped_count == 1
        assert "NoData" in result.skipped[0].reason
        assert result.valid_count == 1

    def test_all_points_outside_raises(self, tmp_path):
        dem = _make_dem(tmp_path, rows=10, cols=10, cell_size=1.0)
        pts = _make_points([(999, 999)])
        with pytest.raises(ValueError, match="No valid pour points"):
            validate_pour_points(str(dem), pts, snap_dist=5.0)


# ============================================================================
# Area threshold conversion
# ============================================================================

class TestAreaConversion:

    def test_acres_to_sq_ft(self):
        result = convert_area_threshold(1.0, "acres", "ft")
        assert result == pytest.approx(43560.0)

    def test_hectares_to_sq_m(self):
        result = convert_area_threshold(1.0, "hectares", "m")
        assert result == pytest.approx(10000.0)

    def test_sq_ft_passthrough(self):
        result = convert_area_threshold(500.0, "sq_ft", "ft")
        assert result == pytest.approx(500.0)

    def test_half_acre_default(self):
        result = convert_area_threshold(0.5, "acres", "ft")
        assert result == pytest.approx(21780.0)

    def test_invalid_unit_raises(self):
        with pytest.raises(ValueError, match="Unknown area unit"):
            convert_area_threshold(1.0, "furlongs", "ft")

    def test_acres_to_sq_m(self):
        result = convert_area_threshold(1.0, "acres", "m")
        assert result == pytest.approx(4046.8564)


# ============================================================================
# Post-snap quality checks
# ============================================================================

class TestSnapChecks:

    def test_collision_detected(self, tmp_path):
        """Two points snapping to the same cell should be flagged."""
        # Original points far apart
        orig = _make_points([(10, 10), (20, 20)])

        # Snapped points at same location
        snapped = _make_points([(15, 15), (15, 15)])
        snapped_path = tmp_path / "snapped.shp"
        snapped.to_file(str(snapped_path))

        result = check_snap_results(orig, str(snapped_path), cell_size=1.0, snap_dist=10.0)
        assert len(result.collisions) > 0
        assert any("COLLISION" in w for w in result.warnings)

    def test_no_movement_warned(self, tmp_path):
        """A point that didn't move should be warned about."""
        orig = _make_points([(50, 50)])
        snapped = _make_points([(50, 50)])
        snapped_path = tmp_path / "snapped.shp"
        snapped.to_file(str(snapped_path))

        result = check_snap_results(orig, str(snapped_path), cell_size=1.0, snap_dist=10.0)
        assert any("did not snap" in w for w in result.warnings)

    def test_max_distance_warned(self, tmp_path):
        """A point that moved the full snap distance should be warned."""
        orig = _make_points([(50, 50)])
        # Moved ~9.9 units with snap_dist=10
        snapped = _make_points([(57, 57)])
        snapped_path = tmp_path / "snapped.shp"
        snapped.to_file(str(snapped_path))

        result = check_snap_results(orig, str(snapped_path), cell_size=1.0, snap_dist=10.0)
        assert any("near max" in w for w in result.warnings)

    def test_clean_snap(self, tmp_path):
        """A normal snap should produce no warnings."""
        orig = _make_points([(50, 50)])
        snapped = _make_points([(52, 52)])  # moved ~2.8 units
        snapped_path = tmp_path / "snapped.shp"
        snapped.to_file(str(snapped_path))

        result = check_snap_results(orig, str(snapped_path), cell_size=1.0, snap_dist=10.0)
        assert len(result.warnings) == 0
        assert len(result.collisions) == 0
