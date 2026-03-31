"""
Generate a realistic LandXML test site for the CatchmentTool.

"Oakwood Meadows" — 8-acre commercial site with storm drainage to detention pond.
All pipes 12in concrete, all structures 48in cylindrical concrete.
"""

import math
import json
import numpy as np
from pathlib import Path
from xml.etree.ElementTree import Element, SubElement, ElementTree, indent
from scipy.spatial import Delaunay
from scipy.ndimage import gaussian_filter


SITE_W, SITE_H = 700.0, 500.0

# Building pad
BLDG = {"x1": 120, "y1": 340, "x2": 320, "y2": 440, "elev": 105.0}

# Detention pond (SE)
POND = {"cx": 560, "cy": 65, "rx": 100, "ry": 45, "bottom": 93.0, "top": 97.0}

# Road (E-W band)
ROAD_Y1, ROAD_Y2 = 130, 160

# Parking area
PARK = {"x1": 80, "y1": 185, "x2": 640, "y2": 300}

# Sidewalk width
SIDEWALK_W = 5.0

# Structures — all 48in cylindrical concrete
STRUCT_DIA = 48  # inches

STRUCTURES = {
    # Row 1 — north edge of parking (drive aisle inlets)
    "STR-1": {"x": 140, "y": 295, "rim": 103.40},
    "STR-2": {"x": 290, "y": 295, "rim": 102.90},
    "STR-3": {"x": 440, "y": 295, "rim": 102.30},
    "STR-4": {"x": 590, "y": 295, "rim": 101.60},
    # Row 2 — south edge of parking
    "STR-5": {"x": 140, "y": 190, "rim": 101.90},
    "STR-6": {"x": 290, "y": 190, "rim": 101.40},
    "STR-7": {"x": 440, "y": 190, "rim": 100.80},
    "STR-8": {"x": 590, "y": 190, "rim": 100.20},
    # Trunk junction (south of road, centered)
    "STR-9":  {"x": 400, "y": 110, "rim": 99.50},
    # Outfall at pond
    "STR-10": {"x": 540, "y": 68,  "rim": 97.30},
}

# Set sump elevations (rim - 6ft typical depth)
for s in STRUCTURES.values():
    s["sump"] = s["rim"] - 6.0

# ALL pipes 12in concrete — guaranteed to match default Storm Sewer parts list
PIPES = [
    ("P-01", "STR-1", "STR-2"),
    ("P-02", "STR-2", "STR-3"),
    ("P-03", "STR-3", "STR-4"),
    ("P-04", "STR-4", "STR-8"),   # Row 1 → Row 2 junction
    ("P-05", "STR-5", "STR-6"),
    ("P-06", "STR-6", "STR-7"),
    ("P-07", "STR-7", "STR-8"),
    ("P-08", "STR-8", "STR-9"),   # trunk south
    ("P-09", "STR-9", "STR-10"),  # to outfall
]
PIPE_DIA = 12  # inches


def compute_pipe_inverts():
    inverts = {}
    for pname, fr, to in PIPES:
        fr_s, to_s = STRUCTURES[fr], STRUCTURES[to]
        length = math.hypot(to_s["x"] - fr_s["x"], to_s["y"] - fr_s["y"])
        us = fr_s["sump"] + 1.0
        # Drop 0.1ft from any incoming pipe at this structure
        for pp, pfr, pto in PIPES:
            if pto == fr and (fr, pp) in inverts:
                us = min(us, inverts[(fr, pp)] - 0.1)
        ds = us - length * 0.005  # 0.5% minimum slope for 12"
        ds = max(ds, to_s["sump"] + 0.5)
        inverts[(fr, pname)] = round(us, 2)
        inverts[(to, pname)] = round(ds, 2)
    return inverts


# ============================================================================
# Elevation model — raster-based with Gaussian smoothing for clean contours
# ============================================================================

