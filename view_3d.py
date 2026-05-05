"""Render a LandXML TIN + pipe network as an interactive 3D Plotly HTML.

Usage:
    python3 view_3d.py path/to/file.xml [output.html]

Output:
    A self-contained HTML file (Plotly via CDN) with the TIN as a colored mesh,
    structures as 3D dots (sized by rim elevation), and pipes as 3D line segments.
    Opens in browser; drag to rotate, scroll to zoom.
"""
from __future__ import annotations

import json
import os
import sys
from xml.etree import ElementTree as ET

NS = "{http://www.landxml.org/schema/LandXML-1.1}"


def parse_landxml(path: str):
    tree = ET.parse(path)
    root = tree.getroot()

    # Surface (largest by face count)
    surfaces = root.findall(f".//{NS}Surface")
    surface = max(surfaces, key=lambda s: len(s.findall(f".//{NS}F")), default=None)

    pnt_index = {}
    verts = []
    if surface is not None:
        for p in surface.findall(f".//{NS}P"):
            pid = p.attrib.get("id", str(len(verts)))
            text = (p.text or "").split()
            if len(text) < 3:
                continue
            # LandXML order: Y X Z
            y, x, z = float(text[0]), float(text[1]), float(text[2])
            pnt_index[pid] = len(verts)
            verts.append((x, y, z))
        tris = []
        for f in surface.findall(f".//{NS}F"):
            text = (f.text or "").split()
            if len(text) < 3:
                continue
            a, b, c = text[0], text[1], text[2]
            if a in pnt_index and b in pnt_index and c in pnt_index:
                tris.append((pnt_index[a], pnt_index[b], pnt_index[c]))
    else:
        tris = []

    structures = []  # list of dicts
    pipes = []
    for net in root.findall(f".//{NS}PipeNetwork"):
        for s in net.findall(f"{NS}Structs/{NS}Struct"):
            name = s.attrib.get("name", "")
            rim = float(s.attrib.get("elevRim", "nan"))
            sump = float(s.attrib.get("elevSump", "nan"))
            center = s.find(f"{NS}Center")
            if center is None:
                center = s.find(f"{NS}CntrLine")
            if center is None or not center.text:
                continue
            parts = center.text.split()
            if len(parts) < 2:
                continue
            y, x = float(parts[0]), float(parts[1])
            structures.append({"name": name, "x": x, "y": y, "rim": rim, "sump": sump})
        for p in net.findall(f"{NS}Pipes/{NS}Pipe"):
            ref_start = p.attrib.get("refStart", "")
            ref_end = p.attrib.get("refEnd", "")
            if ref_start and ref_end:
                pipes.append((ref_start, ref_end))

    # Auto-classify ponds: structures with no downstream pipe
    has_down = {p[0] for p in pipes}
    for s in structures:
        s["kind"] = "Pond" if s["name"] not in has_down else "Inlet"

    return verts, tris, structures, pipes


