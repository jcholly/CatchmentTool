"""
CRS Utilities for Civil 3D Catchment Delineation Tool

Handles tiered CRS resolution from Civil 3D exports, including:
- User-specified CRS override
- Standard WKT from .prj files
- Autodesk CS-MAP code to EPSG translation
- Local engineering CRS fallback (correct linear units, no geographic claim)
"""

import re
import json
import logging
from pathlib import Path

from rasterio.crs import CRS

logger = logging.getLogger(__name__)

# ============================================================================
# Autodesk CS-MAP code -> EPSG mapping
# ============================================================================
# Civil 3D uses proprietary CS-MAP codes, not EPSG. This dictionary maps
# the most common US state plane and UTM codes to their EPSG equivalents.
# Format: {state abbrev}{datum year}-{zone}{unit suffix}
#   Suffix: F = US Survey Feet, M = Meters, IF = International Feet
# Extend this as needed for your projects.

AUTODESK_TO_EPSG = {
    # -- Alabama --
    "AL83-EF": 2759, "AL83-WF": 2760,
    # -- Arizona --
    "AZ83-EF": 2222, "AZ83-CF": 2223, "AZ83-WF": 2224,
    "AZ83-E": 26948, "AZ83-C": 26949, "AZ83-W": 26950,
    # -- California --
    "CA83-IF": 2225, "CA83-IIF": 2226, "CA83-IIIF": 2227,
    "CA83-IVF": 2228, "CA83-VF": 2229, "CA83-VIF": 2230,
    "CA83-I": 26941, "CA83-II": 26942, "CA83-III": 26943,
    "CA83-IV": 26944, "CA83-V": 26945, "CA83-VI": 26946,
    # -- Colorado --
    "CO83-NF": 2231, "CO83-CF": 2232, "CO83-SF": 2233,
    "CO83-N": 26953, "CO83-C": 26954, "CO83-S": 26955,
    # -- Connecticut --
    "CT83-F": 2234, "CT83": 26956,
    # -- Florida --
    "FL83-EF": 2236, "FL83-WF": 2237, "FL83-NF": 2238,
    "FL83-E": 26958, "FL83-W": 26959, "FL83-N": 26960,
    # -- Georgia --
    "GA83-EF": 2239, "GA83-WF": 2240,
    "GA83-E": 26966, "GA83-W": 26967,
    # -- Illinois --
    "IL83-EF": 3443, "IL83-WF": 3444,
    "IL83-E": 26971, "IL83-W": 26972,
    # -- Indiana --
    "IN83-EF": 2965, "IN83-WF": 2966,
    # -- Iowa --
    "IA83-NF": 3417, "IA83-SF": 3418,
    # -- Kansas --
    "KS83-NF": 3419, "KS83-SF": 3420,
    # -- Kentucky --
    "KY83-NF": 2246, "KY83-SF": 2247,
    "KY83-1ZF": 3089,
    # -- Louisiana --
    "LA83-NF": 3451, "LA83-SF": 3452,
    # -- Maryland --
    "MD83-F": 2248, "MD83": 26985,
    # -- Massachusetts --
    "MA83-MF": 2249, "MA83-IF": 2250,
    # -- Michigan --
    "MI83-NF": 2251, "MI83-CF": 2252, "MI83-SF": 2253,
    # -- Minnesota --
    "MN83-NF": 2254, "MN83-CF": 2255, "MN83-SF": 2256,
    # -- Mississippi --
    "MS83-EF": 2257, "MS83-WF": 2258,
    # -- Missouri --
    "MO83-EF": 2259, "MO83-CF": 2260, "MO83-WF": 2261,
    # -- Montana --
    "MT83-F": 2262, "MT83": 32100,
    # -- Nebraska --
    "NE83-F": 2263, "NE83": 32104,
    # -- Nevada --
    "NV83-EF": 3421, "NV83-CF": 3422, "NV83-WF": 3423,
    # -- New Hampshire --
    "NH83-F": 3437,
    # -- New Jersey --
    "NJ83-F": 3424, "NJ83": 32111,
    # -- New Mexico --
    "NM83-EF": 2257, "NM83-CF": 2258, "NM83-WF": 2259,
    # -- New York --
    "NY83-EF": 2260, "NY83-CF": 2261, "NY83-WF": 2262, "NY83-LIF": 2263,
    # -- North Carolina --
    "NC83-F": 2264, "NC83": 32119,
    # -- North Dakota --
    "ND83-NF": 2265, "ND83-SF": 2266,
    # -- Ohio --
    "OH83-NF": 3734, "OH83-SF": 3735,
    "OH83-N": 32122, "OH83-S": 32123,
    # -- Oklahoma --
    "OK83-NF": 2267, "OK83-SF": 2268,
    # -- Oregon --
    "OR83-NF": 2269, "OR83-SF": 2270,
    "OR83-N": 32126, "OR83-S": 32127,
    # -- Pennsylvania --
    "PA83-NF": 2271, "PA83-SF": 2272,
    "PA83-N": 32128, "PA83-S": 32129,
    # -- South Carolina --
    "SC83-F": 2273, "SC83": 32133,
    # -- Tennessee --
    "TN83-F": 2274, "TN83": 32136,
    # -- Texas --
    "TX83-NF": 2275, "TX83-NCF": 2276, "TX83-CF": 2277,
    "TX83-SCF": 2278, "TX83-SF": 2279,
    "TX83-N": 32137, "TX83-NC": 32138, "TX83-C": 32139,
    "TX83-SC": 32140, "TX83-S": 32141,
    # -- Utah --
    "UT83-NF": 2280, "UT83-CF": 2281, "UT83-SF": 2282,
    # -- Vermont --
    "VT83-F": 5646,
    # -- Virginia --
    "VA83-NF": 2283, "VA83-SF": 2284,
    "VA83-N": 32146, "VA83-S": 32147,
    # -- Washington --
    "WA83-NF": 2285, "WA83-SF": 2286,
    "WA83-N": 32148, "WA83-S": 32149,
    # -- West Virginia --
    "WV83-NF": 2287, "WV83-SF": 2288,
    # -- Wisconsin --
    "WI83-NF": 2289, "WI83-CF": 2290, "WI83-SF": 2291,
    # -- Wyoming --
    "WY83-EF": 2292, "WY83-ECF": 2293, "WY83-WCF": 2294, "WY83-WF": 2295,

    # -- UTM NAD83 zones --
    "UTM83-10": 26910, "UTM83-11": 26911, "UTM83-12": 26912,
    "UTM83-13": 26913, "UTM83-14": 26914, "UTM83-15": 26915,
    "UTM83-16": 26916, "UTM83-17": 26917, "UTM83-18": 26918,
    "UTM83-19": 26919,
    # -- UTM WGS84 zones --
    "UTM84-10N": 32610, "UTM84-11N": 32611, "UTM84-12N": 32612,
    "UTM84-13N": 32613, "UTM84-14N": 32614, "UTM84-15N": 32615,
    "UTM84-16N": 32616, "UTM84-17N": 32617, "UTM84-18N": 32618,
    "UTM84-19N": 32619, "UTM84-20N": 32620,
}