def _build_elevation_grid(cell=1.0):
    """Build a graded surface with each inlet as a local low point.

    Simple approach:
    - Regional grade: NW high → SE low
    - Each inlet gets a conical depression so it's the local low point
    - Building pad is a raised plateau
    - Detention pond is a bowl
    - No road features — just clean drainage to inlets
    """
    cols = int(SITE_W / cell) + 1
    rows = int(SITE_H / cell) + 1

    xs = np.arange(cols) * cell
    ys = SITE_H - np.arange(rows) * cell
    XX, YY = np.meshgrid(xs, ys)

    # 1) Regional grade: NW=106 → SE=97
    grid = 106.0 - (XX / SITE_W) * 3.0 - ((SITE_H - YY) / SITE_H) * 6.5

    # 2) Building pad — raised plateau
    bldg_cx = (BLDG["x1"] + BLDG["x2"]) / 2
    bldg_cy = (BLDG["y1"] + BLDG["y2"]) / 2
    bldg_hw = (BLDG["x2"] - BLDG["x1"]) / 2 + 20
    bldg_hh = (BLDG["y2"] - BLDG["y1"]) / 2 + 20
    bldg_d = np.maximum(np.abs(XX - bldg_cx) / bldg_hw, np.abs(YY - bldg_cy) / bldg_hh)
    bldg_bump = 2.5 * np.clip(1.0 - bldg_d, 0, 1)
    grid += bldg_bump

    # 3) Each inlet is a local low point — conical depression
    # Radius controls catchment size; depth ensures it's the low point
    for sname, s in STRUCTURES.items():
        sx, sy = s["x"], s["y"]
        dist = np.sqrt((XX - sx)**2 + (YY - sy)**2)
        # Depression radius ~80ft, depth 1.5ft at center
        radius = 80.0
        depth = 1.5
        depression = depth * np.clip(1.0 - dist / radius, 0, 1)
        grid -= depression

    # 4) Detention pond — smooth bowl
    dx_p = (XX - POND["cx"]) / POND["rx"]
    dy_p = (YY - POND["cy"]) / POND["ry"]
    r_pond = np.sqrt(dx_p**2 + dy_p**2)
    in_pond = r_pond < 1.0
    pond_elev = np.where(
        r_pond < 0.3, POND["bottom"],
        POND["bottom"] + (POND["top"] - POND["bottom"]) * np.clip((r_pond - 0.3) / 0.7, 0, 1) ** 1.5
    )
    grid[in_pond] = pond_elev[in_pond]
    edge = (r_pond >= 1.0) & (r_pond < 2.0)
    t = np.clip((r_pond - 1.0) / 1.0, 0, 1)
    t = t * t * (3 - 2 * t)
    grid[edge] = POND["top"] * (1 - t[edge]) + grid[edge] * t[edge]

    # 5) Site perimeter slopes inward (no flow leaves the site)
    border = 20
    n_mask = YY > (SITE_H - border)
    grid[n_mask] += 1.5 * ((YY[n_mask] - (SITE_H - border)) / border)
    s_mask = YY < border
    grid[s_mask] += 1.5 * ((border - YY[s_mask]) / border)
    w_mask = XX < border
    grid[w_mask] += 1.5 * ((border - XX[w_mask]) / border)
    e_mask = XX > (SITE_W - border)
    grid[e_mask] += 1.5 * ((XX[e_mask] - (SITE_W - border)) / border)

    # 6) Light smooth — just enough to remove pixel noise
    pond_save = grid[in_pond & (r_pond < 0.3)].copy()
    grid = gaussian_filter(grid, sigma=2.0)
    grid[in_pond & (r_pond < 0.3)] = pond_save

    # 7) Stamp inlet locations as definite low points after smoothing
    # This ensures WhiteboxTools snap hits the actual inlet cell
    for sname, s in STRUCTURES.items():
        sx, sy = int(s["x"]), int(s["y"])
        r, c = int(SITE_H - sy), sx
        if 0 <= r < rows and 0 <= c < cols:
            # Set inlet cell 0.3ft below its 5-cell neighborhood minimum
            r1, r2 = max(0, r - 5), min(rows, r + 6)
            c1, c2 = max(0, c - 5), min(cols, c + 6)
            local_min = grid[r1:r2, c1:c2].min()
            grid[r, c] = local_min - 0.3

    return grid, xs, ys, cols, rows


