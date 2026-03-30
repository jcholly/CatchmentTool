"""
TR-55 Time of Concentration Calculator

Calculates Tc using the NRCS TR-55 segmented method:
  Total Tc = Tt_sheet + Tt_shallow + Tt_channel

Workflow:
1. Takes a conditioned DEM (from catchment delineation or fresh export)
2. Runs WhiteboxTools to extract longest flow path per catchment
3. Samples flow accumulation along the path to determine contributing area
4. Segments the path into sheet / shallow concentrated / channel flow
5. Computes travel time for each segment using TR-55 equations
6. Returns JSON results per catchment

Designed to be called from Civil 3D via subprocess (run_tc.py entry point).
"""

import json
import logging
import math
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import rasterio
import geopandas as gpd
import whitebox
from shapely.geometry import LineString, Point, shape
from shapely.ops import nearest_points

from crs_utils import resolve_crs, get_linear_unit, is_metric_unit

logger = logging.getLogger(__name__)


# ============================================================================
# Constants — TR-55 (Technical Release 55, 1986)
# ============================================================================

# Sheet flow — TR-55 Equation 3-3
SHEET_FLOW_CONSTANT = 0.007
SHEET_FLOW_EXP_NL = 0.8
SHEET_FLOW_EXP_P2 = 0.5
SHEET_FLOW_EXP_S = 0.4

# Sheet flow limits
SHEET_FLOW_MAX_LENGTH_FT = 300   # TR-55 hard max
SHEET_FLOW_DEFAULT_LENGTH_FT = 100  # TR-55 practical guidance
SHEET_FLOW_MAX_CONTRIBUTING_AREA_ACRES = 0.5

# Shallow concentrated flow — TR-55 Figure 3-1
SHALLOW_UNPAVED_COEFF = 16.1345
SHALLOW_PAVED_COEFF = 20.3282

# Channel flow threshold
CHANNEL_THRESHOLD_ACRES = 5.0

# Manning's equation unit conversion (USCS)
MANNINGS_USCS_FACTOR = 1.49

# Minimum slope floor (prevents divide-by-zero)
MIN_SLOPE = 0.001

# Unit conversions
METERS_TO_FEET = 3.28084
SQ_FT_PER_ACRE = 43560
FEET_PER_MILE = 5280

# Default hydraulic radius by drainage area (acres)
HYDRAULIC_RADIUS_TABLE = [
    (1,    0.2),   # Small swale
    (5,    0.4),   # Minor swale
    (20,   0.8),   # Roadside ditch
    (100,  1.5),   # Major ditch
    (500,  2.5),   # Small channel
    (1e12, 4.0),   # Large channel
]

# Default Manning's n values
DEFAULT_SHEET_N = 0.15
DEFAULT_CHANNEL_N = 0.05
DEFAULT_HYDRAULIC_RADIUS_FT = 0.5
DEFAULT_P2_INCHES = 3.5  # Fallback if NOAA Atlas 14 unavailable

# Manning's n for sheet flow — TR-55 Table 3-1 (keyed by NLCD code)
NLCD_MANNINGS_N = {
    11: 0.011,  # Open Water
    21: 0.15,   # Developed, Open Space
    22: 0.10,   # Developed, Low Intensity
    23: 0.05,   # Developed, Medium Intensity
    24: 0.011,  # Developed, High Intensity
    31: 0.05,   # Barren Land
    41: 0.40,   # Deciduous Forest
    42: 0.55,   # Evergreen Forest
    43: 0.45,   # Mixed Forest
    52: 0.13,   # Shrub/Scrub
    71: 0.15,   # Grassland/Herbaceous
    81: 0.06,   # Pasture/Hay
    82: 0.06,   # Cultivated Crops
    90: 0.24,   # Woody Wetlands
    95: 0.24,   # Emergent Herbaceous Wetlands
}


# ============================================================================
# Data classes
# ============================================================================

