"""
Civil 3D Catchment Delineation Tool
Uses WhiteboxTools for hydrologically-correct catchment delineation

This script:
1. Reads a DEM exported from Civil 3D TIN surface
2. Reads inlet structure locations as pour points
3. Performs hydrological analysis using WhiteboxTools
4. Outputs catchment polygons linked to inlet structures
"""

import gc
import json
import logging
import shutil
import tempfile
from contextlib import contextmanager
from pathlib import Path

import whitebox
import numpy as np
import rasterio
from rasterio.features import shapes
import geopandas as gpd
from shapely.geometry import shape

from crs_utils import resolve_crs, get_linear_unit, is_metric_unit
from validation import (
    compute_snap_distance,
    validate_pour_points,
    check_snap_results,
    convert_area_threshold,
)

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)


# ============================================================================
# Context manager for temp workspace (Fix #4)
# ============================================================================

@contextmanager
def gis_workspace(retain: bool = False, workspace_dir: Path | None = None):
    """
    Context manager for intermediate GIS files.

    Args:
        retain: If True, keep intermediates after run (for debugging).
        workspace_dir: Explicit directory. If None, create a temp dir.
    """
    if workspace_dir:
        workspace_dir = Path(workspace_dir)
        workspace_dir.mkdir(parents=True, exist_ok=True)
        work_path = workspace_dir
    else:
        work_path = Path(tempfile.mkdtemp(prefix="catchment_"))

    logger.info(f"Workspace: {work_path}")
    try:
        yield work_path
    finally:
        if not retain:
            gc.collect()  # release GDAL handles before cleanup
            try:
                shutil.rmtree(work_path)
                logger.info(f"Cleaned up workspace: {work_path}")
            except PermissionError:
                logger.warning(
                    f"Could not delete workspace (files may be locked): {work_path}"
                )
        else:
            logger.info(f"Retaining workspace for debugging: {work_path}")