# Global grid (computed once)
_GRID = None
_XS = None
_YS = None
_CELL = 1.0


def _ensure_grid():
    global _GRID, _XS, _YS, _CELL
    if _GRID is None:
        _GRID, _XS, _YS, _, _ = _build_elevation_grid(_CELL)


def get_elevation(x, y):
    """Interpolate elevation from the smoothed grid."""
    _ensure_grid()
    c = x / _CELL
    r = (SITE_H - y) / _CELL
    r = max(0, min(r, _GRID.shape[0] - 1.001))
    c = max(0, min(c, _GRID.shape[1] - 1.001))
    ri, ci = int(r), int(c)
    fr, fc = r - ri, c - ci
    ri2 = min(ri + 1, _GRID.shape[0] - 1)
    ci2 = min(ci + 1, _GRID.shape[1] - 1)
    v = (_GRID[ri, ci] * (1-fr) * (1-fc) + _GRID[ri2, ci] * fr * (1-fc) +
         _GRID[ri, ci2] * (1-fr) * fc + _GRID[ri2, ci2] * fr * fc)
    return round(float(v), 3)


# ============================================================================
# TIN surface
# ============================================================================

def generate_surface_points():
    _ensure_grid()
    points = []
    # 5ft base grid
    for x in np.arange(0, SITE_W + 1, 5):
        for y in np.arange(0, SITE_H + 1, 5):
            points.append((float(x), float(y), get_elevation(x, y)))
    # Dense around pond (2ft)
    p = POND
    for x in np.arange(p["cx"]-p["rx"]*1.5, p["cx"]+p["rx"]*1.5+1, 2):
        for y in np.arange(p["cy"]-p["ry"]*1.5, p["cy"]+p["ry"]*1.5+1, 2):
            if 0 <= x <= SITE_W and 0 <= y <= SITE_H:
                points.append((float(x), float(y), get_elevation(x, y)))
    # Dense around building (2ft)
    b = BLDG
    for x in np.arange(b["x1"]-25, b["x2"]+26, 2):
        for y in np.arange(b["y1"]-25, b["y2"]+26, 2):
            if 0 <= x <= SITE_W and 0 <= y <= SITE_H:
                points.append((float(x), float(y), get_elevation(x, y)))
    # Dense along road (2ft)
    for y in np.arange(ROAD_Y1-5, ROAD_Y2+6, 2):
        for x in np.arange(0, SITE_W+1, 3):
            if 0 <= y <= SITE_H:
                points.append((float(x), float(y), get_elevation(x, y)))
    # Dense along inlet rows (2ft)
    for iy in [190, 295]:
        for y in np.arange(iy-10, iy+11, 2):
            for x in np.arange(80, 641, 3):
                points.append((float(x), float(y), get_elevation(x, y)))
    # Structure locations
    for s in STRUCTURES.values():
        points.append((float(s["x"]), float(s["y"]), s["rim"]))
    # Deduplicate
    seen = set()
    unique = []
    for x, y, z in points:
        key = (round(x, 1), round(y, 1))
        if key not in seen:
            seen.add(key)
            unique.append((x, y, z))
    return unique


def triangulate(points):
    xy = np.array([(p[0], p[1]) for p in points])
    tri = Delaunay(xy)
    return [(int(f[0])+1, int(f[1])+1, int(f[2])+1) for f in tri.simplices]


# ============================================================================
# LandXML
# ============================================================================

