"""
Civil 3D Catchment Delineation Tool
Uses WhiteboxTools for hydrologically-correct catchment delineation

This script:
1. Reads a DEM exported from Civil 3D TIN surface
2. Reads inlet structure locations as pour points
3. Performs hydrological analysis using WhiteboxTools
4. Outputs catchment polygons linked to inlet structures
"""

import os
import json
import logging
from pathlib import Path

import whitebox
import numpy as np
import rasterio
from rasterio.features import shapes
import geopandas as gpd
from shapely.geometry import shape, mapping
import fiona

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)


class CatchmentDelineator:
    """
    Performs catchment delineation using WhiteboxTools.
    
    Workflow:
    1. Fill depressions in DEM (or breach - configurable)
    2. Calculate D8 flow direction
    3. Calculate flow accumulation
    4. Snap pour points to flow accumulation
    5. Delineate watersheds for each pour point
    6. Convert raster watersheds to vector polygons
    """
    
    def __init__(self, working_dir: str, config_file: str = None):
        """
        Initialize the delineator.
        
        Args:
            working_dir: Directory containing input files and for output
            config_file: Optional JSON config file path
        """
        self.working_dir = Path(working_dir)
        self.working_dir.mkdir(parents=True, exist_ok=True)
        
        # Initialize WhiteboxTools
        self.wbt = whitebox.WhiteboxTools()
        self.wbt.set_working_dir(str(self.working_dir))
        self.wbt.set_verbose_mode(True)
        
        # Load config or use defaults
        self.config = self._load_config(config_file)
        
        # File paths (will be set during processing)
        self.dem_path = None
        self.pour_points_path = None
        self.crs = None
        
    def _resolve_crs(self):
        """
        Try to resolve CRS from companion files next to the input DEM.
        Checks for .prj (WKT) and .json (metadata with coordinate_system field).
        """
        from rasterio.crs import CRS

        dem_stem = self.dem_path.with_suffix('')  # strip extension

        # Try .prj file (WKT format written by SurfaceExporter)
        prj_path = dem_stem.with_suffix('.prj')
        if not prj_path.exists():
            # Also try alongside original input path
            prj_path = Path(str(self.dem_path).replace('.tif', '').replace('.bil', '')).with_suffix('.prj')

        if prj_path.exists():
            try:
                wkt = prj_path.read_text().strip()
                if wkt and not wkt.startswith('LOCAL_CS'):
                    crs = CRS.from_wkt(wkt)
                    logger.info(f"CRS from .prj file: {crs}")
                    return crs
                elif wkt.startswith('LOCAL_CS'):
                    # Contains a coordinate system code name — try to parse it
                    import re
                    match = re.search(r'LOCAL_CS\["(.+?)"\]', wkt)
                    if match:
                        cs_code = match.group(1)
                        logger.info(f"Attempting to resolve coordinate system code: {cs_code}")
                        try:
                            crs = CRS.from_user_input(cs_code)
                            logger.info(f"CRS resolved from code: {crs}")
                            return crs
                        except Exception:
                            logger.warning(f"Could not resolve coordinate system code: {cs_code}")
            except Exception as e:
                logger.warning(f"Failed to read .prj file: {e}")

        # Try .json metadata (written by SurfaceExporter)
        json_path = dem_stem.with_suffix('.json')
        if json_path.exists():
            try:
                import json as _json
                meta = _json.loads(json_path.read_text())
                cs = meta.get('coordinate_system', '')
                if cs:
                    try:
                        crs = CRS.from_user_input(cs)
                        logger.info(f"CRS from metadata JSON: {crs}")
                        return crs
                    except Exception:
                        pass
            except Exception:
                pass

        return None

    def _load_config(self, config_file: str) -> dict:
        """Load configuration from JSON file or return defaults."""
        defaults = {
            "depression_method": "breach",  # "breach" or "fill"
            "snap_distance": 10.0,          # meters - distance to snap pour points
            "min_catchment_area": 100.0,    # sq meters - minimum catchment size
            "flow_accumulation_threshold": 100,  # cells for stream definition
        }
        
        if config_file and Path(config_file).exists():
            with open(config_file, 'r') as f:
                user_config = json.load(f)
                defaults.update(user_config)
                
        return defaults
    
    def run(self, dem_file: str, pour_points_file: str, output_file: str) -> str:
        """
        Run the complete catchment delineation workflow.
        
        Args:
            dem_file: Path to input DEM (GeoTIFF)
            pour_points_file: Path to inlet structures (Shapefile/GeoJSON)
            output_file: Path for output catchment polygons
            
        Returns:
            Path to output catchment shapefile
        """
        logger.info("=" * 60)
        logger.info("CATCHMENT DELINEATION TOOL")
        logger.info("=" * 60)
        
        self.dem_path = Path(dem_file)
        self.pour_points_path = Path(pour_points_file)
        output_path = Path(output_file)
        
        # Convert input DEM to GeoTIFF if needed (WhiteboxTools has issues with some BIL files)
        logger.info("\n[Pre-processing] Converting DEM to GeoTIFF for WhiteboxTools compatibility...")
        converted_dem = self.working_dir / "input_dem.tif"
        with rasterio.open(self.dem_path) as src:
            self.crs = src.crs
            self.transform = src.transform
            self.dem_shape = src.shape
            self.cell_size = max(src.res)
            logger.info(f"DEM CRS: {self.crs}")
            logger.info(f"DEM Shape: {self.dem_shape}")
            logger.info(f"DEM Resolution: {src.res}")
            
            # Read data and write as GeoTIFF
            data = src.read(1)
            nodata = src.nodata
            
            # WhiteboxTools requires GeoTIFF with geokeys (CRS info)
            # Try to resolve CRS from multiple sources
            out_crs = self.crs
            if out_crs is None:
                out_crs = self._resolve_crs()
            if out_crs is None:
                # Last resort: use a generic projected placeholder so WhiteboxTools has geokeys.
                # This will not cause spatial errors because the DEM coordinates
                # are already in the drawing's native unit system.
                from rasterio.crs import CRS
                out_crs = CRS.from_epsg(32617)  # UTM zone 17N as placeholder
                logger.warning("No CRS found from .prj or metadata — using generic placeholder. "
                               "Assign a coordinate system in Civil 3D Drawing Settings to avoid this.")
            
            # Create new profile with proper settings
            profile = {
                'driver': 'GTiff',
                'dtype': 'float32',
                'width': src.width,
                'height': src.height,
                'count': 1,
                'crs': out_crs,
                'transform': self.transform,
                'nodata': nodata if nodata is not None else -9999.0,
                'tiled': False,  # Avoid tiling issues
            }
            
            with rasterio.open(converted_dem, 'w', **profile) as dst:
                dst.write(data.astype('float32'), 1)
            
            logger.info(f"Converted to: {converted_dem}")
            
            # Store the CRS we're using
            self.crs = out_crs
        
        # Use converted DEM for processing
        self.dem_path = converted_dem
        
        # Optional: Burn pipes into DEM
        if self.config.get("burn_pipes"):
            logger.info("\n[Pre-processing] Burning pipes into DEM...")
            self.dem_path = self._burn_pipes_into_dem()
        
        # Step 1: Preprocess DEM (fill/breach depressions)
        logger.info("\n[Step 1/6] Preprocessing DEM - removing depressions...")
        conditioned_dem = self._condition_dem()
        
        # Step 2: Calculate flow direction
        logger.info("\n[Step 2/6] Calculating D8 flow direction...")
        flow_dir = self._calculate_flow_direction(conditioned_dem)
        
        # Step 3: Calculate flow accumulation
        logger.info("\n[Step 3/6] Calculating flow accumulation...")
        flow_accum = self._calculate_flow_accumulation(conditioned_dem)
        
        # Step 4: Snap pour points to flow accumulation
        logger.info("\n[Step 4/6] Snapping pour points to flow network...")
        snapped_points = self._snap_pour_points(flow_accum)
        
        # Step 5: Delineate watersheds
        logger.info("\n[Step 5/6] Delineating catchments...")
        watersheds_raster = self._delineate_watersheds(flow_dir, snapped_points)
        
        # Step 6: Convert to vector and link to structures
        logger.info("\n[Step 6/6] Converting to vector polygons...")
        self._raster_to_vector(watersheds_raster, snapped_points, output_path)
        
        logger.info("\n" + "=" * 60)
        logger.info(f"COMPLETE! Output: {output_path}")
        logger.info("=" * 60)
        
        return str(output_path)
    
    def _burn_pipes_into_dem(self) -> str:
        """
        Burn pipe network into DEM to enforce flow along pipe paths.
        
        This lowers the elevation along pipe centerlines to ensure
        water flows towards the pipe network.
        
        Returns:
            Path to burned DEM
        """
        import json
        from shapely.geometry import LineString
        from rasterio.features import rasterize
        
        pipe_file = self.config.get("burn_pipes")
        burn_depth = self.config.get("burn_depth", 3.0)
        
        if not pipe_file or not Path(pipe_file).exists():
            logger.warning(f"Pipe file not found: {pipe_file}")
            return str(self.dem_path)
        
        # Read pipe lines from GeoJSON
        with open(pipe_file, 'r') as f:
            pipe_data = json.load(f)
        
        features = pipe_data.get('features', [])
        logger.info(f"Burning {len(features)} pipes into DEM (depth: {burn_depth} ft)")
        
        if not features:
            logger.warning("No pipes to burn")
            return str(self.dem_path)
        
        # Read the DEM
        with rasterio.open(self.dem_path) as src:
            dem_data = src.read(1)
            profile = src.profile.copy()
            transform = src.transform
            nodata = src.nodata
        
        # Create pipe geometries with buffer
        pipe_shapes = []
        for feature in features:
            coords = feature['geometry']['coordinates']
            if len(coords) >= 2:
                line = LineString(coords)
                # Buffer by 1 cell width to ensure coverage
                cell_size = abs(transform[0])
                buffered = line.buffer(cell_size * 1.5)
                pipe_shapes.append((buffered, 1))
        
        if pipe_shapes:
            # Rasterize pipe shapes to create a mask
            pipe_mask = rasterize(
                pipe_shapes,
                out_shape=dem_data.shape,
                transform=transform,
                fill=0,
                dtype='uint8'
            )
            
            # Lower elevation where pipes exist
            burned_dem = dem_data.copy()
            pipe_cells = pipe_mask > 0
            burned_dem[pipe_cells] = burned_dem[pipe_cells] - burn_depth
            
            # Make sure we don't go below nodata
            if nodata is not None:
                burned_dem[burned_dem <= nodata] = nodata + 0.1
            
            burned_cells = np.sum(pipe_cells)
            logger.info(f"Burned {burned_cells:,} cells")
            
            # Save burned DEM
            output_path = self.working_dir / "dem_burned.tif"
            with rasterio.open(output_path, 'w', **profile) as dst:
                dst.write(burned_dem.astype('float32'), 1)
            
            logger.info(f"Burned DEM saved to: {output_path}")
            return str(output_path)
        
        return str(self.dem_path)
    
    def _condition_dem(self) -> str:
        """
        Remove depressions from DEM using breach or fill method.
        
        Returns:
            Path to conditioned DEM
        """
        output = str(self.working_dir / "dem_conditioned.tif")
        
        if self.config["depression_method"] == "breach":
            breach_dist = self.config.get("breach_distance", 10)
            logger.info(f"Using breach depressions method (dist={breach_dist})...")
            self.wbt.breach_depressions_least_cost(
                dem=str(self.dem_path),
                output=output,
                dist=breach_dist,  # max breach distance
                fill=True  # fill remaining depressions
            )
        else:
            logger.info("Using fill depressions method...")
            self.wbt.fill_depressions(
                dem=str(self.dem_path),
                output=output,
                fix_flats=True
            )
            
        return output
    
    def _calculate_flow_direction(self, dem: str) -> str:
        """
        Calculate D8 flow direction raster.
        
        Args:
            dem: Path to conditioned DEM
            
        Returns:
            Path to flow direction raster
        """
        output = str(self.working_dir / "flow_direction.tif")
        
        self.wbt.d8_pointer(
            dem=dem,
            output=output,
            esri_pntr=False  # Use WhiteboxTools pointer convention
        )
        
        return output
    
    def _calculate_flow_accumulation(self, dem: str) -> str:
        """
        Calculate flow accumulation raster.
        
        Args:
            dem: Path to conditioned DEM
            
        Returns:
            Path to flow accumulation raster
        """
        output = str(self.working_dir / "flow_accumulation.tif")
        
        self.wbt.d8_flow_accumulation(
            i=dem,
            output=output,
            out_type="cells",
            log=False,
            clip=False
        )
        
        return output
    
    def _snap_pour_points(self, flow_accum: str) -> str:
        """
        Snap pour points to cells with highest flow accumulation.
        
        Args:
            flow_accum: Path to flow accumulation raster
            
        Returns:
            Path to snapped pour points shapefile
        """
        output = str(self.working_dir / "pour_points_snapped.shp")
        snap_dist = self.config["snap_distance"]
        
        logger.info(f"Snap distance: {snap_dist} map units")
        
        # Convert GeoJSON to shapefile for WhiteboxTools compatibility
        pour_pts_shp = str(self.working_dir / "pour_points_input.shp")
        self._convert_geojson_to_shapefile(str(self.pour_points_path), pour_pts_shp)
        
        self.wbt.snap_pour_points(
            pour_pts=pour_pts_shp,
            flow_accum=flow_accum,
            output=output,
            snap_dist=snap_dist
        )
        
        return output
    
    def _convert_geojson_to_shapefile(self, geojson_path: str, shapefile_path: str):
        """
        Convert GeoJSON to shapefile for WhiteboxTools compatibility.
        """
        import json
        from shapely.geometry import shape, Point
        import fiona
        from fiona.crs import from_epsg
        
        logger.info(f"Converting GeoJSON to shapefile: {shapefile_path}")
        
        with open(geojson_path, 'r') as f:
            geojson = json.load(f)
        
        features = geojson.get('features', [])
        logger.info(f"Found {len(features)} features to convert")
        
        if not features:
            raise ValueError("No features found in GeoJSON file")
        
        # Define schema based on first feature properties
        first_props = features[0].get('properties', {})
        schema = {
            'geometry': 'Point',
            'properties': {k: 'str' for k in first_props.keys()}
        }
        
        # Write shapefile
        with fiona.open(shapefile_path, 'w', driver='ESRI Shapefile', 
                        schema=schema, crs=self.crs) as dst:
            for feature in features:
                geom = shape(feature['geometry'])
                props = {k: str(v) if v is not None else '' 
                         for k, v in feature.get('properties', {}).items()}
                dst.write({
                    'geometry': geom.__geo_interface__,
                    'properties': props
                })
    
    def _delineate_watersheds(self, flow_dir: str, pour_points: str) -> str:
        """
        Delineate watersheds for each pour point.
        
        Args:
            flow_dir: Path to D8 flow direction raster
            pour_points: Path to snapped pour points
            
        Returns:
            Path to watershed raster
        """
        output = str(self.working_dir / "watersheds.tif")
        
        self.wbt.watershed(
            d8_pntr=flow_dir,
            pour_pts=pour_points,
            output=output,
            esri_pntr=False
        )
        
        return output
    
    def _raster_to_vector(self, watersheds_raster: str, snapped_points: str, 
                          output_path: Path) -> None:
        """
        Convert watershed raster to vector polygons and link to structures.
        
        Args:
            watersheds_raster: Path to watershed raster
            snapped_points: Path to snapped pour points (contains structure IDs)
            output_path: Output shapefile path
        """
        # Read the watershed raster
        with rasterio.open(watersheds_raster) as src:
            data = src.read(1)
            transform = src.transform
            crs = src.crs
            nodata = src.nodata
        
        # Debug: Show watershed raster statistics
        unique_values = np.unique(data)
        logger.info(f"Watershed raster unique values: {unique_values[:20]}{'...' if len(unique_values) > 20 else ''}")
        logger.info(f"Watershed raster nodata: {nodata}")
        
        # Read the snapped pour points to get structure IDs
        pour_points_gdf = gpd.read_file(snapped_points)
        logger.info(f"Pour points columns: {list(pour_points_gdf.columns)}")
        
        # Create a mapping from watershed ID to structure info
        # WhiteboxTools assigns watershed IDs based on pour point order (1-indexed)
        # Note: Shapefile field names are truncated to 10 chars, so we check both
        watershed_to_structure = {}
        for idx, row in pour_points_gdf.iterrows():
            watershed_id = idx + 1  # WhiteboxTools uses 1-based indexing
            
            # Handle truncated shapefile field names
            struct_id = (row.get('structure_id') or row.get('structure_') or 
                        row.get('struct_id') or f'inlet_{idx}')
            struct_name = (row.get('name') or row.get('struct_nam') or 
                          row.get('struct_name') or f'Inlet {idx}')
            
            watershed_to_structure[watershed_id] = {
                'structure_id': struct_id,
                'structure_name': struct_name,
                'x': row.geometry.x,
                'y': row.geometry.y
            }
            logger.info(f"  Watershed {watershed_id} -> {struct_name} ({struct_id})")
            
        logger.info(f"Mapped {len(watershed_to_structure)} watersheds to structures")
        logger.info(f"Expected watershed IDs: {list(watershed_to_structure.keys())}")
        
        # Convert raster to vector polygons
        logger.info("Vectorizing watersheds...")
        
        # Mask nodata values - also exclude 0 which is typically background
        if nodata is not None:
            mask = (data != nodata) & (data != 0)
        else:
            mask = data != 0
        
        # Debug: count valid cells
        valid_cells = np.sum(mask)
        logger.info(f"Valid watershed cells (non-zero, non-nodata): {valid_cells:,}")
        
        # Extract shapes
        results = []
        all_watershed_ids_found = set()
        skipped_small = 0
        skipped_unmatched = 0
        
        for geom, value in shapes(data.astype(np.int32), mask=mask, transform=transform):
            watershed_id = int(value)
            all_watershed_ids_found.add(watershed_id)
            
            if watershed_id in watershed_to_structure:
                structure_info = watershed_to_structure[watershed_id]
                polygon = shape(geom)
                
                # Calculate area
                area = polygon.area
                
                # Skip tiny catchments
                if area < self.config["min_catchment_area"]:
                    logger.debug(f"Skipping small catchment for {structure_info['structure_name']}: {area:.1f} sq units")
                    skipped_small += 1
                    continue
                
                results.append({
                    'geometry': polygon,
                    'watershed_id': watershed_id,
                    'structure_id': structure_info['structure_id'],
                    'struct_name': structure_info['structure_name'],
                    'struct_x': structure_info['x'],
                    'struct_y': structure_info['y'],
                    'area': area
                })
            else:
                skipped_unmatched += 1
        
        logger.info(f"Watershed IDs found in raster: {sorted(all_watershed_ids_found)}")
        if skipped_small > 0:
            logger.info(f"Skipped {skipped_small} catchments below min area ({self.config['min_catchment_area']} sq units)")
        if skipped_unmatched > 0:
            logger.info(f"Skipped {skipped_unmatched} polygons with unmatched watershed IDs")
        
        # FALLBACK: If no results but we have unmatched watershed IDs, try remapping
        if not results and all_watershed_ids_found:
            logger.warning("Attempting fallback: remapping watershed IDs to structures...")
            
            # Get the actual watershed IDs (excluding 0 and nodata)
            actual_ids = sorted([wid for wid in all_watershed_ids_found if wid > 0])
            structure_list = list(watershed_to_structure.values())
            
            if actual_ids and structure_list:
                # Create new mapping: actual_ids -> structures (in order)
                new_mapping = {}
                for i, wid in enumerate(actual_ids):
                    if i < len(structure_list):
                        new_mapping[wid] = structure_list[i]
                        logger.info(f"  Remapped watershed {wid} -> {structure_list[i]['structure_name']}")
                
                # Re-extract with new mapping
                for geom, value in shapes(data.astype(np.int32), mask=mask, transform=transform):
                    watershed_id = int(value)
                    if watershed_id in new_mapping:
                        structure_info = new_mapping[watershed_id]
                        polygon = shape(geom)
                        area = polygon.area
                        
                        if area >= self.config["min_catchment_area"]:
                            results.append({
                                'geometry': polygon,
                                'watershed_id': watershed_id,
                                'structure_id': structure_info['structure_id'],
                                'struct_name': structure_info['structure_name'],
                                'struct_x': structure_info['x'],
                                'struct_y': structure_info['y'],
                                'area': area
                            })
                
                if results:
                    logger.info(f"Fallback successful! Created {len(results)} catchments")
        
        # Ensure output directory exists
        output_path.parent.mkdir(parents=True, exist_ok=True)
        geojson_path = output_path.with_suffix('.json')
        
        # Create GeoDataFrame
        if results:
            gdf = gpd.GeoDataFrame(results, crs=crs)
            
            # Smooth raster stairstep edges using morphological open/close:
            # buffer(r) rounds convex corners; buffer(-r) restores size but keeps rounded concave corners.
            # Douglas-Peucker (simplify) does NOT work here — 90° staircase corners are
            # large deviations and will be retained by D-P regardless of tolerance.
            cell_size = getattr(self, 'cell_size', 1.0)
            smooth_r = self.config.get("smooth_radius") or (cell_size * 1.5)
            if smooth_r > 0:
                logger.info(f"Smoothing polygon edges (radius={smooth_r}, cell_size={cell_size})...")
                gdf['geometry'] = gdf['geometry'].buffer(smooth_r).buffer(-smooth_r)
            
            # Save to shapefile
            gdf.to_file(output_path)
            
            # Also save as GeoJSON for easier import into Civil 3D
            gdf.to_file(geojson_path, driver='GeoJSON')
            logger.info(f"Written GeoJSON: {geojson_path}")
            
            logger.info(f"Created {len(gdf)} catchment polygons")
            logger.info(f"Total catchment area: {gdf['area'].sum():,.1f} sq units")
        else:
            logger.warning("No valid catchments created!")
            logger.warning("Possible causes:")
            logger.warning("  - All catchments below minimum area threshold")
            logger.warning("  - Watershed IDs don't match pour point indices")
            logger.warning("  - DEM doesn't have proper drainage to pour points")
            
            # Create empty GeoJSON so the C# code doesn't fail looking for it
            empty_geojson = {
                "type": "FeatureCollection",
                "features": []
            }
            with open(geojson_path, 'w') as f:
                json.dump(empty_geojson, f)
            logger.info(f"Written empty GeoJSON: {geojson_path}")
            

