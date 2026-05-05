"""Build a single HTML report from runs/best-so-far/*.png + *.grade.json."""
import json
import os
from pathlib import Path

run_dir = Path("/Users/jchollingsworth/Documents/CatchmentTool2/runs/best-so-far")
out = run_dir / "report.html"

rows = []
for grade in sorted(run_dir.glob("*.grade.json")):
    name = grade.stem.replace(".grade", "")
    g = json.loads(grade.read_text())
    rows.append((name, g))

# Sort by score descending
rows.sort(key=lambda x: -x[1]["weighted_score"])

avg = sum(r[1]["weighted_score"] for r in rows) / max(1, len(rows))

best_params = dict(
    cell_size=6.9, inlet_snap=8.58, depression="Breach",
    max_breach=3.16, pond_radius=6.85, routing="D8",
    fallback="DownhillWeighted", bias=1.0,
    rdp=0.39, chaikin=1, min_area=84,
)

html = ["<!doctype html><html><head><meta charset='utf-8'>",
        "<title>Best So Far — CatchmentTool2</title>",
        "<style>",
        "body{font:14px/1.5 -apple-system,Segoe UI,Helvetica,sans-serif;max-width:1200px;margin:2em auto;padding:0 1em;color:#222}",
        "h1{margin-top:0}",
        "h2{border-bottom:1px solid #ddd;padding-bottom:.3em;margin-top:1.6em}",
        "table{border-collapse:collapse;width:100%;margin:.5em 0}",
        "th,td{border:1px solid #ddd;padding:.4em .6em;text-align:left;font-size:13px}",
        "th{background:#f4f4f4}",
        "td.num{text-align:right;font-variant-numeric:tabular-nums}",
        ".pill{display:inline-block;padding:2px 8px;border-radius:10px;background:#eef;margin-right:4px}",
        ".score-good{color:#166}.score-mid{color:#a60}.score-bad{color:#a22}",
        "img{max-width:100%;border:1px solid #ddd;margin:.5em 0}",
        ".fixture{margin-bottom:2em;padding-bottom:1em;border-bottom:1px solid #eee}",
        "</style></head><body>",
        "<h1>CatchmentTool2 — Best Result So Far (killed mid-run)</h1>",
        f"<p><strong>Average score:</strong> <span class='score-good'>{avg:.2f}/100</span> across {len(rows)} fixtures &nbsp; (testing5 skipped — over size limit)</p>",
        "<p><em>Note: tuner was killed during seed-2 round-3. Reported peak from the running tuner was 83.93 with the old 5-component composite. These scores are from re-rendering with the new 8-component composite (slivers, micros, sump containment now weighted), so they're lower.</em></p>",
        "<h2>Best Parameters (so far)</h2>",
        "<table><tr><th>Parameter</th><th>Value</th></tr>",
        ]
for k, v in best_params.items():
    html.append(f"<tr><td>{k}</td><td><strong>{v}</strong></td></tr>")
html.append("</table>")

html.append("<h2>Per-Fixture Scores</h2>")
html.append("<table><tr><th>Fixture</th><th>Score</th><th>Outlet%</th><th>Gap</th><th>Flow%</th><th>Slivers</th><th>Micros</th><th>Smooth</th><th>Runtime</th></tr>")
for name, g in rows:
    cls = "score-good" if g["weighted_score"] >= 75 else "score-mid" if g["weighted_score"] >= 50 else "score-bad"
    html.append(f"<tr><td>{name}</td>"
                f"<td class='num {cls}'>{g['weighted_score']:.1f}</td>"
                f"<td class='num'>{g['outlet_coverage_pct']:.0f}</td>"
                f"<td class='num'>{g['gap_fraction']*100:.1f}%</td>"
                f"<td class='num'>{g['flow_path_correctness_pct']:.0f}</td>"
                f"<td class='num'>{g['sliver_count']}</td>"
                f"<td class='num'>{g['micro_polygon_count']}</td>"
                f"<td class='num'>{g['smoothness']:.3f}</td>"
                f"<td class='num'>{g['runtime_seconds']:.2f}s</td></tr>")
html.append("</table>")

html.append("<h2>Catchment Maps (sorted by score, best first)</h2>")
for name, g in rows:
    cls = "score-good" if g["weighted_score"] >= 75 else "score-mid" if g["weighted_score"] >= 50 else "score-bad"
    html.append(f"<div class='fixture'><h3>{name} <span class='{cls}'>— {g['weighted_score']:.1f}</span></h3>")
    html.append(f"<p><span class='pill'>flow {g['flow_path_correctness_pct']:.0f}%</span>"
                f" <span class='pill'>gap {g['gap_fraction']*100:.1f}%</span>"
                f" <span class='pill'>{g['runtime_seconds']:.2f}s</span></p>")
    html.append(f"<img src='{name}.png' alt='{name}'></div>")
html.append("</body></html>")

out.write_text("\n".join(html))
print(f"Wrote {out}")
print(f"Average score: {avg:.2f}/100 across {len(rows)} fixtures")