def build_landxml(points, faces):
    ns = "http://www.landxml.org/schema/LandXML-1.2"
    root = Element("LandXML", xmlns=ns, date="2026-03-26", time="12:00:00",
                   version="1.2", language="English", readOnly="false")
    root.set("xmlns:xsi", "http://www.w3.org/2001/XMLSchema-instance")
    root.set("xsi:schemaLocation",
             f"{ns} http://www.landxml.org/schema/LandXML-1.2/LandXML-1.2.xsd")
    units = SubElement(root, "Units")
    SubElement(units, "Imperial", areaUnit="squareFoot", linearUnit="USSurveyFoot",
               volumeUnit="cubicFeet", temperatureUnit="fahrenheit", pressureUnit="inHG",
               diameterUnit="inch", velocityUnit="feetPerSecond", flowUnit="cubicFeetSecond",
               angularUnit="decimal degrees", directionUnit="decimal degrees")
    SubElement(root, "Project", name="Oakwood Meadows",
               desc="Proposed commercial site with storm drainage to detention pond")

    _build_surface(root, points, faces)
    _build_pipes(root)
    # Linework goes in the DXF (with proper layers), not in LandXML
    return root


def _build_surface(root, points, faces):
    surfs = SubElement(root, "Surfaces")
    elevs = [p[2] for p in points]
    surf = SubElement(surfs, "Surface", name="Proposed Grade",
                      desc="Proposed grading — building, parking, road, pond")
    defn = SubElement(surf, "Definition", surfType="TIN",
                      area2DSurf=f"{SITE_W*SITE_H:.0f}",
                      elevMax=f"{max(elevs):.3f}", elevMin=f"{min(elevs):.3f}")
    pnts = SubElement(defn, "Pnts")
    for i, (x, y, z) in enumerate(points, 1):
        SubElement(pnts, "P", id=str(i)).text = f"{y:.3f} {x:.3f} {z:.3f}"
    fcs = SubElement(defn, "Faces")
    for f in faces:
        SubElement(fcs, "F").text = f"{f[0]} {f[1]} {f[2]}"


def _build_pipes(root):
    inverts = compute_pipe_inverts()
    nets = SubElement(root, "PipeNetworks")
    net = SubElement(nets, "PipeNetwork", name="Storm Network", pipeNetType="storm",
                     surfaceRef="Proposed Grade")
    structs_el = SubElement(net, "Structs")
    pipes_el = SubElement(net, "Pipes")

    for sname, s in STRUCTURES.items():
        st = SubElement(structs_el, "Struct", name=sname,
                       elevRim=f"{s['rim']:.2f}", elevSump=f"{s['sump']:.2f}")
        SubElement(st, "Center").text = f"{s['y']:.3f} {s['x']:.3f}"
        SubElement(st, "CircStruct", diameter=f"{STRUCT_DIA}")
        for pname, fr, to in PIPES:
            if fr == sname:
                SubElement(st, "Invert", refPipe=pname,
                          elev=f"{inverts[(sname, pname)]:.2f}", flowDir="out")
            elif to == sname:
                SubElement(st, "Invert", refPipe=pname,
                          elev=f"{inverts[(sname, pname)]:.2f}", flowDir="in")

    for pname, fr, to in PIPES:
        length = math.hypot(STRUCTURES[to]["x"]-STRUCTURES[fr]["x"],
                           STRUCTURES[to]["y"]-STRUCTURES[fr]["y"])
        us, ds = inverts[(fr, pname)], inverts[(to, pname)]
        slope = (us - ds) / length if length > 0 else 0
        p = SubElement(pipes_el, "Pipe", name=pname, refStart=fr, refEnd=to,
                      length=f"{length:.2f}", slope=f"{slope:.6f}")
        SubElement(p, "CircPipe", diameter=f"{PIPE_DIA}")


