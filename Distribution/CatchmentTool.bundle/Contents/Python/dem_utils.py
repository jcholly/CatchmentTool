"""
DEM Utilities for Civil 3D Catchment Delineation Tool

Converts between Civil 3D BIL export format and GeoTIFF for WhiteboxTools.
"""

import os
import json
import struct
import logging
from pathlib import Path

import numpy as np
import rasterio
from rasterio.transform import from_bounds
from rasterio.crs import CRS

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)


def read_bil_header(hdr_path: str) -> dict:
    """
    Read ESRI BIL header file.
    
    Args:
        hdr_path: Path to .hdr file
        
    Returns:
        Dictionary of header values
    """
    header = {}
    
    with open(hdr_path, 'r') as f:
        for line in f:
            parts = line.strip().split()
            if len(parts) >= 2:
                key = parts[0].upper()
                value = ' '.join(parts[1:])
                
                # Convert numeric values
                if key in ('NROWS', 'NCOLS', 'NBANDS', 'NBITS'):
                    header[key] = int(value)
                elif key in ('ULXMAP', 'ULYMAP', 'XDIM', 'YDIM', 'NODATA'):
                    header[key] = float(value)
                else:
                    header[key] = value
                    
    return header


def bil_to_geotiff(bil_path: str, output_path: str, crs: str = None) -> str:
    """
    Convert Civil 3D BIL export to GeoTIFF.
    
    Args:
        bil_path: Path to .bil file
        output_path: Path for output .tif file
        crs: Optional CRS string (e.g., 'EPSG:2277')
        
    Returns:
        Path to output GeoTIFF
    """
    logger.info(f"Converting BIL to GeoTIFF: {bil_path}")
    
    # Read header
    hdr_path = Path(bil_path).with_suffix('.hdr')
    if not hdr_path.exists():
        raise FileNotFoundError(f"Header file not found: {hdr_path}")
    
    header = read_bil_header(str(hdr_path))
    
    rows = header['NROWS']
    cols = header['NCOLS']
    nodata = header.get('NODATA', -9999)
    
    logger.info(f"  Dimensions: {cols} x {rows}")
    logger.info(f"  NoData value: {nodata}")
    
    # Read binary data
    with open(bil_path, 'rb') as f:
        data = np.frombuffer(f.read(), dtype=np.float32)
        data = data.reshape((rows, cols))
    
    # Build transform from header
    # ULXMAP/ULYMAP are the center of the upper-left cell
    cell_size_x = header['XDIM']
    cell_size_y = header['YDIM']
    
    # Adjust to get the upper-left corner of the upper-left cell
    origin_x = header['ULXMAP'] - (cell_size_x / 2)
    origin_y = header['ULYMAP'] + (cell_size_y / 2)
    
    transform = rasterio.transform.from_origin(
        west=origin_x,
        north=origin_y,
        xsize=cell_size_x,
        ysize=cell_size_y
    )
    
    # Determine CRS
    output_crs = None
    if crs:
        output_crs = CRS.from_string(crs)
    else:
        # Check for .prj file
        prj_path = Path(bil_path).with_suffix('.prj')
        if prj_path.exists():
            with open(prj_path, 'r') as f:
                wkt = f.read()
                output_crs = CRS.from_wkt(wkt)
    
    # Write GeoTIFF
    with rasterio.open(
        output_path,
        'w',
        driver='GTiff',
        height=rows,
        width=cols,
        count=1,
        dtype=np.float32,
        crs=output_crs,
        transform=transform,
        nodata=nodata,
        compress='lzw'
    ) as dst:
        dst.write(data, 1)
    
    logger.info(f"  Written: {output_path}")
    
    # Also check for companion JSON metadata
    json_path = Path(bil_path).with_suffix('.json')
    if json_path.exists():
        with open(json_path, 'r') as f:
            metadata = json.load(f)
            logger.info(f"  Surface: {metadata.get('surface_name', 'Unknown')}")
            logger.info(f"  Cell size: {metadata.get('cell_size', 'Unknown')}")
    
    return output_path


