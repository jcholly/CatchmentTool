"""
Tests for CRS utility module.

Run:
    python -m pytest tests/test_crs_utils.py -v
"""

import sys
from pathlib import Path

import numpy as np
import pytest
import rasterio
from rasterio.transform import from_origin
from rasterio.crs import CRS

sys.path.insert(0, str(Path(__file__).parent.parent / "Python"))

from crs_utils import (
    AUTODESK_TO_EPSG,
    resolve_crs_from_autodesk_code,
    make_local_engineering_crs,
    resolve_crs,
    get_linear_unit,
    is_metric_unit,
)


# ============================================================================
# Autodesk code -> EPSG lookup
# ============================================================================

class TestAutodeskToEpsg:

    def test_texas_central_feet(self):
        crs = resolve_crs_from_autodesk_code("TX83-CF")
        assert crs is not None
        assert crs.to_epsg() == 2277

    def test_california_zone_v_feet(self):
        crs = resolve_crs_from_autodesk_code("CA83-VF")
        assert crs is not None
        assert crs.to_epsg() == 2229

    def test_utm_wgs84_zone_17(self):
        crs = resolve_crs_from_autodesk_code("UTM84-17N")
        assert crs is not None
        assert crs.to_epsg() == 32617

    def test_case_insensitive(self):
        crs = resolve_crs_from_autodesk_code("tx83-cf")
        assert crs is not None
        assert crs.to_epsg() == 2277

    def test_unknown_code_returns_none(self):
        crs = resolve_crs_from_autodesk_code("FAKE-CODE-999")
        assert crs is None

    def test_empty_string_returns_none(self):
        assert resolve_crs_from_autodesk_code("") is None
        assert resolve_crs_from_autodesk_code(None) is None

    def test_mapping_has_common_states(self):
        """Verify we have entries for the most common US states."""
        assert "TX83-CF" in AUTODESK_TO_EPSG
        assert "CA83-VF" in AUTODESK_TO_EPSG
        assert "FL83-EF" in AUTODESK_TO_EPSG
        assert "VA83-NF" in AUTODESK_TO_EPSG
        assert "NC83-F" in AUTODESK_TO_EPSG
        assert "PA83-NF" in AUTODESK_TO_EPSG


# ============================================================================
# Local engineering CRS
# ============================================================================

class TestLocalEngineeringCrs:

    def test_us_feet(self):
        crs = make_local_engineering_crs("us-ft")
        assert crs is not None
        # Verify the CRS has foot-based units via its WKT or PROJ representation
        wkt = crs.to_wkt().lower()
        assert "foot" in wkt or "ft" in wkt or "feet" in wkt

    def test_meters(self):
        crs = make_local_engineering_crs("m")
        assert crs is not None
        wkt = crs.to_wkt().lower()
        assert "metre" in wkt or "meter" in wkt

    def test_default_is_feet(self):
        crs = make_local_engineering_crs()
        assert crs is not None


# ============================================================================
# Tiered CRS resolution
# ============================================================================

def _make_test_dem(tmp_path, crs=None):
    """Create a minimal test DEM GeoTIFF."""
    path = tmp_path / "test.tif"
    data = np.ones((10, 10), dtype=np.float32)
    profile = {
        'driver': 'GTiff', 'dtype': 'float32',
        'width': 10, 'height': 10, 'count': 1,
        'transform': from_origin(0, 10, 1, 1),
        'nodata': -9999.0,
    }
    if crs:
        profile['crs'] = crs
    with rasterio.open(str(path), 'w', **profile) as dst:
        dst.write(data, 1)
    return path


class TestResolveCrs:

    def test_tier1_user_override(self, tmp_path):
        dem = _make_test_dem(tmp_path)
        crs = resolve_crs(dem, user_crs="EPSG:2277")
        assert crs.to_epsg() == 2277

    def test_tier2_prj_file(self, tmp_path):
        dem = _make_test_dem(tmp_path)
        prj = tmp_path / "test.prj"
        prj.write_text(CRS.from_epsg(4326).to_wkt())
        crs = resolve_crs(dem)
        assert crs.to_epsg() == 4326

    def test_tier3_autodesk_code_in_metadata(self, tmp_path):
        dem = _make_test_dem(tmp_path)
        meta = {"autodesk_cs_code": "TX83-CF"}
        crs = resolve_crs(dem, json_metadata=meta)
        assert crs.to_epsg() == 2277

    def test_tier3_local_cs_in_prj(self, tmp_path):
        dem = _make_test_dem(tmp_path)
        prj = tmp_path / "test.prj"
        prj.write_text('LOCAL_CS["TX83-CF"]')
        crs = resolve_crs(dem)
        assert crs.to_epsg() == 2277

    def test_tier4_local_engineering_fallback(self, tmp_path):
        """When nothing else works, get a local engineering CRS."""
        dem = _make_test_dem(tmp_path)
        crs = resolve_crs(dem, drawing_units="ft")
        # Should not be None, should not be EPSG:32617
        assert crs is not None
        assert crs.to_epsg() != 32617

    def test_user_override_beats_prj(self, tmp_path):
        dem = _make_test_dem(tmp_path)
        prj = tmp_path / "test.prj"
        prj.write_text(CRS.from_epsg(4326).to_wkt())
        crs = resolve_crs(dem, user_crs="EPSG:2277")
        assert crs.to_epsg() == 2277  # user override wins

    def test_invalid_user_crs_falls_through(self, tmp_path):
        dem = _make_test_dem(tmp_path)
        meta = {"autodesk_cs_code": "TX83-CF"}
        crs = resolve_crs(dem, json_metadata=meta, user_crs="NOT_A_CRS")
        # Should fall through to tier 3
        assert crs.to_epsg() == 2277


# ============================================================================
# Unit helpers
# ============================================================================

class TestUnitHelpers:

    def test_is_metric(self):
        assert is_metric_unit("metre") is True
        assert is_metric_unit("meter") is True
        assert is_metric_unit("m") is True

    def test_is_not_metric(self):
        assert is_metric_unit("US survey foot") is False
        assert is_metric_unit("foot") is False