def _build_linework(root):
    """Realistic site linework on named layers."""
    b = BLDG
    pk = PARK

    # --- Property boundary ---
    _pf(root, "C-PROP-BNDY", [
        ("Property Line", _rect(0, 0, SITE_W, SITE_H), None, "existing"),
    ])

    # --- Building footprint + entry sidewalks ---
    _pf(root, "C-BLDG-FTPT", [
        ("Building", _rect(b["x1"], b["y1"], b["x2"], b["y2"]), b["elev"], "proposed"),
    ])

    # --- Sidewalks (5ft wide along building south face and to road) ---
    sw_features = [
        # South of building
        ("Sidewalk - Bldg South",
         _rect(b["x1"], b["y1"]-SIDEWALK_W, b["x2"], b["y1"]), None, "proposed"),
        # East of building to parking
        ("Sidewalk - Bldg East",
         _rect(b["x2"], b["y1"]-SIDEWALK_W, b["x2"]+SIDEWALK_W, b["y1"]+50), None, "proposed"),
        # Entry walk from road to building
        ("Sidewalk - Entry",
         [(200, ROAD_Y2), (200, b["y1"]-SIDEWALK_W),
          (200+SIDEWALK_W, b["y1"]-SIDEWALK_W), (200+SIDEWALK_W, ROAD_Y2), (200, ROAD_Y2)],
         None, "proposed"),
    ]
    _pf(root, "C-SDWK", sw_features)

    # --- Parking lot outline + drive aisles + islands ---
    park_features = [
        ("Parking Perimeter", _rect(pk["x1"], pk["y1"], pk["x2"], pk["y2"]), None, "proposed"),
        # Drive aisles (horizontal)
        ("Drive Aisle North", [(pk["x1"], 290), (pk["x2"], 290),
                               (pk["x2"], 300), (pk["x1"], 300), (pk["x1"], 290)], None, "proposed"),
        ("Drive Aisle South", [(pk["x1"], 185), (pk["x2"], 185),
                               (pk["x2"], 195), (pk["x1"], 195), (pk["x1"], 185)], None, "proposed"),
        # Parking islands
        ("Island 1", _rect(210, 210, 222, 280), None, "proposed"),
        ("Island 2", _rect(360, 210, 372, 280), None, "proposed"),
        ("Island 3", _rect(510, 210, 522, 280), None, "proposed"),
    ]
    _pf(root, "C-PKNG", park_features)

    # --- Curb & gutter (along road and parking edges) ---
    curb_features = []
    # Road curbs
    for label, y in [("Road Curb North", ROAD_Y2), ("Road Curb South", ROAD_Y1)]:
        curb_features.append((label, [(0, y), (SITE_W, y)], None, "proposed"))
    # Parking curb perimeter
    curb_features.append(("Parking Curb", _rect(pk["x1"]-0.5, pk["y1"]-0.5,
                                                 pk["x2"]+0.5, pk["y2"]+0.5), None, "proposed"))
    _pf(root, "C-CURB", curb_features)

    # --- Road ---
    road_features = [
        ("Road CL", [(0, (ROAD_Y1+ROAD_Y2)/2), (SITE_W, (ROAD_Y1+ROAD_Y2)/2)], None, "proposed"),
        ("Road North EOP", [(0, ROAD_Y2), (SITE_W, ROAD_Y2)], None, "proposed"),
        ("Road South EOP", [(0, ROAD_Y1), (SITE_W, ROAD_Y1)], None, "proposed"),
    ]
    _pf(root, "C-ROAD", road_features)

    # --- Pond ---
    pond_features = []
    for label, scale, elev in [("Pond Top of Bank", 1.0, POND["top"]),
                                ("Pond Safety Bench", 0.65, POND["top"]-1.5),
                                ("Pond Bottom", 0.35, POND["bottom"])]:
        pts = _ellipse(POND["cx"], POND["cy"], POND["rx"]*scale, POND["ry"]*scale)
        pond_features.append((label, pts, elev, "proposed"))
    _pf(root, "C-STRM-POND", pond_features)

    # --- Grading features ---
    grade_features = [
        ("Limits of Disturbance", _rect(25, 15, 675, 485), None, "proposed"),
        # Retaining wall along NW corner of parking
        ("Retaining Wall", [(pk["x1"], pk["y2"]), (pk["x1"], pk["y2"]+20),
                            (pk["x1"]+60, pk["y2"]+20)], None, "proposed"),
        # Berm along south property line
        ("Pond Berm", [(POND["cx"]-POND["rx"]-10, POND["cy"]+POND["ry"]+5),
                       (POND["cx"]+POND["rx"]+10, POND["cy"]+POND["ry"]+5)], None, "proposed"),
    ]
    _pf(root, "C-GRAD", grade_features)

    # --- Utility easement ---
    _pf(root, "C-ESMT", [
        ("Utility Easement", [(0, ROAD_Y1-15), (SITE_W, ROAD_Y1-15),
                              (SITE_W, ROAD_Y1-35), (0, ROAD_Y1-35), (0, ROAD_Y1-15)],
         None, "existing"),
    ])

    # --- Driveway entrance ---
    _pf(root, "C-ROAD-DRVW", [
        ("Driveway Entrance",
         [(340, ROAD_Y2), (340, pk["y1"]), (370, pk["y1"]), (370, ROAD_Y2), (340, ROAD_Y2)],
         None, "proposed"),
    ])


