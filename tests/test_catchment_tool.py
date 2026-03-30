"""
Terminal-runnable tests for CatchmentTool Python components.

Run:
    cd CatchmentTool
    python -m pytest tests/test_catchment_tool.py -v
"""

from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import pytest

# Add the Python source directory to path
sys.path.insert(0, str(Path(__file__).parent.parent / "Python"))


# ============================================================================
# CLI — catchment_delineation.py entry point
# ============================================================================

class TestCLI:
    """Test that the main CLI entry point parses arguments correctly."""

    def test_catchment_delineation_help(self):
        import subprocess
        script = str(Path(__file__).parent.parent / "Python" / "catchment_delineation.py")
        result = subprocess.run(
            [sys.executable, script, "--help"],
            capture_output=True, text=True, timeout=30
        )
        assert result.returncode == 0
        assert "--dem" in result.stdout
        assert "--inlets" in result.stdout
        assert "--smooth-radius" in result.stdout

    def test_catchment_delineation_missing_args(self):
        import subprocess
        script = str(Path(__file__).parent.parent / "Python" / "catchment_delineation.py")
        result = subprocess.run(
            [sys.executable, script],
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

    def test_shapely_coverage_simplify(self):
        """Verify coverage_simplify is available (shapely 2.0+)."""
        from shapely import coverage_simplify
        assert coverage_simplify is not None
