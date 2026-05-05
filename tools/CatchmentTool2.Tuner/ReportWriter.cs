using System.Globalization;
using System.Text;
using CatchmentTool2;
using CatchmentTool2.Grading;

internal static class ReportWriter
{
    public static void WriteHtml(string path, string runId, TuningParameters best,
        double bestScore, IReadOnlyList<FinalFixtureRow> rows, IReadOnlyList<RoundSummary> rounds)
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>CatchmentTool2 — Tuning Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font:14px/1.5 -apple-system,Segoe UI,Helvetica,Arial,sans-serif;max-width:1200px;margin:2em auto;padding:0 1em;color:#222}");
        sb.AppendLine("h1{margin-top:0}h2{border-bottom:1px solid #ddd;padding-bottom:.3em;margin-top:1.6em}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;margin:.5em 0}");
        sb.AppendLine("th,td{border:1px solid #ddd;padding:.4em .6em;text-align:left;font-size:13px}");
        sb.AppendLine("th{background:#f4f4f4}");
        sb.AppendLine("td.num{text-align:right;font-variant-numeric:tabular-nums}");
        sb.AppendLine(".pill{display:inline-block;padding:2px 8px;border-radius:10px;background:#eef;margin-right:4px}");
        sb.AppendLine(".score-good{color:#166}.score-mid{color:#a60}.score-bad{color:#a22}");
        sb.AppendLine("img{max-width:100%;border:1px solid #ddd;margin:.5em 0}");
        sb.AppendLine(".fixture{margin-bottom:2em}");
        sb.AppendLine(".params{font-family:ui-monospace,Menlo,Consolas,monospace;font-size:12px;background:#f8f8f8;padding:.6em;border:1px solid #eee;border-radius:4px}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h1>CatchmentTool2 — Tuning Report</h1>");
        sb.AppendLine($"<p><strong>Run:</strong> {runId} &nbsp; <strong>Best score:</strong> <span class=\"score-good\">{bestScore.ToString("0.##", ic)}/100</span></p>");

        sb.AppendLine("<h2>Final Tuning Parameters</h2>");
        sb.AppendLine("<table><tr><th>Parameter</th><th>Value</th><th>Rank</th></tr>");
        AppendParamRow(sb, "Cell Size", best.CellSize.ToString("0.##", ic), "#1");
        AppendParamRow(sb, "Inlet Snap Radius (cells)", best.InletSnapRadiusCells.ToString("0.##", ic), "#2");
        AppendParamRow(sb, "Depression Handling", best.DepressionHandling.ToString(), "#3");
        AppendParamRow(sb, "Max Breach Depth", best.MaxBreachDepth.ToString("0.##", ic), "#4");
        AppendParamRow(sb, "Pond Capture Radius (cells)", best.PondCaptureRadiusCells.ToString("0.##", ic), "#5");
        AppendParamRow(sb, "Routing Algorithm", best.RoutingAlgorithm.ToString(), "#6");
        AppendParamRow(sb, "RDP Tolerance × cell", best.RdpToleranceCellMultiplier.ToString("0.##", ic), "#7");
        AppendParamRow(sb, "Chaikin Iterations", best.ChaikinIterations.ToString(), "#8");
        AppendParamRow(sb, "Min Catchment Area (ft²)", best.MinCatchmentArea.ToString("0.##", ic), "#9");
        AppendParamRow(sb, "Fallback Metric", best.FallbackMetric.ToString(), "#10");
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Tuning Progression</h2>");
        sb.AppendLine("<table><tr><th>Round</th><th>Best Score</th><th>Params</th></tr>");
        foreach (var r in rounds)
        {
            sb.AppendLine($"<tr><td class=\"num\">{r.Round}</td><td class=\"num\">{r.BestScore.ToString("0.##", ic)}</td><td class=\"params\">{Esc(r.BestParams.ToCompactString())}</td></tr>");
        }
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Per-Fixture Scores</h2>");
        sb.AppendLine("<table><tr><th>Fixture</th><th>Catch.</th><th>Score</th><th>Outlet %</th><th>Gap</th><th>Overlap</th><th>Flow %</th><th>Sliv</th><th>Micro</th><th>Smooth</th><th>Runtime</th></tr>");
        foreach (var r in rows)
        {
            var g = r.Grade;
            string scoreCls = g.WeightedScore >= 75 ? "score-good" : g.WeightedScore >= 50 ? "score-mid" : "score-bad";
            sb.AppendLine($"<tr><td>{Esc(r.Fixture)}</td><td class=\"num\">{r.CatchmentCount}</td>" +
                $"<td class=\"num {scoreCls}\">{g.WeightedScore.ToString("0.##", ic)}</td>" +
                $"<td class=\"num\">{g.OutletCoverageScore.ToString("0.#", ic)}</td>" +
                $"<td class=\"num\">{(g.GapFraction * 100).ToString("0.#", ic)}%</td>" +
                $"<td class=\"num\">{(g.OverlapFraction * 100).ToString("0.#", ic)}%</td>" +
                $"<td class=\"num\">{g.FlowPathCorrectness.ToString("0.#", ic)}</td>" +
                $"<td class=\"num\">{g.SliverCount}</td>" +
                $"<td class=\"num\">{g.MicroPolygonCount}</td>" +
                $"<td class=\"num\">{g.SmoothnessScore.ToString("0.###", ic)}</td>" +
                $"<td class=\"num\">{g.RuntimeSeconds.ToString("0.##", ic)}s</td></tr>");
        }
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>Catchment Maps</h2>");
        foreach (var r in rows)
        {
            var rel = "final/" + Path.GetFileName(r.PngPath);
            sb.AppendLine($"<div class=\"fixture\"><h3>{Esc(r.Fixture)}</h3>");
            sb.AppendLine($"<p><span class=\"pill\">{r.CatchmentCount} catchments</span> <span class=\"pill\">score {r.Grade.WeightedScore.ToString("0.##", ic)}</span> <span class=\"pill\">{r.Grade.RuntimeSeconds.ToString("0.##", ic)}s</span></p>");
            sb.AppendLine($"<img src=\"{Esc(rel)}\" alt=\"{Esc(r.Fixture)} catchments\"></div>");
        }
        sb.AppendLine("<p><small>Blue dots = ponds (selected). Red dots = inlets/junctions. Filled polygons = catchments. Gray lines = pipes.</small></p>");
        sb.AppendLine("</body></html>");
        File.WriteAllText(path, sb.ToString());
    }

    public static void WriteMarkdown(string path, string runId, TuningParameters best,
        double bestScore, IReadOnlyList<FinalFixtureRow> rows, IReadOnlyList<RoundSummary> rounds)
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"# CatchmentTool2 — Tuning Report\n");
        sb.AppendLine($"**Run:** {runId}  \n**Best score:** {bestScore.ToString("0.##", ic)}/100\n");
        sb.AppendLine("## Final Tuning Parameters\n");
        sb.AppendLine("| Rank | Parameter | Value |");
        sb.AppendLine("|------|-----------|-------|");
        sb.AppendLine($"| 1 | Cell Size | {best.CellSize.ToString("0.##", ic)} |");
        sb.AppendLine($"| 2 | Inlet Snap Radius (cells) | {best.InletSnapRadiusCells.ToString("0.##", ic)} |");
        sb.AppendLine($"| 3 | Depression Handling | {best.DepressionHandling} |");
        sb.AppendLine($"| 4 | Max Breach Depth | {best.MaxBreachDepth.ToString("0.##", ic)} |");
        sb.AppendLine($"| 5 | Pond Capture Radius (cells) | {best.PondCaptureRadiusCells.ToString("0.##", ic)} |");
        sb.AppendLine($"| 6 | Routing | {best.RoutingAlgorithm} |");
        sb.AppendLine($"| 7 | RDP Tol × cell | {best.RdpToleranceCellMultiplier.ToString("0.##", ic)} |");
        sb.AppendLine($"| 8 | Chaikin Iterations | {best.ChaikinIterations} |");
        sb.AppendLine($"| 9 | Min Catchment Area | {best.MinCatchmentArea.ToString("0.##", ic)} |");
        sb.AppendLine($"| 10 | Fallback Metric | {best.FallbackMetric} |\n");

        sb.AppendLine("## Tuning Progression\n");
        sb.AppendLine("| Round | Best Score | Params |");
        sb.AppendLine("|------|-----------|--------|");
        foreach (var r in rounds)
            sb.AppendLine($"| {r.Round} | {r.BestScore.ToString("0.##", ic)} | `{r.BestParams.ToCompactString()}` |");

        sb.AppendLine("\n## Per-Fixture Scores\n");
        sb.AppendLine("| Fixture | Catch. | Score | Outlet% | Gap | Overlap | Flow% | Sliv | Micro | Smooth | Runtime |");
        sb.AppendLine("|---------|--------|-------|---------|-----|---------|-------|------|-------|--------|---------|");
        foreach (var r in rows)
        {
            var g = r.Grade;
            sb.AppendLine($"| {r.Fixture} | {r.CatchmentCount} | {g.WeightedScore.ToString("0.##", ic)} | {g.OutletCoverageScore.ToString("0.#", ic)} | {(g.GapFraction * 100).ToString("0.#", ic)}% | {(g.OverlapFraction * 100).ToString("0.#", ic)}% | {g.FlowPathCorrectness.ToString("0.#", ic)} | {g.SliverCount} | {g.MicroPolygonCount} | {g.SmoothnessScore.ToString("0.###", ic)} | {g.RuntimeSeconds.ToString("0.##", ic)}s |");
        }

        sb.AppendLine("\n## Catchment Maps\n");
        foreach (var r in rows)
        {
            var rel = "final/" + Path.GetFileName(r.PngPath);
            sb.AppendLine($"### {r.Fixture}\n");
            sb.AppendLine($"![{r.Fixture}]({rel})\n");
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void AppendParamRow(StringBuilder sb, string name, string val, string rank)
    {
        sb.AppendLine($"<tr><td>{Esc(name)}</td><td><strong>{Esc(val)}</strong></td><td>{rank}</td></tr>");
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