def _rect(x1, y1, x2, y2):
    return [(x1,y1), (x2,y1), (x2,y2), (x1,y2), (x1,y1)]

def _ellipse(cx, cy, rx, ry, n=40):
    pts = []
    for i in range(n + 1):
        t = 2 * math.pi * (i % n) / n
        pts.append((cx + rx * math.cos(t), cy + ry * math.sin(t)))
    return pts

def _pf(root, layer, features):
    pf = SubElement(root, "PlanFeatures", name=layer, desc=layer)
    for name, coords, elev, state in features:
        feat = SubElement(pf, "PlanFeature", name=name, desc=name, state=state)
        cg = SubElement(feat, "CoordGeom")
        for i in range(len(coords) - 1):
            x1, y1 = coords[i]
            x2, y2 = coords[i+1]
            e1 = elev if elev is not None else get_elevation(x1, y1)
            e2 = elev if elev is not None else get_elevation(x2, y2)
            ln = SubElement(cg, "Line")
            SubElement(ln, "Start").text = f"{y1:.3f} {x1:.3f} {e1:.3f}"
            SubElement(ln, "End").text = f"{y2:.3f} {x2:.3f} {e2:.3f}"


# ============================================================================
# Companion data
# ============================================================================

def export_companion_data(out):
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    feats = [{"type": "Feature",
              "geometry": {"type": "Point", "coordinates": [s["x"], s["y"]]},
              "properties": {"structure_id": n, "name": n, "rim_elevation": s["rim"]}}
             for n, s in STRUCTURES.items()]
    with open(out/"inlet_structures.json",'w') as f:
        json.dump({"type": "FeatureCollection", "features": feats}, f, indent=2)
    pf = [{"type": "Feature",
           "geometry": {"type": "LineString",
                       "coordinates": [[STRUCTURES[fr]["x"],STRUCTURES[fr]["y"]],
                                      [STRUCTURES[to]["x"],STRUCTURES[to]["y"]]]},
           "properties": {"name": pn}} for pn, fr, to in PIPES]
    with open(out/"pipe_lines.json",'w') as f:
        json.dump({"type": "FeatureCollection", "features": pf}, f, indent=2)
    _ensure_grid()
    cs = 2.0
    c2 = int(SITE_W/cs)+1
    r2 = int(SITE_H/cs)+1
    data = np.zeros((r2,c2), dtype=np.float32)
    for r in range(r2):
        for c in range(c2):
            data[r,c] = get_elevation(c*cs, SITE_H-r*cs)
    (out/"surface_dem.bil").write_bytes(data.tobytes())
    with open(out/"surface_dem.hdr",'w') as f:
        f.write(f"NROWS {r2}\nNCOLS {c2}\nNBANDS 1\nNBITS 32\nPIXELTYPE FLOAT\n")
        f.write(f"BYTEORDER I\nLAYOUT BIL\nULXMAP {cs/2}\nULYMAP {SITE_H-cs/2}\n")
        f.write(f"XDIM {cs}\nYDIM {cs}\nNODATA -9999\n")
    meta = {"surface_name":"Proposed Grade","cell_size":cs,"rows":r2,"cols":c2,
            "origin_x":cs/2,"origin_y":SITE_H-cs/2,"no_data":-9999,
            "drawing_units":"ft","is_metric":False,"coordinate_system":"","autodesk_cs_code":""}
    with open(out/"surface_dem.json",'w') as f: json.dump(meta,f,indent=2)
    with open(out/"workflow_config.json",'w') as f:
        json.dump({"drawing_units":"ft","is_metric":False,
                   "surface":{"name":"Proposed Grade","dem_file":str(out/"surface_dem.bil"),
                             "cell_size":cs,"rows":r2,"columns":c2},
                   "pipe_network":{"name":"Storm Network",
                                  "inlets_file":str(out/"inlet_structures.json"),
                                  "inlet_count":len(STRUCTURES)}},f,indent=2)


