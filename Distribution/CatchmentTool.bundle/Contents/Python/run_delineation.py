"""
Automated Catchment Delineation Runner

This script automates the complete workflow:
1. Prepare inputs (convert BIL to GeoTIFF, GeoJSON to Shapefile)
2. Run WhiteboxTools catchment delineation
3. Output results in formats ready for Civil 3D import
"""

import os
import sys
import json
import logging
from pathlib import Path

from dem_utils import prepare_inputs, bil_to_geotiff, geojson_to_shapefile
from catchment_delineation import CatchmentDelineator, gis_workspace

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)


def run_workflow(working_dir: str, crs: str = None, config_file: str = None,
                 drawing_units: str = "ft", retain_intermediates: bool = False):
    """
    Run the complete catchment delineation workflow.

    Args:
        working_dir: Directory containing Civil 3D exports
        crs: Optional CRS string (e.g., 'EPSG:2277')
        config_file: Optional configuration file path
        drawing_units: Drawing linear units ('ft' or 'm')
        retain_intermediates: Keep intermediate files for debugging
    """
    working_dir = Path(working_dir)

    logger.info("=" * 70)
    logger.info("CATCHMENT DELINEATION WORKFLOW")
    logger.info("=" * 70)
    logger.info(f"Working directory: {working_dir}")

    # Check for workflow config from Civil 3D
    workflow_config = {}
    workflow_config_path = working_dir / 'workflow_config.json'
    if workflow_config_path.exists():
        logger.info(f"Found workflow config: {workflow_config_path}")
        with open(workflow_config_path, 'r') as f:
            workflow_config = json.load(f)
            logger.info(f"  Surface: {workflow_config.get('surface', {}).get('name', 'Unknown')}")
            logger.info(f"  Network: {workflow_config.get('pipe_network', {}).get('name', 'Unknown')}")

            # Read drawing units from workflow config if available
            if 'drawing_units' in workflow_config and drawing_units == "ft":
                drawing_units = workflow_config['drawing_units']
                logger.info(f"  Drawing units (from config): {drawing_units}")

    # Step 1: Prepare inputs
    logger.info("\n--- STEP 1: Preparing Inputs ---")
    try:
        inputs = prepare_inputs(str(working_dir), crs)
    except FileNotFoundError as e:
        logger.error(f"Input preparation failed: {e}")
        logger.error("Make sure you've run EXPORTCATCHMENTDATA in Civil 3D first.")
        sys.exit(1)

    # Step 2: Run delineation with managed workspace
    logger.info("\n--- STEP 2: Running Catchment Delineation ---")

    with gis_workspace(
        retain=retain_intermediates,
        workspace_dir=working_dir / 'temp',
    ) as temp_dir:
        delineator = CatchmentDelineator(
            working_dir=str(temp_dir),
            config_file=config_file
        )

        # Pass drawing units to config
        delineator.config["drawing_units"] = drawing_units

        output_shp = inputs['output']
        output_json = str(Path(output_shp).with_suffix('.json'))

        result = delineator.run(
            dem_file=inputs['dem'],
            pour_points_file=inputs['pour_points'],
            output_file=output_shp,
            user_crs=crs,
        )

    # Step 3: Summary
    logger.info("\n--- STEP 3: Summary ---")
    import geopandas as gpd

    geojson_path = Path(output_shp).with_suffix('.json')
    if geojson_path.exists():
        gdf = gpd.read_file(geojson_path)
        if len(gdf) > 0:
            logger.info("\n" + "=" * 70)
            logger.info("WORKFLOW COMPLETE")
            logger.info("=" * 70)
            logger.info(f"Catchments created: {len(gdf)}")
            logger.info(f"Total area: {gdf['area'].sum():,.1f} sq units")
            logger.info(f"\nOutput files:")
            logger.info(f"  Shapefile: {output_shp}")
            logger.info(f"  GeoJSON:   {geojson_path}")
            logger.info(f"\nNext step: In Civil 3D, run IMPORTCATCHMENTS")
            logger.info(f"  and select: {geojson_path}")
        else:
            logger.error("No catchments were created. Check warnings above.")
            sys.exit(1)
    else:
        logger.error("Catchment delineation failed - no output created")
        sys.exit(1)


def main():
    """Command-line interface."""
    import argparse

    parser = argparse.ArgumentParser(
        description='Run complete catchment delineation workflow',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Example usage:
  python run_delineation.py C:/path/to/CatchmentData
  python run_delineation.py . --crs EPSG:2277
  python run_delineation.py ./data --config config.json
  python run_delineation.py ./data --drawing-units m --min-area 0.2 --area-unit hectares
        """
    )

    parser.add_argument('working_dir',
                        help='Working directory containing Civil 3D exports')
    parser.add_argument('--crs', '-c',
                        help='Coordinate Reference System (e.g., EPSG:2277)')
    parser.add_argument('--config', '-f',
                        help='Configuration JSON file')
    parser.add_argument('--drawing-units', choices=['ft', 'm'], default='ft',
                        help='Drawing linear units (default: ft)')
    parser.add_argument('--retain-intermediates', action='store_true',
                        help='Keep intermediate files for debugging')

    args = parser.parse_args()

    run_workflow(
        working_dir=args.working_dir,
        crs=args.crs,
        config_file=args.config,
        drawing_units=args.drawing_units,
        retain_intermediates=args.retain_intermediates,
    )


if __name__ == '__main__':
    main()
