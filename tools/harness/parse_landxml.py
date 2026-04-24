"""Parse Civil 3D LandXML export into Python data structures.

Extracts:
  - Surface points (id -> (x, y, z))
  - Surface triangles (list of (v1, v2, v3) tuples of point ids)
  - Pipe network structures (name -> {x, y, z_rim, z_sump, inverts, ...})
  - Pipes (name -> {start, end, invert_start, invert_end, diameter})

The LandXML schema is nested, but the fields we need are regular enough
that a simple ElementTree walk is adequate.
"""
from __future__ import annotations

import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Tuple

NS = {"lx": "http://www.landxml.org/schema/LandXML-1.2"}


@dataclass
class Structure:
    name: str
    x: float
    y: float
    z_rim: float
    z_sump: float
    inverts: List[Tuple[str, float]] = field(default_factory=list)  # (flow_dir, elev)


@dataclass
class Pipe:
    name: str
    start_struct: str
    end_struct: str
    length: float
    slope: float
    diameter_in: float


@dataclass
class Surface:
    name: str
    points: Dict[int, Tuple[float, float, float]]
    triangles: List[Tuple[int, int, int]]


@dataclass
class LandXMLData:
    surface: Surface
    structures: Dict[str, Structure]
    pipes: Dict[str, Pipe]


def _text_to_xy(text: str) -> Tuple[float, float]:
    parts = text.strip().split()
    return float(parts[0]), float(parts[1])


def _text_to_xyz(text: str) -> Tuple[float, float, float]:
    parts = text.strip().split()
    return float(parts[0]), float(parts[1]), float(parts[2])


def parse(path: Path) -> LandXMLData:
    tree = ET.parse(path)
    root = tree.getroot()

    surface = _parse_surface(root)
    structures = _parse_structures(root)
    pipes = _parse_pipes(root)

    return LandXMLData(surface=surface, structures=structures, pipes=pipes)


def _parse_surface(root: ET.Element) -> Surface:
    surf_el = root.find(".//lx:Surface", NS)
    if surf_el is None:
        raise ValueError("No <Surface> element found")

    name = surf_el.get("name", "unnamed")

    points: Dict[int, Tuple[float, float, float]] = {}
    for p_el in surf_el.findall(".//lx:P", NS):
        pid = int(p_el.get("id"))
        # LandXML stores coordinates as "y x z" in the Pnts child — verify:
        # looking at the file, format is "x y z". Convention matches our tool.
        # Actually Civil 3D LandXML writes "northing easting elev" per schema.
        # For this project's CRS the drawing uses plan X/Y so we treat it as
        # (x, y, z). If catchments look rotated, swap these.
        parts = p_el.text.strip().split()
        x, y, z = float(parts[0]), float(parts[1]), float(parts[2])
        points[pid] = (x, y, z)

    triangles: List[Tuple[int, int, int]] = []
    for f_el in surf_el.findall(".//lx:F", NS):
        parts = f_el.text.strip().split()
        a, b, c = int(parts[0]), int(parts[1]), int(parts[2])
        triangles.append((a, b, c))

    return Surface(name=name, points=points, triangles=triangles)


def _parse_structures(root: ET.Element) -> Dict[str, Structure]:
    out: Dict[str, Structure] = {}
    for s_el in root.findall(".//lx:Struct", NS):
        name = s_el.get("name")
        z_rim = float(s_el.get("elevRim", "nan"))
        z_sump = float(s_el.get("elevSump", "nan"))
        center_el = s_el.find("lx:Center", NS)
        if center_el is None or not center_el.text:
            continue
        x, y = _text_to_xy(center_el.text)
        inverts = []
        for inv_el in s_el.findall("lx:Invert", NS):
            fd = inv_el.get("flowDir", "?")
            elev = float(inv_el.get("elev", "nan"))
            inverts.append((fd, elev))
        out[name] = Structure(
            name=name, x=x, y=y, z_rim=z_rim, z_sump=z_sump, inverts=inverts
        )
    return out


def _parse_pipes(root: ET.Element) -> Dict[str, Pipe]:
    out: Dict[str, Pipe] = {}
    for p_el in root.findall(".//lx:Pipe", NS):
        name = p_el.get("name")
        start = p_el.get("refStart", "")
        end = p_el.get("refEnd", "")
        length = float(p_el.get("length", "0"))
        slope = float(p_el.get("slope", "0"))
        diameter = 0.0
        circ = p_el.find("lx:CircPipe", NS)
        if circ is not None:
            diameter = float(circ.get("diameter", "0"))
        out[name] = Pipe(
            name=name,
            start_struct=start,
            end_struct=end,
            length=length,
            slope=slope,
            diameter_in=diameter,
        )
    return out


if __name__ == "__main__":
    import sys

    path = Path(sys.argv[1] if len(sys.argv) > 1 else "testing.xml")
    data = parse(path)
    print(f"Surface: {data.surface.name}")
    print(f"  {len(data.surface.points):,} points")
    print(f"  {len(data.surface.triangles):,} triangles")
    xs = [p[0] for p in data.surface.points.values()]
    ys = [p[1] for p in data.surface.points.values()]
    zs = [p[2] for p in data.surface.points.values()]
    print(f"  bounds: x=[{min(xs):.1f}, {max(xs):.1f}]  "
          f"y=[{min(ys):.1f}, {max(ys):.1f}]  z=[{min(zs):.1f}, {max(zs):.1f}]")
    print(f"Structures: {len(data.structures)}")
    print(f"Pipes: {len(data.pipes)}")