# ============================================================================
# DXF export — linework on proper layers with colors
# ============================================================================

# AutoCAD color indices
CYAN, GREEN, RED, YELLOW, MAGENTA, WHITE, BLUE = 4, 3, 1, 2, 6, 7, 5

LAYERS = {
    "C-PROP-BNDY": {"color": GREEN,   "desc": "Property Boundary"},
    "C-BLDG-FTPT": {"color": CYAN,    "desc": "Building Footprint"},
    "C-SDWK":      {"color": WHITE,   "desc": "Sidewalks"},
    "C-PKNG":      {"color": WHITE,   "desc": "Parking"},
    "C-CURB":      {"color": YELLOW,  "desc": "Curb and Gutter"},
    "C-ROAD":      {"color": WHITE,   "desc": "Road"},
    "C-STRM-POND": {"color": BLUE,    "desc": "Stormwater Pond"},
    "C-GRAD":      {"color": RED,     "desc": "Grading"},
    "C-ESMT":      {"color": MAGENTA, "desc": "Easement"},
    "C-ROAD-DRVW": {"color": WHITE,   "desc": "Driveway"},
}


def export_dxf(out_path):
    """Write a DXF R12 file with site linework on proper named layers."""
    b = BLDG
    pk = PARK

    # Collect all polylines: (layer, [(x,y,z), ...], closed)
    polys = []

    # Property boundary
    polys.append(("C-PROP-BNDY", _rect(0, 0, SITE_W, SITE_H), True))

    # Building
    polys.append(("C-BLDG-FTPT", [(b["x1"],b["y1"]), (b["x2"],b["y1"]),
                                    (b["x2"],b["y2"]), (b["x1"],b["y2"])], True))

    # Sidewalks
    polys.append(("C-SDWK", _rect(b["x1"], b["y1"]-SIDEWALK_W, b["x2"], b["y1"]), True))
    polys.append(("C-SDWK", _rect(b["x2"], b["y1"]-SIDEWALK_W, b["x2"]+SIDEWALK_W, b["y1"]+50), True))
    polys.append(("C-SDWK", _rect(200, ROAD_Y2, 200+SIDEWALK_W, b["y1"]-SIDEWALK_W), True))

    # Parking
    polys.append(("C-PKNG", _rect(pk["x1"], pk["y1"], pk["x2"], pk["y2"]), True))
    polys.append(("C-PKNG", _rect(pk["x1"], 270, pk["x2"], 300), True))  # drive aisle N
    polys.append(("C-PKNG", _rect(pk["x1"], 185, pk["x2"], 195), True))  # drive aisle S
    # Islands
    polys.append(("C-PKNG", _rect(210, 210, 222, 280), True))
    polys.append(("C-PKNG", _rect(360, 210, 372, 280), True))
    polys.append(("C-PKNG", _rect(510, 210, 522, 280), True))

    # Curb lines
    polys.append(("C-CURB", [(0, ROAD_Y2), (SITE_W, ROAD_Y2)], False))
    polys.append(("C-CURB", [(0, ROAD_Y1), (SITE_W, ROAD_Y1)], False))
    polys.append(("C-CURB", _rect(pk["x1"]-0.5, pk["y1"]-0.5, pk["x2"]+0.5, pk["y2"]+0.5), True))

    # Road
    polys.append(("C-ROAD", [(0, (ROAD_Y1+ROAD_Y2)/2), (SITE_W, (ROAD_Y1+ROAD_Y2)/2)], False))
    polys.append(("C-ROAD", [(0, ROAD_Y2), (SITE_W, ROAD_Y2)], False))
    polys.append(("C-ROAD", [(0, ROAD_Y1), (SITE_W, ROAD_Y1)], False))

    # Pond
    polys.append(("C-STRM-POND", _ellipse(POND["cx"], POND["cy"], POND["rx"], POND["ry"]), True))
    polys.append(("C-STRM-POND", _ellipse(POND["cx"], POND["cy"], POND["rx"]*0.65, POND["ry"]*0.65), True))
    polys.append(("C-STRM-POND", _ellipse(POND["cx"], POND["cy"], POND["rx"]*0.35, POND["ry"]*0.35), True))

    # Grading limits
    polys.append(("C-GRAD", _rect(25, 15, 675, 485), True))

    # Easement
    polys.append(("C-ESMT", _rect(0, ROAD_Y1-15, SITE_W, ROAD_Y1-35), True))

    # Driveway
    polys.append(("C-ROAD-DRVW", _rect(340, ROAD_Y2, 370, pk["y1"]), True))

    import ezdxf

    doc = ezdxf.new('R2010')
    msp = doc.modelspace()

    # Create layers with colors
    for lname, linfo in LAYERS.items():
        doc.layers.add(lname, color=linfo['color'])

    # Add as individual LINE entities (no fill possible)
    for layer, pts, closed in polys:
        coords = list(pts)
        if closed and len(coords) > 1 and coords[0] != coords[-1]:
            coords.append(coords[0])
        for i in range(len(coords) - 1):
            x1, y1 = coords[i]
            x2, y2 = coords[i + 1]
            z1 = get_elevation(x1, y1)
            z2 = get_elevation(x2, y2)
            msp.add_line((x1, y1, z1), (x2, y2, z2), dxfattribs={'layer': layer})

    doc.saveas(out_path)


