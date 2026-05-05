"""Trim each LandXML file to one surface (proposed/finished) and one pipe network."""
from pathlib import Path
from lxml import etree

LANDXML_DIR = Path("/Users/jchollingsworth/Documents/CatchmentTool2/test_data/landxml")

# File-specific surface to keep (None = keep the only surface present)
SURFACE_TO_KEEP = {
    "siteops.xml": "Surface Proposed",
}


def q(ns, tag):
    return f"{{{ns}}}{tag}"


def get_face_count(surface, ns):
    return len(surface.findall(f".//{q(ns, 'Faces')}/{q(ns, 'F')}"))


def pick_surface(surfaces, ns, preferred_name):
    if preferred_name:
        for s in surfaces:
            if s.get("name") == preferred_name:
                return s
    # Fallback: surface with most faces
    return max(surfaces, key=lambda s: get_face_count(s, ns))


def clean_file(path: Path):
    tree = etree.parse(str(path))
    root = tree.getroot()
    ns = root.tag.split("}")[0].strip("{")

    report = {"file": path.name, "surfaces_before": 0, "surfaces_after": 0,
              "networks_before": 0, "networks_after": 0,
              "pipes_total": 0, "structs_total": 0, "kept_surface": None}

    # --- Surfaces ---
    surfaces_elem = root.find(q(ns, "Surfaces"))
    if surfaces_elem is not None:
        surfaces = surfaces_elem.findall(q(ns, "Surface"))
        report["surfaces_before"] = len(surfaces)
        if surfaces:
            preferred = SURFACE_TO_KEEP.get(path.name)
            kept = pick_surface(surfaces, ns, preferred)
            report["kept_surface"] = kept.get("name")
            for s in surfaces:
                if s is not kept:
                    surfaces_elem.remove(s)
            report["surfaces_after"] = 1

    # --- PipeNetworks ---
    pn_container = root.find(q(ns, "PipeNetworks"))
    if pn_container is not None:
        networks = pn_container.findall(q(ns, "PipeNetwork"))
        report["networks_before"] = len(networks)

        if len(networks) > 1:
            primary = networks[0]
            # Find or create Pipes/Structs containers in primary
            primary_pipes = primary.find(q(ns, "Pipes"))
            primary_structs = primary.find(q(ns, "Structs"))
            if primary_pipes is None:
                primary_pipes = etree.SubElement(primary, q(ns, "Pipes"))
            if primary_structs is None:
                primary_structs = etree.SubElement(primary, q(ns, "Structs"))

            for n in networks[1:]:
                pipes = n.find(q(ns, "Pipes"))
                structs = n.find(q(ns, "Structs"))
                if pipes is not None:
                    for p in list(pipes):
                        primary_pipes.append(p)
                if structs is not None:
                    for s in list(structs):
                        primary_structs.append(s)
                pn_container.remove(n)

            primary.set("name", "Combined Network")
            primary.set("desc", "Combined from multiple source networks")

        # Final counts
        remaining = pn_container.findall(q(ns, "PipeNetwork"))
        report["networks_after"] = len(remaining)
        for n in remaining:
            pipes = n.find(q(ns, "Pipes"))
            structs = n.find(q(ns, "Structs"))
            report["pipes_total"] += len(pipes) if pipes is not None else 0
            report["structs_total"] += len(structs) if structs is not None else 0

    tree.write(str(path), xml_declaration=True, encoding="UTF-8", standalone=False)
    return report


def main():
    files = sorted(LANDXML_DIR.glob("*.xml"))
    print(f"{'File':<40} {'Surf':>10} {'Net':>10} {'Pipes':>6} {'Strs':>6}  Kept Surface")
    print("-" * 110)
    for f in files:
        r = clean_file(f)
        surf = f"{r['surfaces_before']}->{r['surfaces_after']}"
        net = f"{r['networks_before']}->{r['networks_after']}"
        print(f"{r['file']:<40} {surf:>10} {net:>10} {r['pipes_total']:>6} {r['structs_total']:>6}  {r['kept_surface']}")


if __name__ == "__main__":
    main()