def geojson_to_shapefile(geojson_path: str, output_path: str) -> str:
    """
    Convert GeoJSON to Shapefile for WhiteboxTools.
    
    Args:
        geojson_path: Path to .json or .geojson file
        output_path: Path for output .shp file
        
    Returns:
        Path to output shapefile
    """
    import geopandas as gpd
    
    logger.info(f"Converting GeoJSON to Shapefile: {geojson_path}")
    
    gdf = gpd.read_file(geojson_path)
    gdf.to_file(output_path)
    
    logger.info(f"  Written: {output_path}")
    logger.info(f"  Features: {len(gdf)}")
    
    return output_path


def prepare_inputs(working_dir: str, crs: str = None) -> dict:
    """
    Prepare all inputs for catchment delineation.
    
    Looks for Civil 3D exports and converts to formats needed by WhiteboxTools.
    
    Args:
        working_dir: Working directory with Civil 3D exports
        crs: Optional CRS string
        
    Returns:
        Dictionary with paths to prepared files
    """
    working_dir = Path(working_dir)
    result = {}
    
    logger.info(f"Preparing inputs in: {working_dir}")
    
    # Find and convert DEM
    bil_files = list(working_dir.glob('*.bil'))
    if bil_files:
        bil_path = bil_files[0]
        tif_path = working_dir / 'dem.tif'
        result['dem'] = bil_to_geotiff(str(bil_path), str(tif_path), crs)
    else:
        # Check for existing TIF
        tif_files = list(working_dir.glob('*.tif'))
        if tif_files:
            result['dem'] = str(tif_files[0])
        else:
            raise FileNotFoundError("No DEM file found (*.bil or *.tif)")
    
    # Find and convert pour points
    json_files = [f for f in working_dir.glob('*structures*.json') 
                  if 'workflow' not in f.name.lower()]
    
    if json_files:
        json_path = json_files[0]
        shp_path = working_dir / 'pour_points.shp'
        result['pour_points'] = geojson_to_shapefile(str(json_path), str(shp_path))
    else:
        # Check for existing SHP
        shp_files = list(working_dir.glob('*structures*.shp')) + list(working_dir.glob('*inlet*.shp'))
        if shp_files:
            result['pour_points'] = str(shp_files[0])
        else:
            raise FileNotFoundError("No pour points file found")
    
    # Set output path
    result['output'] = str(working_dir / 'catchments.shp')
    
    logger.info("Input preparation complete:")
    for key, value in result.items():
        logger.info(f"  {key}: {value}")
    
    return result


def main():
    """Command-line interface for DEM utilities."""
    import argparse
    
    parser = argparse.ArgumentParser(description='DEM utilities for catchment delineation')
    
    subparsers = parser.add_subparsers(dest='command', help='Command to run')
    
    # bil2tif command
    bil2tif = subparsers.add_parser('bil2tif', help='Convert BIL to GeoTIFF')
    bil2tif.add_argument('input', help='Input BIL file')
    bil2tif.add_argument('output', help='Output GeoTIFF file')
    bil2tif.add_argument('--crs', help='CRS string (e.g., EPSG:2277)')
    
    # json2shp command
    json2shp = subparsers.add_parser('json2shp', help='Convert GeoJSON to Shapefile')
    json2shp.add_argument('input', help='Input GeoJSON file')
    json2shp.add_argument('output', help='Output Shapefile')
    
    # prepare command
    prepare = subparsers.add_parser('prepare', help='Prepare all inputs for delineation')
    prepare.add_argument('working_dir', help='Working directory with Civil 3D exports')
    prepare.add_argument('--crs', help='CRS string (e.g., EPSG:2277)')
    
    args = parser.parse_args()
    
    if args.command == 'bil2tif':
        bil_to_geotiff(args.input, args.output, args.crs)
    elif args.command == 'json2shp':
        geojson_to_shapefile(args.input, args.output)
    elif args.command == 'prepare':
        prepare_inputs(args.working_dir, args.crs)
    else:
        parser.print_help()


if __name__ == '__main__':
    main()