def main():
    out = Path(__file__).parent
    print("Building elevation grid...")
    _ensure_grid()
    print("Generating TIN points...")
    pts = generate_surface_points()
    print(f"  {len(pts)} points")
    faces = triangulate(pts)
    print(f"  {len(faces)} faces")
    print("Building LandXML (surface + pipe network)...")
    root = build_landxml(pts, faces)
    indent(root, space="  ")
    xml = out/"Oakwood_Meadows_Proposed.xml"
    ElementTree(root).write(str(xml), encoding="utf-8", xml_declaration=True)
    print(f"  {xml.name} ({xml.stat().st_size//1024}KB)")
    print("Building DXF (linework on layers)...")
    dxf = out/"Oakwood_Meadows_Site.dxf"
    export_dxf(str(dxf))
    print(f"  {dxf.name} ({dxf.stat().st_size//1024}KB)")
    print("Exporting companion data...")
    export_companion_data(out)
    elevs = [p[2] for p in pts]
    print(f"\n{'='*50}")
    print(f"Site: {SITE_W:.0f}x{SITE_H:.0f}ft ({SITE_W*SITE_H/43560:.1f}ac)")
    print(f"Surface: {len(pts)} pts, {len(faces)} faces, {min(elevs):.1f}-{max(elevs):.1f}ft")
    print(f"Structures: {len(STRUCTURES)} (all {STRUCT_DIA}in cylindrical)")
    print(f"Pipes: {len(PIPES)} (all {PIPE_DIA}in concrete)")
    print(f"\nImport order in Civil 3D:")
    print(f"  1. Insert > LandXML > {xml.name}  (surface + pipes)")
    print(f"  2. INSERT or OPEN > {dxf.name}     (linework on layers)")
    connected = set()
    for _, fr, to in PIPES:
        connected.add(fr); connected.add(to)
    orphans = [s for s in STRUCTURES if s not in connected]
    print(f"Connectivity: {'ALL OK' if not orphans else 'DISCONNECTED: ' + str(orphans)}")

if __name__ == "__main__":
    main()