def resolve_crs_from_autodesk_code(cs_code: str) -> CRS | None:
    """
    Resolve an Autodesk CS-MAP code to a pyproj CRS.

    Tries direct EPSG lookup first, then falls back to pyproj.from_user_input().
    """
    if not cs_code or cs_code.strip() == "":
        return None

    cs_code = cs_code.strip()

    # Direct lookup in our mapping table
    epsg = AUTODESK_TO_EPSG.get(cs_code)
    if epsg:
        logger.info(f"Resolved Autodesk code '{cs_code}' -> EPSG:{epsg}")
        return CRS.from_epsg(epsg)

    # Try case-insensitive match
    for key, epsg in AUTODESK_TO_EPSG.items():
        if key.upper() == cs_code.upper():
            logger.info(f"Resolved Autodesk code '{cs_code}' -> EPSG:{epsg} (case-insensitive)")
            return CRS.from_epsg(epsg)

    # Last resort: try pyproj's general resolver
    try:
        crs = CRS.from_user_input(cs_code)
        logger.info(f"pyproj resolved '{cs_code}' -> {crs}")
        return crs
    except Exception:
        logger.warning(f"Could not resolve Autodesk coordinate system code: '{cs_code}'")
        return None


def make_local_engineering_crs(unit: str = "us-ft") -> CRS:
    """
    Create a local engineering CRS with correct linear units.

    This gives WhiteboxTools the geokeys it needs without making false
    geographic claims. Coordinates are treated as a local Transverse Mercator
    projection centered at the origin.

    Args:
        unit: Linear unit — 'us-ft', 'ft', or 'm'
    """
    unit_map = {
        "us-ft": "us-ft",
        "ft": "ft",
        "feet": "us-ft",
        "foot": "us-ft",
        "m": "m",
        "meters": "m",
        "metre": "m",
    }
    proj_unit = unit_map.get(unit.lower(), "us-ft")

    proj4 = (
        f"+proj=tmerc +lat_0=0 +lon_0=0 +k=1 "
        f"+x_0=0 +y_0=0 +datum=WGS84 +units={proj_unit} +no_defs +type=crs"
    )
    crs = CRS.from_proj4(proj4)
    logger.info(f"Created local engineering CRS (units={proj_unit})")
    return crs