class CatchmentDelineator:
    """
    Performs catchment delineation using WhiteboxTools.

    Workflow:
    1. Fill depressions in DEM (or breach - configurable)
    2. Calculate D8 flow direction
    3. Calculate flow accumulation
    4. Snap pour points to flow accumulation
    5. Delineate watersheds for each pour point
    6. Convert raster watersheds to vector polygons (spatial join)
    """

    def __init__(self, working_dir: str, config_file: str = None):
        self.working_dir = Path(working_dir)
        self.working_dir.mkdir(parents=True, exist_ok=True)

        # Initialize WhiteboxTools
        self.wbt = whitebox.WhiteboxTools()
        self.wbt.set_working_dir(str(self.working_dir))
        self.wbt.set_verbose_mode(True)

        # Load config or use defaults
        self.config = self._load_config(config_file)

        # Set during processing
        self.dem_path = None
        self.pour_points_path = None
        self.crs = None
        self.cell_size = None

    def _load_config(self, config_file: str) -> dict:
        """Load configuration from JSON file or return defaults."""
        defaults = {
            "depression_method": "breach",
            "snap_distance": "auto",       # Fix #1: auto-calibrate from cell size
            "snap_multiplier": 10.0,       # Fix #1: multiplier for auto snap (10x cell = robust for Civil 3D)
            "min_catchment_area": 0.5,     # Fix #10: in real-world units
            "area_unit": "acres",          # Fix #10: unit for min area
            "drawing_units": "ft",         # Fix #10: drawing linear units
            "flow_accumulation_threshold": 100,
            "retain_intermediates": False,  # Fix #4: cleanup by default
            "smooth_radius": None,          # Morphological smooth radius; None = auto (1.5x cell size)
        }

        if config_file and Path(config_file).exists():
            with open(config_file, 'r') as f:
                user_config = json.load(f)
                defaults.update(user_config)

        return defaults

    def run(self, dem_file: str, pour_points_file: str, output_file: str,
            user_crs: str = None) -> str:
        """
        Run the complete catchment delineation workflow.

        Args:
            dem_file: Path to input DEM (GeoTIFF or BIL)
            pour_points_file: Path to inlet structures (Shapefile/GeoJSON)
            output_file: Path for output catchment polygons
            user_crs: Optional user-specified CRS (e.g., 'EPSG:2277')

        Returns:
            Path to output catchment shapefile
        """
        logger.info("=" * 60)
        logger.info("CATCHMENT DELINEATION TOOL")
        logger.info("=" * 60)

        self.dem_path = Path(dem_file)
        self.pour_points_path = Path(pour_points_file)
        output_path = Path(output_file)

        # --- Load JSON metadata from C# exporter (if present) ---
        json_metadata = self._load_dem_metadata()

        # --- Fix #2: Tiered CRS resolution (no more fake EPSG:32617) ---
        logger.info("\n[CRS] Resolving coordinate reference system...")
        self.crs = resolve_crs(
            dem_path=self.dem_path,
            json_metadata=json_metadata,
            user_crs=user_crs,
            drawing_units=self.config.get("drawing_units", "ft"),
        )
        logger.info(f"Using CRS: {self.crs}")

        # --- Pre-process DEM to GeoTIFF for WhiteboxTools ---
        logger.info("\n[Pre-processing] Converting DEM to GeoTIFF...")
        converted_dem = self.working_dir / "input_dem.tif"
        self._convert_dem_to_geotiff(converted_dem)
        self.dem_path = converted_dem

        # Read cell size for later use
        with rasterio.open(str(self.dem_path)) as src:
            self.cell_size = max(src.res)

        # --- Fix #1: Auto-calibrate snap distance ---
        snap_dist = self.config["snap_distance"]
        if snap_dist == "auto" or snap_dist is None:
            snap_dist = compute_snap_distance(
                str(self.dem_path),
                multiplier=self.config.get("snap_multiplier", 5.0),
            )
        else:
            snap_dist = float(snap_dist)
            logger.info(f"Using user-specified snap distance: {snap_dist}")
        self.config["_resolved_snap_distance"] = snap_dist

        # --- Fix #10: Convert area threshold to square drawing units ---
        min_area_sq_units = convert_area_threshold(
            min_area=self.config["min_catchment_area"],
            area_unit=self.config.get("area_unit", "acres"),
            drawing_units=self.config.get("drawing_units", "ft"),
        )
        self.config["_resolved_min_area"] = min_area_sq_units

        # --- Fix #5: Validate pour points against DEM extent ---
        logger.info("\n[Validation] Checking pour points against DEM extent...")
        pour_points_gdf = gpd.read_file(str(self.pour_points_path))
        validation = validate_pour_points(
            dem_path=str(self.dem_path),
            pour_points_gdf=pour_points_gdf,
            snap_dist=snap_dist,
        )
        # Filter to valid points only
        valid_gdf = pour_points_gdf.iloc[validation.valid_indices].reset_index(drop=True)
        logger.info(f"Proceeding with {len(valid_gdf)} valid pour points")

        # Save validated points for WhiteboxTools
        validated_shp = str(self.working_dir / "pour_points_validated.shp")
        valid_gdf.to_file(validated_shp)

        # --- Optional: Burn pipes into DEM ---
        if self.config.get("burn_pipes"):
            logger.info("\n[Pre-processing] Burning pipes into DEM...")
            self.dem_path = Path(self._burn_pipes_into_dem())

        # Step 1: Preprocess DEM (fill/breach depressions)
        logger.info("\n[Step 1/6] Preprocessing DEM - removing depressions...")
        conditioned_dem = self._condition_dem(valid_gdf)

        # Step 2: Calculate flow direction
        logger.info("\n[Step 2/6] Calculating D8 flow direction...")
        flow_dir = self._calculate_flow_direction(conditioned_dem)

        # Step 3: Calculate flow accumulation
        logger.info("\n[Step 3/6] Calculating flow accumulation...")
        flow_accum = self._calculate_flow_accumulation(conditioned_dem)

        # Step 4: Snap pour points to flow accumulation
        logger.info("\n[Step 4/6] Snapping pour points to flow network...")
        snapped_points = self._snap_pour_points(flow_accum, validated_shp, snap_dist)

        # --- Fix #1 cont'd: Post-snap quality check ---
        snap_check = check_snap_results(
            original_gdf=valid_gdf,
            snapped_path=snapped_points,
            cell_size=self.cell_size,
            snap_dist=snap_dist,
        )

        # Resolve collisions — ensure each pour point occupies a unique cell
        if snap_check.collisions:
            snapped_points = self._resolve_snap_collisions(
                snapped_points, flow_accum, valid_gdf
            )

        # Step 5: Delineate watersheds
        logger.info("\n[Step 5/6] Delineating catchments...")
        watersheds_raster = self._delineate_watersheds(flow_dir, snapped_points)

        # Step 6: Convert to vector and link to structures via spatial join
        logger.info("\n[Step 6/6] Converting to vector polygons...")
        self._raster_to_vector_spatial_join(
            watersheds_raster, snapped_points, valid_gdf, output_path
        )

        logger.info("\n" + "=" * 60)
        logger.info(f"COMPLETE! Output: {output_path}")
        logger.info("=" * 60)

        return str(output_path)

    # ------------------------------------------------------------------
    # Pre-processing helpers
    # ------------------------------------------------------------------

    def _load_dem_metadata(self) -> dict | None:
        """Load companion JSON metadata written by C# SurfaceExporter."""
        dem_stem = self.dem_path.with_suffix('')
        for suffix in ('.json',):
            json_path = dem_stem.with_suffix(suffix)
            if json_path.exists():
                try:
                    meta = json.loads(json_path.read_text())
                    logger.info(f"Loaded metadata from {json_path}")
                    return meta
                except Exception as e:
                    logger.warning(f"Failed to read metadata {json_path}: {e}")
        return None

    def _convert_dem_to_geotiff(self, output_path: Path):
        """Convert input DEM to GeoTIFF with valid CRS for WhiteboxTools."""
        with rasterio.open(self.dem_path) as src:
            self.transform = src.transform
            self.dem_shape = src.shape
            logger.info(f"DEM Shape: {self.dem_shape}, Resolution: {src.res}")

            data = src.read(1)
            nodata = src.nodata

            profile = {
                'driver': 'GTiff',
                'dtype': 'float32',
                'width': src.width,
                'height': src.height,
                'count': 1,
                'crs': self.crs,
                'transform': self.transform,
                'nodata': nodata if nodata is not None else -9999.0,
                'tiled': False,
            }

            with rasterio.open(output_path, 'w', **profile) as dst:
                dst.write(data.astype('float32'), 1)

            logger.info(f"Written: {output_path}")

    def _burn_pipes_into_dem(self) -> str:
        """Burn pipe network into DEM to enforce flow along pipe paths."""
        from shapely.geometry import LineString
        from rasterio.features import rasterize

        pipe_file = self.config.get("burn_pipes")
        burn_depth = self.config.get("burn_depth", 3.0)

        if not pipe_file or not Path(pipe_file).exists():
            logger.warning(f"Pipe file not found: {pipe_file}")
            return str(self.dem_path)

        with open(pipe_file, 'r') as f:
            pipe_data = json.load(f)

        features = pipe_data.get('features', [])
        logger.info(f"Burning {len(features)} pipes into DEM (depth: {burn_depth})")

        if not features:
            logger.warning("No pipes to burn")
            return str(self.dem_path)

        with rasterio.open(self.dem_path) as src:
            dem_data = src.read(1)
            profile = src.profile.copy()
            transform = src.transform
            nodata = src.nodata

        pipe_shapes = []
        cell_size = abs(transform[0])
        for feature in features:
            coords = feature['geometry']['coordinates']
            if len(coords) >= 2:
                line = LineString(coords)
                # Scale buffer by pipe diameter (if available), min 1.5x cell size
                diameter = feature.get('properties', {}).get('diameter', 0) or 0
                buffer_dist = max(cell_size * 1.5, diameter * 0.75)
                buffered = line.buffer(buffer_dist)
                # Scale burn depth by pipe diameter: larger pipes get deeper burns
                pipe_burn = burn_depth
                if diameter > 0:
                    # Scale: small pipes (< 12") get base depth, large pipes get up to 2x
                    pipe_burn = burn_depth * max(1.0, min(2.0, diameter / 12.0))
                pipe_shapes.append((buffered, pipe_burn))

        if pipe_shapes:
            # Rasterize with per-pipe burn depths (float values)
            burn_mask = rasterize(
                pipe_shapes, out_shape=dem_data.shape,
                transform=transform, fill=0.0, dtype='float32'
            )
            burned_dem = dem_data.copy()
            pipe_cells = burn_mask > 0
            burned_dem[pipe_cells] = burned_dem[pipe_cells] - burn_mask[pipe_cells]

            if nodata is not None:
                burned_dem[burned_dem == nodata] = nodata

            logger.info(f"Burned {np.sum(pipe_cells):,} cells (depth varies by pipe diameter)")

            output_path = self.working_dir / "dem_burned.tif"
            with rasterio.open(output_path, 'w', **profile) as dst:
                dst.write(burned_dem.astype('float32'), 1)
            return str(output_path)

        return str(self.dem_path)

    # ------------------------------------------------------------------
    # WhiteboxTools processing steps
    # ------------------------------------------------------------------

    def _condition_dem(self, pour_points_gdf: gpd.GeoDataFrame) -> str:
        """Condition DEM while preserving designed drainage near inlets.

        Standard breach/fill treats every pit as an artifact, but on graded
        civil sites the depressions at inlets are intentional.  This method:
        1. Saves original elevations within a protection radius of each inlet
        2. Runs breach/fill normally to fix genuine artifacts
        3. Restores the original elevations inside the protected zones
        4. Re-stamps each inlet cell as the local low point
        """
        raw_output = str(self.working_dir / "dem_conditioned_raw.tif")
        output = str(self.working_dir / "dem_conditioned.tif")

        if self.config["depression_method"] == "breach":
            breach_dist = self.config.get("breach_distance", 25)
            logger.info(f"Using breach depressions method (dist={breach_dist})...")
            self.wbt.breach_depressions_least_cost(
                dem=str(self.dem_path), output=raw_output,
                dist=breach_dist, fill=True
            )
        else:
            logger.info("Using fill depressions method...")
            self.wbt.fill_depressions(
                dem=str(self.dem_path), output=raw_output, fix_flats=True
            )

        # Restore original elevations near inlets
        protect_radius = self.config.get("inlet_protect_radius", 5)
        logger.info(
            f"Protecting inlet neighborhoods ({protect_radius} cells) "
            f"from conditioning changes"
        )

        with rasterio.open(str(self.dem_path)) as orig_src:
            orig_data = orig_src.read(1)
            transform = orig_src.transform
            profile = orig_src.profile.copy()

        with rasterio.open(raw_output) as cond_src:
            cond_data = cond_src.read(1)

        rows, cols = orig_data.shape
        restored_cells = 0

        for _, pt in pour_points_gdf.iterrows():
            col_f, row_f = ~transform * (pt.geometry.x, pt.geometry.y)
            cr, cc = int(round(row_f)), int(round(col_f))

            r1 = max(0, cr - protect_radius)
            r2 = min(rows, cr + protect_radius + 1)
            c1 = max(0, cc - protect_radius)
            c2 = min(cols, cc + protect_radius + 1)

            # Restore original elevations in the protection window
            patch = orig_data[r1:r2, c1:c2]
            cond_data[r1:r2, c1:c2] = patch
            restored_cells += (r2 - r1) * (c2 - c1)

            # Re-stamp inlet cell as definitive low point
            if 0 <= cr < rows and 0 <= cc < cols:
                local_min = orig_data[r1:r2, c1:c2].min()
                cond_data[cr, cc] = local_min - 0.5

        logger.info(f"Restored {restored_cells:,} cells near {len(pour_points_gdf)} inlets")

        with rasterio.open(output, 'w', **profile) as dst:
            dst.write(cond_data.astype('float32'), 1)

        return output

    def _calculate_flow_direction(self, dem: str) -> str:
        output = str(self.working_dir / "flow_direction.tif")
        self.wbt.d8_pointer(dem=dem, output=output, esri_pntr=False)
        return output

    def _calculate_flow_accumulation(self, dem: str) -> str:
        output = str(self.working_dir / "flow_accumulation.tif")
        self.wbt.d8_flow_accumulation(
            i=dem, output=output, out_type="cells", log=False, clip=False
        )
        return output

    def _snap_pour_points(self, flow_accum: str, pour_pts_shp: str,
                          snap_dist: float) -> str:
        """Snap pour points to cells with highest flow accumulation."""
        output = str(self.working_dir / "pour_points_snapped.shp")
        logger.info(f"Snap distance: {snap_dist:.2f} map units")
        self.wbt.snap_pour_points(
            pour_pts=pour_pts_shp, flow_accum=flow_accum,
            output=output, snap_dist=snap_dist
        )
        return output

    def _resolve_snap_collisions(self, snapped_path: str,
                                  flow_accum_path: str,
                                  original_gdf: gpd.GeoDataFrame) -> str:
        """Move collided pour points to unique cells.

        When multiple inlets snap to the same high-accumulation cell
        (e.g., along a trunk pipe), only the last one produces a watershed.
        This method detects collisions and moves extras to the next-best
        unique cell within a small search window around each point's
        original location, guaranteeing one watershed per inlet.

        Returns path to the de-duplicated shapefile.
        """
        snapped_gdf = gpd.read_file(snapped_path)

        with rasterio.open(flow_accum_path) as src:
            accum_data = src.read(1)
            transform = src.transform
            cell_size = max(src.res)

        def _xy_to_cell(x, y):
            col, row = ~transform * (x, y)
            return (int(round(row)), int(round(col)))

        def _cell_to_xy(r, c):
            x, y = transform * (c + 0.5, r + 0.5)
            return (x, y)

        occupied = {}
        collision_indices = []

        for i, pt in enumerate(snapped_gdf.geometry):
            cell = _xy_to_cell(pt.x, pt.y)
            if cell in occupied:
                collision_indices.append(i)
            else:
                occupied[cell] = i

        if not collision_indices:
            return snapped_path

        logger.warning(
            f"{len(collision_indices)} pour point(s) collided after snapping — "
            f"resolving to unique cells"
        )

        rows, cols = accum_data.shape
        search_radius = max(3, int(cell_size * 5 / cell_size))

        from shapely.geometry import Point
        for i in collision_indices:
            orig_pt = original_gdf.geometry.iloc[i]
            or_, oc_ = _xy_to_cell(orig_pt.x, orig_pt.y)

            candidates = []
            for dr in range(-search_radius, search_radius + 1):
                for dc in range(-search_radius, search_radius + 1):
                    r, c = or_ + dr, oc_ + dc
                    if 0 <= r < rows and 0 <= c < cols:
                        cell = (r, c)
                        if cell not in occupied:
                            candidates.append((accum_data[r, c], r, c))

            if candidates:
                candidates.sort(key=lambda t: t[0], reverse=True)
                best_accum, best_r, best_c = candidates[0]
                x, y = _cell_to_xy(best_r, best_c)
                snapped_gdf.geometry.iloc[i] = Point(x, y)
                occupied[(best_r, best_c)] = i
                name = "?"
                for col_name in ('name', 'label', 'structure_id', 'struct_nam'):
                    if col_name in original_gdf.columns:
                        name = original_gdf.iloc[i][col_name]
                        break
                logger.info(
                    f"  Moved '{name}' to unique cell ({best_r},{best_c}) "
                    f"accum={best_accum:.0f}"
                )
            else:
                logger.warning(
                    f"  Could not find unique cell for point {i} — "
                    f"it may share a watershed with another inlet"
                )

        output = str(self.working_dir / "pour_points_deduped.shp")
        snapped_gdf.to_file(output)
        logger.info(f"Saved de-duplicated pour points: {output}")
        return output

    def _delineate_watersheds(self, flow_dir: str, pour_points: str) -> str:
        output = str(self.working_dir / "watersheds.tif")
        self.wbt.watershed(
            d8_pntr=flow_dir, pour_pts=pour_points,
            output=output, esri_pntr=False
        )
        return output

    # ------------------------------------------------------------------
    # Fix #3 + #9: Spatial join instead of fragile ID matching
    # ------------------------------------------------------------------

    def _raster_to_vector_spatial_join(
        self,
        watersheds_raster: str,
        snapped_points: str,
        original_pour_points: gpd.GeoDataFrame,
        output_path: Path,
    ) -> None:
        """
        Convert watershed raster to vector polygons and link to structures
        using spatial join instead of fragile sequential ID matching.
        """
        # Read the watershed raster
        with rasterio.open(watersheds_raster) as src:
            data = src.read(1)
            transform = src.transform
            crs = src.crs
            nodata = src.nodata

        unique_values = np.unique(data)
        logger.info(
            f"Watershed raster values: "
            f"{unique_values[:20]}{'...' if len(unique_values) > 20 else ''}"
        )

        # Vectorize — mask out nodata and 0 (background)
        if nodata is not None:
            mask = (data != nodata) & (data != 0)
        else:
            mask = data != 0

        valid_cells = np.sum(mask)
        logger.info(f"Valid watershed cells: {valid_cells:,}")

        min_area = self.config["_resolved_min_area"]

        # Extract ALL polygons from raster (keep small ones for merging later)
        cell_size = getattr(self, 'cell_size', 1.0)
        polygons = []
        small_polygons = []
        for geom, value in shapes(data.astype(np.int32), mask=mask, transform=transform):
            polygon = shape(geom)
            area = polygon.area
            entry = {'geometry': polygon, 'raster_id': int(value), 'area': area}
            if area >= min_area:
                polygons.append(entry)
            else:
                small_polygons.append(entry)

        if small_polygons:
            logger.info(f"{len(small_polygons)} polygon(s) below min area — will merge into nearest neighbor")

        if not polygons:
            logger.warning("No valid catchment polygons created!")
            logger.warning("Possible causes:")
            logger.warning("  - All catchments below minimum area threshold")
            logger.warning("  - DEM doesn't have proper drainage to pour points")
            self._write_empty_output(output_path)
            return

        watersheds_gdf = gpd.GeoDataFrame(polygons, crs=crs)
        logger.info(f"Vectorized {len(watersheds_gdf)} polygons above min area")

        # Merge small polygons into their closest neighbor (longest shared border)
        if small_polygons:
            from shapely.ops import unary_union
            merged_count = 0
            for sp in small_polygons:
                sg = sp['geometry']
                best_idx = None
                best_shared = 0
                for idx, row in watersheds_gdf.iterrows():
                    shared = sg.intersection(row.geometry).length
                    if shared > best_shared:
                        best_shared = shared
                        best_idx = idx
                if best_idx is not None and best_shared > 0:
                    watersheds_gdf.at[best_idx, 'geometry'] = unary_union(
                        [watersheds_gdf.at[best_idx, 'geometry'], sg])
                    merged_count += 1
            if merged_count:
                watersheds_gdf['area'] = watersheds_gdf.geometry.area
                logger.info(f"Merged {merged_count} small polygon(s) into neighbors")

        # Smooth + simplify raster stairstep edges while keeping adjacent
        # catchments perfectly snapped. coverage_simplify treats the polygons
        # as a coverage — shared edges are simplified identically, so no gaps
        # or overlaps are introduced. Applied here (before spatial join) where
        # the raster polygons form a perfect tiling.
        smooth_tol = self.config.get("smooth_radius") or (cell_size * 1.0)
        try:
            from shapely import coverage_simplify
            raw_geoms = np.array(list(watersheds_gdf.geometry), dtype=object)
            simplified = list(coverage_simplify(raw_geoms, smooth_tol))
            watersheds_gdf['geometry'] = simplified
            watersheds_gdf['area'] = watersheds_gdf.geometry.area
            avg_v = int(sum(len(g.exterior.coords) for g in simplified if g.geom_type == 'Polygon') / len(simplified))
            logger.info(f"Coverage-simplified {len(simplified)} polygons (tol={smooth_tol:.1f}, ~{avg_v} verts avg)")
        except Exception as e:
            logger.warning(f"Coverage simplify failed ({e}), using raw raster polygons")

        # --- Spatial join: match pour points to polygons ---
        snapped_gdf = gpd.read_file(snapped_points)
        snapped_gdf = snapped_gdf.set_crs(crs, allow_override=True)

        # Copy attributes from original pour points (labels, IDs, etc.)
        for col in original_pour_points.columns:
            if col != 'geometry' and col not in snapped_gdf.columns:
                if len(original_pour_points) == len(snapped_gdf):
                    snapped_gdf[col] = original_pour_points[col].values

        # Spatial join: which polygon contains each snapped pour point?
        joined = gpd.sjoin(
            snapped_gdf, watersheds_gdf,
            how="left", predicate="within"
        )

        # Build final output — one catchment per matched pour point.
        # If multiple pour points share the same watershed polygon,
        # split it using Voronoi partitioning so each gets a unique area.
        from collections import defaultdict

        # Group pour points by their matched polygon
        poly_to_points = defaultdict(list)
        unmatched_points = []

        for idx, row in joined.iterrows():
            if 'index_right' not in row or np.isnan(row.get('index_right', np.nan)):
                label = _get_structure_label(row, idx)
                logger.warning(f"Pour point '{label}' did not fall inside any watershed polygon")
                unmatched_points.append(idx)
                continue
            poly_idx = int(row['index_right'])
            poly_to_points[poly_idx].append(idx)

        results = []
        matched_raster_ids = set()

        for poly_idx, point_indices in poly_to_points.items():
            polygon = watersheds_gdf.geometry.iloc[poly_idx]
            raster_id = watersheds_gdf.iloc[poly_idx].get('raster_id', -1)
            matched_raster_ids.add(raster_id)

            if len(point_indices) == 1:
                # Single pour point — assign whole polygon
                idx = point_indices[0]
                row = joined.loc[idx]
                results.append({
                    'geometry': polygon,
                    'watershed_id': raster_id,
                    'structure_id': _get_structure_id(row, idx),
                    'struct_name': _get_structure_label(row, idx),
                    'struct_x': snapped_gdf.geometry.iloc[idx].x,
                    'struct_y': snapped_gdf.geometry.iloc[idx].y,
                    'area': polygon.area,
                })
            else:
                # Multiple pour points share this polygon — split by nearest point
                logger.info(f"Splitting watershed {raster_id} among {len(point_indices)} pour points")
                from shapely.ops import voronoi_diagram, unary_union
                from shapely.geometry import MultiPoint
                pts = [snapped_gdf.geometry.iloc[i] for i in point_indices]
                mp = MultiPoint(pts)
                try:
                    voronoi = voronoi_diagram(mp, envelope=polygon.buffer(10))
                    for idx in point_indices:
                        pt = snapped_gdf.geometry.iloc[idx]
                        row = joined.loc[idx]
                        # Find the Voronoi cell containing this point
                        for cell in voronoi.geoms:
                            if cell.contains(pt):
                                clipped = polygon.intersection(cell)
                                if clipped.is_empty:
                                    continue
                                if clipped.geom_type == 'MultiPolygon':
                                    clipped = max(clipped.geoms, key=lambda g: g.area)
                                results.append({
                                    'geometry': clipped,
                                    'watershed_id': raster_id,
                                    'structure_id': _get_structure_id(row, idx),
                                    'struct_name': _get_structure_label(row, idx),
                                    'struct_x': pt.x,
                                    'struct_y': pt.y,
                                    'area': clipped.area,
                                })
                                break
                except Exception as e:
                    logger.warning(f"Voronoi split failed for watershed {raster_id}: {e}")
                    # Fallback: assign whole polygon to first point
                    idx = point_indices[0]
                    row = joined.loc[idx]
                    results.append({
                        'geometry': polygon,
                        'watershed_id': raster_id,
                        'structure_id': _get_structure_id(row, idx),
                        'struct_name': _get_structure_label(row, idx),
                        'struct_x': snapped_gdf.geometry.iloc[idx].x,
                        'struct_y': snapped_gdf.geometry.iloc[idx].y,
                        'area': polygon.area,
                    })

        # Check for unmatched polygons (watersheds with no pour point)
        all_raster_ids = set(watersheds_gdf['raster_id'])
        unmatched = all_raster_ids - matched_raster_ids
        if unmatched:
            logger.info(f"{len(unmatched)} watershed polygon(s) had no matching pour point")

        # Finalize output
        output_path.parent.mkdir(parents=True, exist_ok=True)
        geojson_path = output_path.with_suffix('.json')

        if results:
            gdf = gpd.GeoDataFrame(results, crs=crs)
            gdf['area'] = gdf.geometry.area

            gdf.to_file(output_path)
            gdf.to_file(geojson_path, driver='GeoJSON')
            logger.info(f"Created {len(gdf)} catchment polygons")
            logger.info(f"Total catchment area: {gdf['area'].sum():,.1f} sq units")
            logger.info(f"Written: {output_path}")
            logger.info(f"Written: {geojson_path}")
        else:
            logger.warning("No catchments could be matched to pour points!")
            self._write_empty_output(output_path)

    def _write_empty_output(self, output_path: Path):
        """Write an empty GeoJSON so the C# side doesn't fail looking for it."""
        geojson_path = output_path.with_suffix('.json')
        output_path.parent.mkdir(parents=True, exist_ok=True)
        empty = {"type": "FeatureCollection", "features": []}
        with open(geojson_path, 'w') as f:
            json.dump(empty, f)
        logger.info(f"Written empty GeoJSON: {geojson_path}")