def main():
    """Main entry point for command-line usage."""
    import argparse
    
    parser = argparse.ArgumentParser(
        description='Delineate catchments from DEM and inlet structures'
    )
    parser.add_argument(
        '--dem', '-d',
        required=True,
        help='Path to input DEM (GeoTIFF)'
    )
    parser.add_argument(
        '--inlets', '-i',
        required=True,
        help='Path to inlet structures (Shapefile/GeoJSON)'
    )
    parser.add_argument(
        '--output', '-o',
        required=True,
        help='Path for output catchment polygons'
    )
    parser.add_argument(
        '--config', '-c',
        help='Path to JSON configuration file'
    )
    parser.add_argument(
        '--working-dir', '-w',
        default='./temp',
        help='Working directory for intermediate files'
    )
    parser.add_argument(
        '--snap-distance',
        type=float,
        default=10.0,
        help='Distance to snap pour points to flow network (default: 10)'
    )
    parser.add_argument(
        '--breach-distance',
        type=int,
        default=10,
        help='Maximum breach distance for depression removal (default: 10)'
    )
    parser.add_argument(
        '--depression-method',
        choices=['breach', 'fill'],
        default='breach',
        help='Method for handling depressions (default: breach)'
    )
    parser.add_argument(
        '--min-area',
        type=float,
        default=100.0,
        help='Minimum catchment area in sq units (default: 100)'
    )
    parser.add_argument(
        '--simplify-tolerance',
        type=float,
        default=1.0,
        help='Tolerance for polygon simplification, 0=none (default: 1)'
    )
    parser.add_argument(
        '--burn-pipes',
        help='Path to pipe lines GeoJSON for burning into DEM'
    )
    parser.add_argument(
        '--burn-depth',
        type=float,
        default=3.0,
        help='Depth to burn pipes into DEM (default: 3)'
    )
    
    args = parser.parse_args()
    
    # Build config from command-line args
    config = {
        "depression_method": args.depression_method,
        "snap_distance": args.snap_distance,
        "breach_distance": args.breach_distance,
        "min_catchment_area": args.min_area,
        "simplify_tolerance": args.simplify_tolerance,
        "burn_pipes": args.burn_pipes,
        "burn_depth": args.burn_depth,
    }
    
    # Run delineation
    delineator = CatchmentDelineator(
        working_dir=args.working_dir,
        config_file=args.config
    )
    
    # Override config with command-line args
    delineator.config.update(config)
    
    output = delineator.run(
        dem_file=args.dem,
        pour_points_file=args.inlets,
        output_file=args.output
    )
    
    print(f"\nOutput saved to: {output}")


if __name__ == "__main__":
    main()