def resolve_crs(
    dem_path: str | Path,
    json_metadata: dict | None = None,
    user_crs: str | None = None,
    drawing_units: str | None = None,
) -> CRS:
    """
    Tiered CRS resolution for Civil 3D exports.

    Priority order:
        1. User-specified CRS (--crs flag)
        2. .prj file with valid WKT
        3. Autodesk CS-MAP code from JSON metadata -> EPSG lookup
        4. Local engineering CRS with correct linear units
        5. Raise error if nothing works

    Args:
        dem_path: Path to the DEM file (checks for .prj sidecar)
        json_metadata: Parsed JSON metadata dict from the C# exporter
        user_crs: User-specified CRS string (e.g., 'EPSG:2277')
        drawing_units: Drawing unit string ('ft', 'm', etc.)

    Returns:
        A valid pyproj CRS object.

    Raises:
        ValueError: If CRS cannot be resolved at all.
    """
    dem_path = Path(dem_path)

    # --- Tier 1: User override ---
    if user_crs:
        try:
            crs = CRS.from_user_input(user_crs)
            logger.info(f"CRS from user override: {crs}")
            return crs
        except Exception as e:
            logger.warning(f"User CRS '{user_crs}' is invalid: {e}")

    # --- Tier 2: .prj sidecar file with valid WKT ---
    for suffix in ('.prj',):
        prj_path = dem_path.with_suffix(suffix)
        if not prj_path.exists():
            # Also check without the .tif/.bil extension stem
            prj_path = Path(str(dem_path).replace('.tif', '').replace('.bil', '')).with_suffix(suffix)
        if prj_path.exists():
            try:
                wkt = prj_path.read_text().strip()
                if wkt and not wkt.startswith('LOCAL_CS'):
                    crs = CRS.from_wkt(wkt)
                    logger.info(f"CRS from .prj file: {crs}")
                    return crs
                elif wkt.startswith('LOCAL_CS'):
                    # Extract the Autodesk code from LOCAL_CS["code"]
                    match = re.search(r'LOCAL_CS\["(.+?)"\]', wkt)
                    if match:
                        cs_code = match.group(1)
                        crs = resolve_crs_from_autodesk_code(cs_code)
                        if crs:
                            return crs
            except Exception as e:
                logger.warning(f"Failed to parse .prj file '{prj_path}': {e}")

    # --- Tier 3: Autodesk CS-MAP code from JSON metadata ---
    if json_metadata:
        # Check for the dedicated autodesk_cs_code field (new)
        cs_code = json_metadata.get('autodesk_cs_code', '')
        if cs_code:
            crs = resolve_crs_from_autodesk_code(cs_code)
            if crs:
                return crs

        # Fall back to coordinate_system field (legacy)
        cs_raw = json_metadata.get('coordinate_system', '')
        if cs_raw:
            # It might be WKT
            try:
                crs = CRS.from_wkt(cs_raw)
                logger.info(f"CRS from metadata WKT: {crs}")
                return crs
            except Exception:
                pass
            # Or it might be an Autodesk code wrapped in LOCAL_CS
            match = re.search(r'LOCAL_CS\["(.+?)"\]', cs_raw)
            if match:
                crs = resolve_crs_from_autodesk_code(match.group(1))
                if crs:
                    return crs
            # Or just a raw code
            crs = resolve_crs_from_autodesk_code(cs_raw)
            if crs:
                return crs

    # --- Tier 4: Local engineering CRS with correct units ---
    unit = drawing_units or "ft"
    logger.warning(
        "No CRS found from .prj, metadata, or user override. "
        f"Using local engineering CRS (units={unit}). "
        "Assign a coordinate system in Civil 3D Drawing Settings to avoid this."
    )
    return make_local_engineering_crs(unit)


def get_linear_unit(crs: CRS) -> str:
    """Extract the linear unit name from a CRS (e.g., 'metre', 'US survey foot')."""
    try:
        return crs.axis_info[0].unit_name
    except Exception:
        return "unknown"


def is_metric_unit(unit_name: str) -> bool:
    """Check if a unit name represents metric units."""
    metric_names = {"metre", "meter", "m"}
    return unit_name.lower() in metric_names