@dataclass
class TcSegment:
    """One segment of the flow path (sheet, shallow, or channel)."""
    type: str
    length_ft: float
    slope_ft_ft: float
    travel_time_hours: float
    inputs: dict = field(default_factory=dict)

    @property
    def travel_time_minutes(self) -> float:
        return self.travel_time_hours * 60.0


@dataclass
class TcResult:
    """Tc calculation result for one catchment."""
    catchment_id: str
    tc_minutes: float
    tc_hours: float
    method: str = "TR-55 Segmented"
    flow_path_length_ft: float = 0.0
    elevation_change_ft: float = 0.0
    avg_slope_percent: float = 0.0
    segments: list = field(default_factory=list)

    def to_dict(self) -> dict:
        return {
            "catchmentId": self.catchment_id,
            "tcMinutes": round(self.tc_minutes, 2),
            "tcHours": round(self.tc_hours, 4),
            "method": self.method,
            "flowPathLengthFt": round(self.flow_path_length_ft, 1),
            "elevationChangeFt": round(self.elevation_change_ft, 2),
            "avgSlopePercent": round(self.avg_slope_percent, 2),
            "segments": [
                {
                    "type": s.type,
                    "travelTimeMinutes": round(s.travel_time_minutes, 2),
                    "lengthFt": round(s.length_ft, 1),
                    "slopeFtFt": round(s.slope_ft_ft, 4),
                    **s.inputs,
                }
                for s in self.segments
            ],
        }


# ============================================================================
# TR-55 travel time equations
# ============================================================================

def sheet_flow_tt(length_ft: float, slope: float, mannings_n: float,
                  p2_inches: float) -> float:
    """
    TR-55 Equation 3-3: Sheet flow travel time.

    Tt = 0.007 * (n * L)^0.8 / (P2^0.5 * S^0.4)

    Returns travel time in hours.
    """
    slope = max(slope, MIN_SLOPE)
    length_ft = min(length_ft, SHEET_FLOW_MAX_LENGTH_FT)
    if p2_inches <= 0:
        p2_inches = DEFAULT_P2_INCHES

    numerator = SHEET_FLOW_CONSTANT * (mannings_n * length_ft) ** SHEET_FLOW_EXP_NL
    denominator = (p2_inches ** SHEET_FLOW_EXP_P2) * (slope ** SHEET_FLOW_EXP_S)

    return numerator / denominator


def shallow_concentrated_tt(length_ft: float, slope: float,
                            is_paved: bool = False) -> float:
    """
    TR-55 Figure 3-1: Shallow concentrated flow travel time.

    V = coeff * S^0.5   (ft/s)
    Tt = L / (3600 * V)  (hours)
    """
    slope = max(slope, MIN_SLOPE)
    coeff = SHALLOW_PAVED_COEFF if is_paved else SHALLOW_UNPAVED_COEFF
    velocity = coeff * math.sqrt(slope)
    if velocity <= 0:
        return 0.0
    return length_ft / (3600.0 * velocity)


def channel_flow_tt(length_ft: float, slope: float, mannings_n: float,
                    hydraulic_radius_ft: float) -> float:
    """
    Manning's equation: Channel flow travel time.

    V = (1.49 / n) * R^(2/3) * S^0.5   (ft/s)
    Tt = L / (3600 * V)                  (hours)
    """
    slope = max(slope, MIN_SLOPE)
    velocity = (MANNINGS_USCS_FACTOR / mannings_n) * \
               (hydraulic_radius_ft ** (2.0 / 3.0)) * math.sqrt(slope)
    if velocity <= 0:
        return 0.0
    return length_ft / (3600.0 * velocity)


def get_hydraulic_radius(drainage_area_acres: float) -> float:
    """Look up default hydraulic radius by drainage area."""
    for threshold, radius in HYDRAULIC_RADIUS_TABLE:
        if drainage_area_acres < threshold:
            return radius
    return HYDRAULIC_RADIUS_TABLE[-1][1]