# ============================================================================
# Helpers
# ============================================================================

def _get_structure_label(row, index: int) -> str:
    """Extract a structure label from a row, trying common column names."""
    for col in ('name', 'label', 'struct_nam', 'struct_name', 'structure_name'):
        val = row.get(col)
        if val is not None and str(val).strip():
            return str(val)
    return f"Inlet {index}"


def _get_structure_id(row, index: int) -> str:
    """Extract a structure ID from a row."""
    for col in ('structure_id', 'structure_', 'struct_id', 'id'):
        val = row.get(col)
        if val is not None and str(val).strip():
            return str(val)
    return f"inlet_{index}"


# ============================================================================
# CLI
# ============================================================================

def main():
    """Main entry point for command-line usage."""
    import argparse

    parser = argparse.ArgumentParser(
        description='Delineate catchments from DEM and inlet structures'
    )
    parser.add_argument('--dem', '-d', required=True,
                        help='Path to input DEM (GeoTIFF or BIL)')
    parser.add_argument('--inlets', '-i', required=True,
                        help='Path to inlet structures (Shapefile/GeoJSON)')
    parser.add_argument('--output', '-o', required=True,
                        help='Path for output catchment polygons')
    parser.add_argument('--config', '-c',
                        help='Path to JSON configuration file')
    parser.add_argument('--working-dir', '-w', default=None,
                        help='Working directory for intermediate files (default: auto temp)')
    parser.add_argument('--crs',
                        help='Coordinate Reference System (e.g., EPSG:2277)')
    parser.add_argument('--drawing-units', choices=['ft', 'm'], default='ft',
                        help='Drawing linear units (default: ft)')
    parser.add_argument('--snap-distance', type=float, default=None,
                        help='Snap distance in map units (default: auto from cell size)')
    parser.add_argument('--snap-multiplier', type=float, default=10.0,
                        help='Cell-size multiplier for auto snap distance (default: 10)')
    parser.add_argument('--breach-distance', type=int, default=25,
                        help='Max breach distance for depression removal (default: 25)')
    parser.add_argument('--depression-method', choices=['breach', 'fill'], default='breach',
                        help='Depression handling method (default: breach)')
    parser.add_argument('--min-area', type=float, default=0.5,
                        help='Minimum catchment area (default: 0.5)')
    parser.add_argument('--area-unit', choices=['acres', 'hectares', 'sq_ft', 'sq_m'],
                        default='sq_ft',
                        help='Unit for --min-area (default: sq_ft)')
    parser.add_argument('--smooth-radius', type=float, default=None,
                        help='Morphological smooth radius in map units, 0=none (default: auto = 3x cell size)')
    parser.add_argument('--burn-pipes',
                        help='Path to pipe lines GeoJSON for burning into DEM')
    parser.add_argument('--burn-depth', type=float, default=3.0,
                        help='Depth to burn pipes into DEM (default: 3)')
    parser.add_argument('--retain-intermediates', action='store_true',
                        help='Keep intermediate files for debugging')

    args = parser.parse_args()

    # Build config
    config = {
        "depression_method": args.depression_method,
        "snap_distance": args.snap_distance if args.snap_distance else "auto",
        "snap_multiplier": args.snap_multiplier,
        "breach_distance": args.breach_distance,
        "min_catchment_area": args.min_area,
        "area_unit": args.area_unit,
        "drawing_units": args.drawing_units,
        "smooth_radius": args.smooth_radius,
        "burn_pipes": args.burn_pipes,
        "burn_depth": args.burn_depth,
        "retain_intermediates": args.retain_intermediates,
    }

    # Run with managed workspace (Fix #4)
    # When C# provides --working-dir, always retain (C# manages cleanup)
    retain = args.retain_intermediates or (args.working_dir is not None)
    with gis_workspace(
        retain=retain,
        workspace_dir=Path(args.working_dir) if args.working_dir else None,
    ) as ws:
        delineator = CatchmentDelineator(
            working_dir=str(ws),
            config_file=args.config,
        )
        delineator.config.update(config)

        output = delineator.run(
            dem_file=args.dem,
            pour_points_file=args.inlets,
            output_file=args.output,
            user_crs=args.crs,
        )

    print(f"\nOutput saved to: {output}")


if __name__ == "__main__":
    main()
