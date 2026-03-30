"""
CLI entry point for TR-55 Time of Concentration calculation.

Called by the C# Civil 3D plugin via subprocess:
    python run_tc.py --dem surface_dem.bil --catchments catchments.json --output tc_results.json

Reads a DEM + catchment polygons, computes Tc per catchment, writes JSON results.
"""

import argparse
import json
import logging
import sys
from pathlib import Path

from tc_calculator import TcCalculator

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)


def main():
    parser = argparse.ArgumentParser(
        description='TR-55 Time of Concentration Calculator'
    )
    parser.add_argument('--dem', '-d', required=True,
                        help='Path to input DEM (GeoTIFF or BIL from Civil 3D)')
    parser.add_argument('--catchments', '-c', required=True,
                        help='Path to catchment polygons (GeoJSON or Shapefile)')
    parser.add_argument('--output', '-o', required=True,
                        help='Path for output Tc results JSON')
    parser.add_argument('--working-dir', '-w', default=None,
                        help='Working directory for intermediate files')
    parser.add_argument('--crs',
                        help='CRS override (e.g., EPSG:2277)')
    parser.add_argument('--drawing-units', choices=['ft', 'm'], default='ft',
                        help='Drawing linear units (default: ft)')

    # TR-55 parameters
    parser.add_argument('--mannings-n-sheet', type=float, default=None,
                        help='Manning\'s n for sheet flow (default: 0.15)')
    parser.add_argument('--mannings-n-channel', type=float, default=None,
                        help='Manning\'s n for channel flow (default: 0.05)')
    parser.add_argument('--p2-rainfall', type=float, default=None,
                        help='2-year, 24-hour rainfall depth in inches (default: 3.5)')
    parser.add_argument('--hydraulic-radius', type=float, default=None,
                        help='Hydraulic radius in feet (default: auto from drainage area)')
    parser.add_argument('--sheet-flow-length', type=float, default=100,
                        help='Max sheet flow length in feet (default: 100)')
    parser.add_argument('--channel-threshold', type=float, default=5.0,
                        help='Channel flow threshold in acres (default: 5.0)')
    parser.add_argument('--is-paved', action='store_true',
                        help='Use paved surface coefficients for shallow flow')

    # DEM processing
    parser.add_argument('--depression-method', choices=['breach', 'fill'],
                        default='breach',
                        help='Depression handling method (default: breach)')
    parser.add_argument('--breach-distance', type=int, default=10,
                        help='Max breach distance (default: 10)')

    # Config file (overrides individual args)
    parser.add_argument('--config', default=None,
                        help='Path to JSON config file')

    args = parser.parse_args()

    # Build config dict
    config = {
        "drawing_units": args.drawing_units,
        "is_metric": args.drawing_units == "m",
        "depression_method": args.depression_method,
        "breach_distance": args.breach_distance,
        "sheet_flow_length_ft": args.sheet_flow_length,
        "channel_threshold_acres": args.channel_threshold,
        "is_paved": args.is_paved,
    }

    # Optional overrides
    if args.mannings_n_sheet is not None:
        config["mannings_n_sheet"] = args.mannings_n_sheet
    if args.mannings_n_channel is not None:
        config["mannings_n_channel"] = args.mannings_n_channel
    if args.p2_rainfall is not None:
        config["p2_rainfall_inches"] = args.p2_rainfall
    if args.hydraulic_radius is not None:
        config["hydraulic_radius_ft"] = args.hydraulic_radius

    # Load config file if provided (overrides CLI args)
    if args.config and Path(args.config).exists():
        with open(args.config) as f:
            file_config = json.load(f)
        config.update(file_config)

    # Determine working directory
    if args.working_dir:
        working_dir = args.working_dir
    else:
        working_dir = str(Path(args.output).parent / "tc_working")

    # Run
    calculator = TcCalculator(working_dir=working_dir, config=config)
    results = calculator.run(
        dem_path=args.dem,
        catchments_path=args.catchments,
        user_crs=args.crs,
    )

    # Copy results to requested output path
    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, 'w') as f:
        json.dump(results, f, indent=2)

    # Print summary to stdout for C# to capture
    print(f"\nTC_RESULTS_FILE={output_path}")
    for r in results:
        tc = r.get("tcMinutes", -1)
        cid = r.get("catchmentId", "?")
        if tc >= 0:
            print(f"  {cid}: Tc = {tc:.1f} min")
        else:
            print(f"  {cid}: ERROR - {r.get('error', 'unknown')}")

    print(f"\nProcessed {len(results)} catchments")


if __name__ == "__main__":
    main()