# ============================================================================
# Flow path processing
# ============================================================================

class TcCalculator:
    """
    Computes TR-55 Time of Concentration for catchments using WhiteboxTools
    for flow path extraction and DEM-based slope/area analysis.
    """

    def __init__(self, working_dir: str, config: dict = None):
        self.working_dir = Path(working_dir)
        self.working_dir.mkdir(parents=True, exist_ok=True)

        self.wbt = whitebox.WhiteboxTools()
        self.wbt.set_working_dir(str(self.working_dir))
        self.wbt.set_verbose_mode(True)

        self.config = config or {}

        # Drawing unit state
        self.is_metric = self.config.get("is_metric", False)
        self.unit_to_feet = METERS_TO_FEET if self.is_metric else 1.0

    def run(self, dem_path: str, catchments_path: str,
            user_crs: str = None) -> list[dict]:
        """
        Calculate Tc for each catchment.

        Args:
            dem_path: Path to DEM raster (BIL or GeoTIFF from Civil 3D export)
            catchments_path: Path to catchment polygons (GeoJSON/shapefile)
                             Each feature needs an id/name attribute.
            user_crs: Optional CRS override

        Returns:
            List of TcResult dicts, one per catchment.
        """
        logger.info("=" * 60)
        logger.info("TR-55 TIME OF CONCENTRATION CALCULATOR")
        logger.info("=" * 60)

        dem_path = Path(dem_path)
        catchments_path = Path(catchments_path)

        # --- Resolve CRS ---
        json_metadata = self._load_dem_metadata(dem_path)
        crs = resolve_crs(
            dem_path=dem_path,
            json_metadata=json_metadata,
            user_crs=user_crs,
            drawing_units=self.config.get("drawing_units", "ft"),
        )
        logger.info(f"CRS: {crs}")

        # --- Convert DEM to GeoTIFF ---
        logger.info("\n[Step 1/5] Preparing DEM...")
        input_dem = self.working_dir / "tc_input_dem.tif"
        self._convert_dem(dem_path, input_dem, crs)

        with rasterio.open(str(input_dem)) as src:
            self.cell_size = max(src.res)
            self.dem_transform = src.transform
            self.dem_shape = src.shape

        cell_area_sq_units = self.cell_size ** 2
        cell_area_sq_ft = cell_area_sq_units * (self.unit_to_feet ** 2)
        self.cell_area_acres = cell_area_sq_ft / SQ_FT_PER_ACRE

        # --- Condition DEM ---
        logger.info("\n[Step 2/5] Conditioning DEM...")
        conditioned = self._condition_dem(str(input_dem))

        # --- Flow direction + accumulation ---
        logger.info("\n[Step 3/5] Computing flow direction and accumulation...")
        flow_dir = self._flow_direction(conditioned)
        flow_accum = self._flow_accumulation(conditioned)

        # --- Load catchments ---
        catchments_gdf = gpd.read_file(str(catchments_path))
        catchments_gdf = catchments_gdf.set_crs(crs, allow_override=True)
        logger.info(f"Loaded {len(catchments_gdf)} catchments")

        # --- Process each catchment ---
        logger.info("\n[Step 4/5] Extracting flow paths and computing Tc...")
        results = []

        for idx, row in catchments_gdf.iterrows():
            catchment_id = self._get_catchment_id(row, idx)
            logger.info(f"\n--- Catchment: {catchment_id} ---")

            try:
                result = self._process_catchment(
                    catchment_id=catchment_id,
                    geometry=row.geometry,
                    conditioned_dem=conditioned,
                    flow_dir=flow_dir,
                    flow_accum=flow_accum,
                    crs=crs,
                )
                results.append(result.to_dict())
            except Exception as e:
                logger.error(f"Failed for catchment {catchment_id}: {e}")
                results.append({
                    "catchmentId": catchment_id,
                    "tcMinutes": -1,
                    "error": str(e),
                })

        # --- Write output ---
        logger.info("\n[Step 5/5] Writing results...")
        output_path = self.working_dir / "tc_results.json"
        with open(output_path, 'w') as f:
            json.dump(results, f, indent=2)
        logger.info(f"Results written to: {output_path}")

        logger.info("\n" + "=" * 60)
        logger.info("TC CALCULATION COMPLETE")
        logger.info("=" * 60)

        return results

    # ------------------------------------------------------------------
    # Per-catchment processing
    # ------------------------------------------------------------------

    def _process_catchment(self, catchment_id: str, geometry,
                           conditioned_dem: str, flow_dir: str,
                           flow_accum: str, crs) -> TcResult:
        """Extract flow path and compute Tc for a single catchment."""

        # Clip DEM to catchment + small buffer
        buffer_dist = self.cell_size * 5
        catchment_dem = self._clip_raster_to_polygon(
            conditioned_dem, geometry, buffer_dist,
            str(self.working_dir / f"catch_{catchment_id}_dem.tif"), crs
        )

        # Run WhiteboxTools longest flow path on the clipped DEM
        flow_path_shp = str(self.working_dir / f"catch_{catchment_id}_flowpath.shp")
        self._extract_longest_flowpath(catchment_dem, flow_path_shp)

        # Read the flow path
        flow_path_gdf = gpd.read_file(flow_path_shp)
        if len(flow_path_gdf) == 0:
            raise ValueError("WhiteboxTools returned no flow path")

        # Get the longest path if multiple returned
        flow_path_gdf['length'] = flow_path_gdf.geometry.length
        longest = flow_path_gdf.loc[flow_path_gdf['length'].idxmax()]
        flow_line = longest.geometry

        # Get upstream/downstream elevations from attributes or DEM sampling
        up_elev, dn_elev = self._get_flowpath_elevations(longest, conditioned_dem)
        up_elev_ft = up_elev * self.unit_to_feet
        dn_elev_ft = dn_elev * self.unit_to_feet

        # Total flow path length in feet
        total_length_ft = flow_line.length * self.unit_to_feet
        elevation_change_ft = up_elev_ft - dn_elev_ft

        if total_length_ft <= 0:
            raise ValueError("Flow path has zero length")

        avg_slope_pct = (elevation_change_ft / total_length_ft) * 100 if total_length_ft > 0 else 0

        # Sample flow accumulation along the path to get contributing area
        coords = list(flow_line.coords)
        distances_ft, areas_acres = self._sample_flow_accum_along_path(
            coords, flow_accum
        )

        # Get total catchment area for hydraulic radius lookup
        catchment_area_sq_units = geometry.area
        catchment_area_acres = (catchment_area_sq_units * (self.unit_to_feet ** 2)) / SQ_FT_PER_ACRE

        # Segment the flow path
        segments = self._segment_flow_path(
            coords, distances_ft, areas_acres,
            conditioned_dem, catchment_area_acres
        )

        # Sum travel times
        total_tc_hours = sum(s.travel_time_hours for s in segments)

        result = TcResult(
            catchment_id=catchment_id,
            tc_minutes=total_tc_hours * 60.0,
            tc_hours=total_tc_hours,
            flow_path_length_ft=total_length_ft,
            elevation_change_ft=elevation_change_ft,
            avg_slope_percent=avg_slope_pct,
            segments=segments,
        )

        logger.info(f"  Tc = {result.tc_minutes:.1f} min ({result.tc_hours:.3f} hr)")
        for s in segments:
            logger.info(f"    {s.type}: {s.travel_time_minutes:.1f} min, {s.length_ft:.0f} ft, S={s.slope_ft_ft:.4f}")

        return result

    # ------------------------------------------------------------------
    # Flow path segmentation + Tc
    # ------------------------------------------------------------------

    def _segment_flow_path(self, coords: list, distances_ft: list,
                           areas_acres: list, dem_path: str,
                           catchment_area_acres: float) -> list[TcSegment]:
        """
        Walk the flow path from upstream to downstream and split into
        sheet flow, shallow concentrated flow, and channel flow segments.
        """
        sheet_limit_ft = self.config.get("sheet_flow_length_ft", SHEET_FLOW_DEFAULT_LENGTH_FT)
        sheet_area_limit = self.config.get("sheet_flow_max_area_acres", SHEET_FLOW_MAX_CONTRIBUTING_AREA_ACRES)
        channel_threshold = self.config.get("channel_threshold_acres", CHANNEL_THRESHOLD_ACRES)

        # User-configurable inputs
        mannings_n_sheet = self.config.get("mannings_n_sheet", DEFAULT_SHEET_N)
        mannings_n_channel = self.config.get("mannings_n_channel", DEFAULT_CHANNEL_N)
        p2_inches = self.config.get("p2_rainfall_inches", DEFAULT_P2_INCHES)
        hydraulic_radius = self.config.get("hydraulic_radius_ft", None)
        is_paved = self.config.get("is_paved", False)

        # Classify each vertex
        regimes = []
        for i, (dist, area) in enumerate(zip(distances_ft, areas_acres)):
            if dist <= sheet_limit_ft and area <= sheet_area_limit:
                regimes.append("sheet")
            elif area < channel_threshold:
                regimes.append("shallow")
            else:
                regimes.append("channel")

        # Ensure at least some sheet flow at the start
        if regimes and regimes[0] != "sheet":
            regimes[0] = "sheet"

        # Build contiguous segments
        segments_raw = []
        current_regime = regimes[0]
        start_idx = 0

        for i in range(1, len(regimes)):
            if regimes[i] != current_regime:
                segments_raw.append((current_regime, start_idx, i))
                current_regime = regimes[i]
                start_idx = i
        segments_raw.append((current_regime, start_idx, len(regimes) - 1))

        # Compute Tc per segment
        segments = []
        for regime, i_start, i_end in segments_raw:
            if i_start >= i_end:
                continue

            seg_length_ft = distances_ft[i_end] - distances_ft[i_start]
            if seg_length_ft <= 0:
                continue

            # Segment slope from DEM elevations
            seg_slope = self._slope_between_indices(coords, i_start, i_end, dem_path)

            if regime == "sheet":
                seg_length_ft = min(seg_length_ft, SHEET_FLOW_MAX_LENGTH_FT)
                tt = sheet_flow_tt(seg_length_ft, seg_slope, mannings_n_sheet, p2_inches)
                seg = TcSegment(
                    type="Sheet Flow",
                    length_ft=seg_length_ft,
                    slope_ft_ft=seg_slope,
                    travel_time_hours=tt,
                    inputs={
                        "manningsN": mannings_n_sheet,
                        "rainfall2yrInches": p2_inches,
                    },
                )

            elif regime == "shallow":
                tt = shallow_concentrated_tt(seg_length_ft, seg_slope, is_paved)
                seg = TcSegment(
                    type="Shallow Concentrated Flow",
                    length_ft=seg_length_ft,
                    slope_ft_ft=seg_slope,
                    travel_time_hours=tt,
                    inputs={
                        "surfaceType": "Paved" if is_paved else "Unpaved",
                    },
                )

            elif regime == "channel":
                # Determine hydraulic radius from catchment area if not specified
                r = hydraulic_radius or get_hydraulic_radius(catchment_area_acres)
                tt = channel_flow_tt(seg_length_ft, seg_slope, mannings_n_channel, r)
                seg = TcSegment(
                    type="Channel Flow",
                    length_ft=seg_length_ft,
                    slope_ft_ft=seg_slope,
                    travel_time_hours=tt,
                    inputs={
                        "manningsN": mannings_n_channel,
                        "hydraulicRadiusFt": r,
                    },
                )
            else:
                continue

            segments.append(seg)

        # Fallback: if no segments produced, use whole path as shallow concentrated
        if not segments:
            total_length = distances_ft[-1] if distances_ft else 0
            if total_length > 0:
                overall_slope = self._slope_between_indices(
                    coords, 0, len(coords) - 1, dem_path
                )
                tt = shallow_concentrated_tt(total_length, overall_slope, is_paved)
                segments.append(TcSegment(
                    type="Shallow Concentrated Flow",
                    length_ft=total_length,
                    slope_ft_ft=overall_slope,
                    travel_time_hours=tt,
                    inputs={"surfaceType": "Unpaved", "note": "fallback - no segmentation"},
                ))

        return segments

    # ------------------------------------------------------------------
    # DEM / raster helpers
    # ------------------------------------------------------------------

    def _load_dem_metadata(self, dem_path: Path) -> dict | None:
        """Load companion JSON metadata from C# exporter."""
        for suffix in ('.json',):
            json_path = dem_path.with_suffix(suffix)
            if json_path.exists():
                try:
                    return json.loads(json_path.read_text())
                except Exception as e:
                    logger.warning(f"Failed to read metadata: {e}")
        return None

    def _convert_dem(self, src_path: Path, dst_path: Path, crs):
        """Convert input DEM (BIL or TIF) to a clean GeoTIFF with CRS."""
        with rasterio.open(str(src_path)) as src:
            data = src.read(1)
            nodata = src.nodata

            profile = {
                'driver': 'GTiff',
                'dtype': 'float32',
                'width': src.width,
                'height': src.height,
                'count': 1,
                'crs': crs,
                'transform': src.transform,
                'nodata': nodata if nodata is not None else -9999.0,
                'tiled': False,
            }

            with rasterio.open(str(dst_path), 'w', **profile) as dst:
                dst.write(data.astype('float32'), 1)

        logger.info(f"DEM converted: {dst_path}")

    def _condition_dem(self, dem_path: str) -> str:
        """Breach depressions in the DEM."""
        output = str(self.working_dir / "tc_dem_conditioned.tif")
        method = self.config.get("depression_method", "breach")

        if method == "breach":
            breach_dist = self.config.get("breach_distance", 10)
            self.wbt.breach_depressions_least_cost(
                dem=dem_path, output=output, dist=breach_dist, fill=True
            )
        else:
            self.wbt.fill_depressions(dem=dem_path, output=output, fix_flats=True)

        return output

    def _flow_direction(self, dem_path: str) -> str:
        """D8 flow direction."""
        output = str(self.working_dir / "tc_flow_dir.tif")
        self.wbt.d8_pointer(dem=dem_path, output=output, esri_pntr=False)
        return output

    def _flow_accumulation(self, dem_path: str) -> str:
        """D8 flow accumulation (cell count)."""
        output = str(self.working_dir / "tc_flow_accum.tif")
        self.wbt.d8_flow_accumulation(
            i=dem_path, output=output, out_type="cells", log=False, clip=False
        )
        return output

    def _extract_longest_flowpath(self, dem_path: str, output_path: str):
        """
        Run WhiteboxTools LongestFlowpath on a clipped catchment DEM.

        This requires a conditioned DEM clipped to the catchment boundary.
        WhiteboxTools will find the longest flow path within the raster extent.
        """
        # LongestFlowpath needs the DEM directly
        self.wbt.longest_flowpath(
            dem=dem_path,
            basins="",  # empty = use entire DEM extent
            output=output_path,
        )

    def _clip_raster_to_polygon(self, raster_path: str, polygon,
                                buffer_dist: float, output_path: str,
                                crs) -> str:
        """Clip a raster to a polygon boundary with optional buffer."""
        from rasterio.mask import mask as rasterio_mask

        buffered = polygon.buffer(buffer_dist)

        with rasterio.open(raster_path) as src:
            out_image, out_transform = rasterio_mask(
                src, [buffered], crop=True, nodata=-9999.0
            )
            out_profile = src.profile.copy()
            out_profile.update({
                'driver': 'GTiff',
                'height': out_image.shape[1],
                'width': out_image.shape[2],
                'transform': out_transform,
                'crs': crs,
                'nodata': -9999.0,
            })

        with rasterio.open(output_path, 'w', **out_profile) as dst:
            dst.write(out_image)

        return output_path

    def _get_flowpath_elevations(self, flow_path_row, dem_path: str):
        """
        Get upstream and downstream elevations from flow path attributes
        or by sampling the DEM at path endpoints.
        """
        # Try WhiteboxTools attributes first
        up_elev = flow_path_row.get('UP_ELEV')
        dn_elev = flow_path_row.get('DN_ELEV')

        if up_elev is not None and dn_elev is not None:
            try:
                return float(up_elev), float(dn_elev)
            except (ValueError, TypeError):
                pass

        # Fall back to DEM sampling at endpoints
        line = flow_path_row.geometry
        coords = list(line.coords)
        start_pt = coords[0]
        end_pt = coords[-1]

        with rasterio.open(dem_path) as src:
            start_vals = list(src.sample([(start_pt[0], start_pt[1])]))
            end_vals = list(src.sample([(end_pt[0], end_pt[1])]))
            e1 = float(start_vals[0][0])
            e2 = float(end_vals[0][0])

        # Upstream = higher elevation
        return max(e1, e2), min(e1, e2)

    def _sample_flow_accum_along_path(self, coords: list,
                                      flow_accum_path: str):
        """
        Walk along flow path vertices and sample flow accumulation raster.

        Returns:
            distances_ft: Cumulative distance from start (in feet) per vertex
            areas_acres: Contributing area (in acres) per vertex
        """
        distances_ft = [0.0]
        cumulative = 0.0

        for i in range(1, len(coords)):
            dx = coords[i][0] - coords[i - 1][0]
            dy = coords[i][1] - coords[i - 1][1]
            dist = math.sqrt(dx * dx + dy * dy) * self.unit_to_feet
            cumulative += dist
            distances_ft.append(cumulative)

        # Sample flow accumulation at each vertex
        areas_acres = []
        with rasterio.open(flow_accum_path) as src:
            xy_pairs = [(c[0], c[1]) for c in coords]
            samples = list(src.sample(xy_pairs))

            for val in samples:
                cell_count = float(val[0])
                if cell_count < 0 or np.isnan(cell_count):
                    cell_count = 0
                area = cell_count * self.cell_area_acres
                areas_acres.append(area)

        return distances_ft, areas_acres

    def _slope_between_indices(self, coords: list, i_start: int, i_end: int,
                               dem_path: str) -> float:
        """
        Compute slope (ft/ft) between two vertices by sampling the DEM.
        """
        pt_start = (coords[i_start][0], coords[i_start][1])
        pt_end = (coords[i_end][0], coords[i_end][1])

        with rasterio.open(dem_path) as src:
            e_start = float(list(src.sample([pt_start]))[0][0])
            e_end = float(list(src.sample([pt_end]))[0][0])

        elev_start_ft = e_start * self.unit_to_feet
        elev_end_ft = e_end * self.unit_to_feet

        # Horizontal distance in feet
        dx = coords[i_end][0] - coords[i_start][0]
        dy = coords[i_end][1] - coords[i_start][1]
        h_dist_ft = math.sqrt(dx * dx + dy * dy) * self.unit_to_feet

        if h_dist_ft <= 0:
            return MIN_SLOPE

        slope = abs(elev_start_ft - elev_end_ft) / h_dist_ft
        return max(slope, MIN_SLOPE)

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    def _get_catchment_id(self, row, index: int) -> str:
        """Extract a catchment identifier from a GeoDataFrame row."""
        for col in ('catchment_id', 'struct_name', 'structure_id', 'name',
                     'Name', 'id', 'ID', 'FID'):
            val = row.get(col)
            if val is not None and str(val).strip():
                return str(val)
        return f"catchment_{index}"