def write_html(verts, tris, structures, pipes, out_path: str, title: str):
    if not verts:
        raise SystemExit("no vertices found in LandXML")

    xs = [v[0] for v in verts]
    ys = [v[1] for v in verts]
    zs = [v[2] for v in verts]
    # Center the data so the camera default is sane
    cx = (min(xs) + max(xs)) / 2
    cy = (min(ys) + max(ys)) / 2
    cz = (min(zs) + max(zs)) / 2
    xs = [x - cx for x in xs]
    ys = [y - cy for y in ys]
    zs = [z - cz for z in zs]
    i = [t[0] for t in tris]
    j = [t[1] for t in tris]
    k = [t[2] for t in tris]

    by_name = {s["name"]: s for s in structures}
    inlet_pts = {"x": [], "y": [], "z": [], "name": []}
    pond_pts = {"x": [], "y": [], "z": [], "name": []}
    for s in structures:
        bucket = pond_pts if s["kind"] == "Pond" else inlet_pts
        bucket["x"].append(s["x"] - cx)
        bucket["y"].append(s["y"] - cy)
        bucket["z"].append((s["rim"] if s["rim"] == s["rim"] else cz) - cz)
        bucket["name"].append(s["name"])

    # Pipe segments: for each pipe, add 3 points (start, end, None) for line breaks
    pipe_x, pipe_y, pipe_z = [], [], []
    for a, b in pipes:
        if a not in by_name or b not in by_name:
            continue
        sa = by_name[a]; sb = by_name[b]
        za = sa["sump"] if sa["sump"] == sa["sump"] else sa["rim"]
        zb = sb["sump"] if sb["sump"] == sb["sump"] else sb["rim"]
        pipe_x += [sa["x"] - cx, sb["x"] - cx, None]
        pipe_y += [sa["y"] - cy, sb["y"] - cy, None]
        pipe_z += [(za if za == za else cz) - cz, (zb if zb == zb else cz) - cz, None]

    data = [
        {
            "type": "mesh3d",
            "x": xs, "y": ys, "z": zs,
            "i": i, "j": j, "k": k,
            "intensity": zs,
            "colorscale": "Earth",
            "opacity": 1.0,
            "name": "TIN",
            "showscale": True,
            "colorbar": {"title": {"text": "Elevation (rel)"}, "thickness": 12},
            "lighting": {"ambient": 0.6, "diffuse": 0.8, "specular": 0.05},
        },
        {
            "type": "scatter3d", "mode": "markers",
            "x": inlet_pts["x"], "y": inlet_pts["y"], "z": inlet_pts["z"],
            "marker": {"size": 4, "color": "red"},
            "text": inlet_pts["name"], "name": f"Inlets ({len(inlet_pts['x'])})",
            "hovertemplate": "%{text}<br>rim z=%{z:.2f}<extra></extra>",
        },
        {
            "type": "scatter3d", "mode": "markers",
            "x": pond_pts["x"], "y": pond_pts["y"], "z": pond_pts["z"],
            "marker": {"size": 8, "color": "blue", "symbol": "diamond"},
            "text": pond_pts["name"], "name": f"Ponds ({len(pond_pts['x'])})",
            "hovertemplate": "%{text}<br>rim z=%{z:.2f}<extra></extra>",
        },
        {
            "type": "scatter3d", "mode": "lines",
            "x": pipe_x, "y": pipe_y, "z": pipe_z,
            "line": {"color": "black", "width": 4},
            "name": f"Pipes ({len(pipes)})",
            "hoverinfo": "skip",
        },
    ]
    layout = {
        "title": {"text": title},
        "scene": {
            "xaxis": {"title": "X (rel ft)"},
            "yaxis": {"title": "Y (rel ft)"},
            "zaxis": {"title": "Z (rel ft)"},
            "aspectmode": "data",
        },
        "margin": {"l": 0, "r": 0, "t": 40, "b": 0},
        "legend": {"orientation": "h", "y": -0.02},
    }

    html = f"""<!doctype html>
<html><head>
<meta charset="utf-8">
<title>{title}</title>
<script src="https://cdn.plot.ly/plotly-2.35.2.min.js"></script>
<style>html,body,#g{{margin:0;height:100%;font-family:-apple-system,Segoe UI,sans-serif}}#g{{background:#fafafa}}</style>
</head><body>
<div id="g"></div>
<script>
const data = {json.dumps(data)};
const layout = {json.dumps(layout)};
Plotly.newPlot('g', data, layout, {{responsive: true}});
</script></body></html>"""
    with open(out_path, "w") as f:
        f.write(html)


def main():
    if len(sys.argv) < 2:
        print("usage: python3 view_3d.py path/to/file.xml [output.html]", file=sys.stderr)
        sys.exit(2)
    src = sys.argv[1]
    name = os.path.splitext(os.path.basename(src))[0]
    out = sys.argv[2] if len(sys.argv) > 2 else f"/tmp/{name}_3d.html"
    verts, tris, structures, pipes = parse_landxml(src)
    print(f"  TIN: {len(verts)} verts, {len(tris)} tris")
    print(f"  Structures: {len(structures)} ({sum(1 for s in structures if s['kind']=='Pond')} ponds)")
    print(f"  Pipes: {len(pipes)}")
    write_html(verts, tris, structures, pipes, out, name)
    print(f"  → {out}")


if __name__ == "__main__":
    main()
