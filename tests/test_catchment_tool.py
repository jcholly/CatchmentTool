"""
Terminal-runnable tests for CatchmentTool Python components.

Run:
    cd CatchmentTool
    python -m pytest tests/test_catchment_tool.py -v
    python -m pytest tests/test_catchment_tool.py -v -k "stress"   # stress tests only
    python -m pytest tests/test_catchment_tool.py -v -k "edge"     # edge cases only
"""

from __future__ import annotations

import json
import os
import struct
import sys
import tempfile
import time
from pathlib import Path

import numpy as np
import pytest

# Add the Python source directory to path
sys.path.insert(0, str(Path(__file__).parent.parent / "Python"))

from dem_utils import read_bil_header, bil_to_geotiff, geojson_to_shapefile, prepare_inputs


# ============================================================================
# Helpers — synthetic test data generators
# ============================================================================

def make_bil(directory: Path, rows=100, cols=100, cell_size=1.0,
             origin_x=0.0, origin_y=100.0, nodata=-9999,
             surface="flat", name="surface"):
    """Create a synthetic BIL + HDR + optional PRJ for testing."""
    if surface == "flat":
        data = np.full((rows, cols), 50.0, dtype=np.float32)
    elif surface == "slope_ns":
        # North-to-south slope (row 0 = high, row N = low)
        data = np.linspace(100, 0, rows).reshape(-1, 1).repeat(cols, axis=1).astype(np.float32)
    elif surface == "valley":
        # V-shaped valley running north-south in the center
        x = np.abs(np.linspace(-1, 1, cols))
        y = np.linspace(1, 0, rows)
        data = (x[None, :] * 50 + y[:, None] * 20).astype(np.float32)
    elif surface == "bowl":
        # Bowl shape — low point in center
        cx, cy = cols // 2, rows // 2
        yy, xx = np.mgrid[0:rows, 0:cols]
        data = (((xx - cx) ** 2 + (yy - cy) ** 2) ** 0.5).astype(np.float32)
    elif surface == "nodata_holes":
        data = np.full((rows, cols), 50.0, dtype=np.float32)
        data[rows//4:rows//2, cols//4:cols//2] = nodata
    else:
        data = np.random.uniform(0, 100, (rows, cols)).astype(np.float32)

    bil_path = directory / f"{name}.bil"
    hdr_path = directory / f"{name}.hdr"

    data.tofile(str(bil_path))

    hdr_content = (
        f"NROWS {rows}\n"
        f"NCOLS {cols}\n"
        f"NBANDS 1\n"
        f"NBITS 32\n"
        f"BYTEORDER I\n"
        f"LAYOUT BIL\n"
        f"PIXELTYPE FLOAT\n"
        f"ULXMAP {origin_x + cell_size / 2}\n"
        f"ULYMAP {origin_y - cell_size / 2}\n"
        f"XDIM {cell_size}\n"
        f"YDIM {cell_size}\n"
        f"NODATA {nodata}\n"
    )
    hdr_path.write_text(hdr_content)

    return bil_path


def make_pour_points_geojson(directory: Path, points: list[tuple[float, float]],
                              name="structures"):
    """Create a GeoJSON file with point features for pour points."""
    features = []
    for i, (x, y) in enumerate(points):
        features.append({
            "type": "Feature",
            "properties": {"id": i, "label": f"Inlet_{i}"},
            "geometry": {"type": "Point", "coordinates": [x, y]}
        })

    geojson = {"type": "FeatureCollection", "features": features}
    path = directory / f"{name}.json"
    path.write_text(json.dumps(geojson))
    return path


# ============================================================================
# BIL header parsing
# ============================================================================

class TestBilHeaderParsing:
    """Verify BIL header parsing handles various formats."""

    def test_standard_header(self, tmp_path):
        hdr = tmp_path / "test.hdr"
        hdr.write_text("NROWS 256\nNCOLS 512\nNBITS 32\nULXMAP 100.5\nULYMAP 200.5\nXDIM 1.0\nYDIM 1.0\nNODATA -9999\n")
        result = read_bil_header(str(hdr))
        assert result["NROWS"] == 256
        assert result["NCOLS"] == 512
        assert result["ULXMAP"] == 100.5
        assert result["NODATA"] == -9999

    def test_header_case_insensitive_keys(self, tmp_path):
        """BIL headers from different tools may use different casing."""
        hdr = tmp_path / "test.hdr"
        hdr.write_text("nrows 100\nncols 200\nxdim 2.5\nydim 2.5\nulxmap 0\nulymap 0\n")
        result = read_bil_header(str(hdr))
        assert result["NROWS"] == 100
        assert result["NCOLS"] == 200

    def test_header_extra_whitespace(self, tmp_path):
        hdr = tmp_path / "test.hdr"
        hdr.write_text("NROWS   100\nNCOLS   200\n  \nXDIM  1.0\nYDIM  1.0\nULXMAP  50.0\nULYMAP  50.0\n")
        result = read_bil_header(str(hdr))
        assert result["NROWS"] == 100

    def test_header_missing_nodata(self, tmp_path):
        hdr = tmp_path / "test.hdr"
        hdr.write_text("NROWS 10\nNCOLS 10\nXDIM 1\nYDIM 1\nULXMAP 0\nULYMAP 0\n")
        result = read_bil_header(str(hdr))
        assert "NODATA" not in result


# ============================================================================
# BIL to GeoTIFF conversion
# ============================================================================

class TestBilToGeotiff:
    """Test BIL-to-GeoTIFF conversion with various DEM shapes and sizes."""

    def test_basic_conversion(self, tmp_path):
        bil = make_bil(tmp_path, rows=50, cols=50, surface="flat")
        out = str(tmp_path / "out.tif")
        result = bil_to_geotiff(str(bil), out)
        assert Path(result).exists()

    def test_conversion_preserves_dimensions(self, tmp_path):
        import rasterio
        bil = make_bil(tmp_path, rows=75, cols=120, surface="slope_ns")
        out = str(tmp_path / "out.tif")
        bil_to_geotiff(str(bil), out)
        with rasterio.open(out) as ds:
            assert ds.height == 75
            assert ds.width == 120

    def test_conversion_preserves_values(self, tmp_path):
        import rasterio
        bil = make_bil(tmp_path, rows=10, cols=10, surface="flat")
        out = str(tmp_path / "out.tif")
        bil_to_geotiff(str(bil), out)
        with rasterio.open(out) as ds:
            data = ds.read(1)
            assert np.allclose(data, 50.0)

    def test_conversion_with_crs(self, tmp_path):
        import rasterio
        bil = make_bil(tmp_path, rows=20, cols=20)
        out = str(tmp_path / "out.tif")
        bil_to_geotiff(str(bil), out, crs="EPSG:2277")
        with rasterio.open(out) as ds:
            assert ds.crs is not None
            assert ds.crs.to_epsg() == 2277

    def test_conversion_with_prj_file(self, tmp_path):
        import rasterio
        from rasterio.crs import CRS
        bil = make_bil(tmp_path, rows=20, cols=20)
        prj = tmp_path / "surface.prj"
        prj.write_text(CRS.from_epsg(4326).to_wkt())
        out = str(tmp_path / "out.tif")
        bil_to_geotiff(str(bil), out)
        with rasterio.open(out) as ds:
            assert ds.crs is not None

    def test_nodata_preserved(self, tmp_path):
        import rasterio
        bil = make_bil(tmp_path, rows=30, cols=30, surface="nodata_holes", nodata=-9999)
        out = str(tmp_path / "out.tif")
        bil_to_geotiff(str(bil), out)
        with rasterio.open(out) as ds:
            assert ds.nodata == -9999
            data = ds.read(1)
            assert np.any(data == -9999)

    def test_missing_hdr_raises(self, tmp_path):
        bil = tmp_path / "nohdr.bil"
        np.zeros((10, 10), dtype=np.float32).tofile(str(bil))
        with pytest.raises(FileNotFoundError):
            bil_to_geotiff(str(bil), str(tmp_path / "out.tif"))

    def test_missing_bil_raises(self, tmp_path):
        with pytest.raises(Exception):
            bil_to_geotiff(str(tmp_path / "nonexistent.bil"), str(tmp_path / "out.tif"))


# ============================================================================
# Edge cases — degenerate inputs
# ============================================================================

class TestEdgeCases:
    """Edge cases that could come from Civil 3D exports."""

    def test_single_pixel_dem(self, tmp_path):
        """A 1x1 DEM — degenerate but should not crash."""
        bil = make_bil(tmp_path, rows=1, cols=1)
        out = str(tmp_path / "out.tif")
        result = bil_to_geotiff(str(bil), out)
        assert Path(result).exists()

    def test_single_row_dem(self, tmp_path):
        bil = make_bil(tmp_path, rows=1, cols=100)
        out = str(tmp_path / "out.tif")
        result = bil_to_geotiff(str(bil), out)
        assert Path(result).exists()

    def test_single_column_dem(self, tmp_path):
        bil = make_bil(tmp_path, rows=100, cols=1)
        out = str(tmp_path / "out.tif")
        result = bil_to_geotiff(str(bil), out)
        assert Path(result).exists()

    def test_all_nodata_dem(self, tmp_path):
        """Entire DEM is NoData — should still convert without error."""
        data = np.full((20, 20), -9999, dtype=np.float32)
        bil_path = tmp_path / "allnodata.bil"
        data.tofile(str(bil_path))
        hdr = tmp_path / "allnodata.hdr"
        hdr.write_text("NROWS 20\nNCOLS 20\nNBITS 32\nULXMAP 0.5\nULYMAP 19.5\nXDIM 1\nYDIM 1\nNODATA -9999\n")
        out = str(tmp_path / "out.tif")
        result = bil_to_geotiff(str(bil_path), out)
        assert Path(result).exists()

    def test_large_coordinate_offsets(self, tmp_path):
        """State plane coordinates can be very large numbers."""
        import rasterio
        bil = make_bil(tmp_path, rows=10, cols=10,
                       origin_x=2_500_000.0, origin_y=7_000_000.0, cell_size=3.0)
        out = str(tmp_path / "out.tif")
        bil_to_geotiff(str(bil), out, crs="EPSG:2277")
        with rasterio.open(out) as ds:
            bounds = ds.bounds
            assert bounds.left > 2_000_000

    def test_very_small_cell_size(self, tmp_path):
        """LiDAR data can have sub-foot cell sizes."""
        bil = make_bil(tmp_path, rows=50, cols=50, cell_size=0.1)
        out = str(tmp_path / "out.tif")
        result = bil_to_geotiff(str(bil), out)
        assert Path(result).exists()

    def test_very_large_cell_size(self, tmp_path):
        """Coarse DEM with 30m cells."""
        bil = make_bil(tmp_path, rows=20, cols=20, cell_size=30.0)
        out = str(tmp_path / "out.tif")
        result = bil_to_geotiff(str(bil), out)
        assert Path(result).exists()


# ============================================================================
# GeoJSON to Shapefile conversion
# ============================================================================

class TestGeojsonToShapefile:

    def test_basic_conversion(self, tmp_path):
        geojson = make_pour_points_geojson(tmp_path, [(10, 20), (30, 40)])
        out = str(tmp_path / "points.shp")
        result = geojson_to_shapefile(str(geojson), out)
        assert Path(result).exists()

    def test_single_point(self, tmp_path):
        geojson = make_pour_points_geojson(tmp_path, [(0, 0)])
        out = str(tmp_path / "single.shp")
        result = geojson_to_shapefile(str(geojson), out)
        assert Path(result).exists()

    def test_preserves_feature_count(self, tmp_path):
        import geopandas as gpd
        pts = [(i, i * 2) for i in range(25)]
        geojson = make_pour_points_geojson(tmp_path, pts)
        out = str(tmp_path / "multi.shp")
        geojson_to_shapefile(str(geojson), out)
        gdf = gpd.read_file(out)
        assert len(gdf) == 25

    def test_negative_coordinates(self, tmp_path):
        """Western hemisphere / southern hemisphere coordinates."""
        geojson = make_pour_points_geojson(tmp_path, [(-97.5, 32.8), (-97.6, 32.9)])
        out = str(tmp_path / "neg.shp")
        result = geojson_to_shapefile(str(geojson), out)
        assert Path(result).exists()

    def test_missing_geojson_raises(self, tmp_path):
        with pytest.raises(Exception):
            geojson_to_shapefile(str(tmp_path / "nope.json"), str(tmp_path / "out.shp"))


# ============================================================================
# prepare_inputs workflow
# ============================================================================

class TestPrepareInputs:
    """Test the input preparation pipeline that Civil 3D feeds into."""

    def test_bil_and_geojson_workflow(self, tmp_path):
        make_bil(tmp_path, rows=50, cols=50)
        make_pour_points_geojson(tmp_path, [(25, 25), (10, 10)])
        result = prepare_inputs(str(tmp_path))
        assert "dem" in result
        assert "pour_points" in result
        assert "output" in result
        assert Path(result["dem"]).exists()
        assert Path(result["pour_points"]).exists()

    def test_existing_tif_skips_conversion(self, tmp_path):
        """If a .tif already exists, should use it directly."""
        import rasterio
        from rasterio.transform import from_origin
        tif_path = tmp_path / "existing.tif"
        data = np.ones((10, 10), dtype=np.float32)
        with rasterio.open(str(tif_path), 'w', driver='GTiff',
                           height=10, width=10, count=1, dtype=np.float32,
                           transform=from_origin(0, 10, 1, 1)) as dst:
            dst.write(data, 1)
        make_pour_points_geojson(tmp_path, [(5, 5)])
        result = prepare_inputs(str(tmp_path))
        assert result["dem"] == str(tif_path)

    def test_no_dem_raises(self, tmp_path):
        make_pour_points_geojson(tmp_path, [(5, 5)])
        with pytest.raises(FileNotFoundError, match="No DEM"):
            prepare_inputs(str(tmp_path))

    def test_no_pour_points_raises(self, tmp_path):
        make_bil(tmp_path, rows=10, cols=10)
        with pytest.raises(FileNotFoundError, match="No pour points"):
            prepare_inputs(str(tmp_path))

    def test_empty_directory_raises(self, tmp_path):
        with pytest.raises(FileNotFoundError):
            prepare_inputs(str(tmp_path))

    def test_workflow_config_json_loaded(self, tmp_path):
        """workflow_config.json from Civil 3D should be read without error."""
        make_bil(tmp_path, rows=10, cols=10)
        make_pour_points_geojson(tmp_path, [(5, 5)])
        config = {
            "surface": {"name": "EG Surface", "type": "TIN"},
            "pipe_network": {"name": "Storm Network", "structure_count": 5}
        }
        (tmp_path / "workflow_config.json").write_text(json.dumps(config))
        result = prepare_inputs(str(tmp_path))
        assert Path(result["dem"]).exists()


# ============================================================================
# Stress tests — large data, many points, performance
# ============================================================================

class TestStress:
    """Stress tests for memory, performance, and large inputs."""

    @pytest.mark.slow
    def test_large_dem_conversion(self, tmp_path):
        """2000x2000 DEM (~16MB) — typical Civil 3D surface export."""
        bil = make_bil(tmp_path, rows=2000, cols=2000, surface="slope_ns")
        out = str(tmp_path / "large.tif")
        start = time.time()
        result = bil_to_geotiff(str(bil), out)
        elapsed = time.time() - start
        assert Path(result).exists()
        size_mb = Path(result).stat().st_size / (1024 * 1024)
        print(f"\n  2000x2000 DEM: converted in {elapsed:.2f}s, output {size_mb:.1f}MB")

    @pytest.mark.slow
    def test_very_large_dem_conversion(self, tmp_path):
        """5000x5000 DEM (~100MB) — large LiDAR-derived surface."""
        bil = make_bil(tmp_path, rows=5000, cols=5000, surface="random")
        out = str(tmp_path / "xlarge.tif")
        start = time.time()
        result = bil_to_geotiff(str(bil), out)
        elapsed = time.time() - start
        assert Path(result).exists()
        size_mb = Path(result).stat().st_size / (1024 * 1024)
        print(f"\n  5000x5000 DEM: converted in {elapsed:.2f}s, output {size_mb:.1f}MB")

    @pytest.mark.slow
    def test_many_pour_points(self, tmp_path):
        """500 inlet structures — large pipe network."""
        import geopandas as gpd
        pts = [(i * 2.0, i * 2.0) for i in range(500)]
        geojson = make_pour_points_geojson(tmp_path, pts)
        out = str(tmp_path / "many_pts.shp")
        start = time.time()
        geojson_to_shapefile(str(geojson), out)
        elapsed = time.time() - start
        gdf = gpd.read_file(out)
        assert len(gdf) == 500
        print(f"\n  500 pour points: converted in {elapsed:.2f}s")

    @pytest.mark.slow
    def test_repeated_conversions_no_memory_leak(self, tmp_path):
        """Run 50 conversions to check for accumulating memory."""
        import tracemalloc
        tracemalloc.start()
        bil = make_bil(tmp_path, rows=200, cols=200, surface="valley")

        snapshots = []
        for i in range(50):
            out = str(tmp_path / f"iter_{i}.tif")
            bil_to_geotiff(str(bil), out)
            if i % 10 == 0:
                current, peak = tracemalloc.get_traced_memory()
                snapshots.append(current / (1024 * 1024))

        tracemalloc.stop()
        # Memory should not grow more than 50MB across iterations
        if len(snapshots) >= 2:
            growth = snapshots[-1] - snapshots[0]
            print(f"\n  Memory growth over 50 iterations: {growth:.1f}MB")
            assert growth < 50, f"Possible memory leak: {growth:.1f}MB growth"

    def test_concurrent_file_operations(self, tmp_path):
        """Multiple conversions writing to the same directory."""
        from concurrent.futures import ThreadPoolExecutor
        results = []

        def convert(idx):
            subdir = tmp_path / f"thread_{idx}"
            subdir.mkdir()
            bil = make_bil(subdir, rows=50, cols=50, surface="random", name=f"dem_{idx}")
            out = str(subdir / f"out_{idx}.tif")
            return bil_to_geotiff(str(bil), out)

        with ThreadPoolExecutor(max_workers=4) as pool:
            futures = [pool.submit(convert, i) for i in range(8)]
            results = [f.result() for f in futures]

        assert all(Path(r).exists() for r in results)
        assert len(results) == 8


# ============================================================================
# CLI argument parsing (run_delineation.py)
# ============================================================================

class TestCLI:
    """Test that command-line entry points parse arguments correctly."""

    def test_dem_utils_bil2tif_help(self):
        """Verify dem_utils CLI doesn't crash on --help."""
        import subprocess
        script = str(Path(__file__).parent.parent / "Python" / "dem_utils.py")
        result = subprocess.run(
            [sys.executable, script, "bil2tif", "--help"],
            capture_output=True, text=True, timeout=30
        )
        assert result.returncode == 0
        assert "BIL" in result.stdout or "bil" in result.stdout.lower()

    def test_run_delineation_help(self):
        """Verify run_delineation.py --help works."""
        import subprocess
        script = str(Path(__file__).parent.parent / "Python" / "run_delineation.py")
        result = subprocess.run(
            [sys.executable, script, "--help"],
            capture_output=True, text=True, timeout=30
        )
        assert result.returncode == 0
        assert "working_dir" in result.stdout

    def test_run_delineation_bad_dir(self):
        """Running with a nonexistent directory should fail gracefully."""
        import subprocess
        script = str(Path(__file__).parent.parent / "Python" / "run_delineation.py")
        result = subprocess.run(
            [sys.executable, script, "/nonexistent/path/12345"],
            capture_output=True, text=True, timeout=30
        )
        assert result.returncode != 0


# ============================================================================
# Python environment / dependency validation
# ============================================================================

class TestDependencies:
    """Verify required packages are importable and functional."""

    @pytest.mark.parametrize("pkg", [
        "numpy", "rasterio", "geopandas", "shapely", "fiona",
    ])
    def test_required_package_importable(self, pkg):
        __import__(pkg)

    def test_whitebox_importable(self):
        import whitebox
        wbt = whitebox.WhiteboxTools()
        assert wbt is not None

    def test_numpy_version_compatible(self):
        assert int(np.__version__.split(".")[0]) >= 1

    def test_rasterio_can_write(self, tmp_path):
        """Verify rasterio can actually write files (not just import)."""
        import rasterio
        from rasterio.transform import from_origin
        out = str(tmp_path / "test_write.tif")
        data = np.ones((5, 5), dtype=np.float32)
        with rasterio.open(out, 'w', driver='GTiff', height=5, width=5,
                           count=1, dtype=np.float32,
                           transform=from_origin(0, 5, 1, 1)) as dst:
            dst.write(data, 1)
        assert Path(out).exists()
        assert Path(out).stat().st_size > 0
