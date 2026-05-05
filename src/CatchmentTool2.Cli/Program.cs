using System.Globalization;
using CatchmentTool2;
using CatchmentTool2.Grading;
using CatchmentTool2.LandXml;
using CatchmentTool2.Output;
using CatchmentTool2.Pipeline;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ct2 <input.xml> <output_dir> [param=value ...]");
    return 1;
}
var input = args[0];
var outDir = args[1];
Directory.CreateDirectory(outDir);

var p = new TuningParameters();
for (int i = 2; i < args.Length; i++)
{
    var kv = args[i].Split('=', 2);
    if (kv.Length != 2) continue;
    p = ApplyOverride(p, kv[0], kv[1]);
}

Console.WriteLine($"Reading {Path.GetFileName(input)}...");
var data = LandXmlReader.Read(input);
Console.WriteLine($"  TIN: {data.Tin.Vertices.Count:N0} vertices, {data.Tin.Triangles.Count:N0} triangles");
Console.WriteLine($"  Structures: {data.Structures.Count} (selected: {data.Structures.Count(s => s.UserSelected)})");
Console.WriteLine($"  Pipes: {data.PipeNetwork?.Pipes.Count ?? 0}");

var pipelineInput = new PipelineInput(data.Tin, data.Structures, data.PipeNetwork, null);
Console.WriteLine($"Running pipeline with {p.ToCompactString()}");
var result = CatchmentPipeline.Run(pipelineInput, p);
Console.WriteLine($"  Catchments: {result.Catchments.Count}");
Console.WriteLine($"  Topo-assigned: {result.TopoAssignedCells:N0}, Fallback: {result.FallbackAssignedCells:N0}, Unassigned: {result.UnassignedCells:N0}");
Console.WriteLine($"  Runtime: {result.RuntimeSeconds:F2}s");

var grade = Grader.Grade(result, pipelineInput, p);
Console.WriteLine($"  Score: {grade.WeightedScore:F1}/100  (gap={grade.GapFraction:P1}, flow={grade.FlowPathCorrectness:F0}%, slivers={grade.SliverCount})");

var name = Path.GetFileNameWithoutExtension(input);
GeoJsonWriter.Write(Path.Combine(outDir, $"{name}.catchments.geojson"), result, data.Structures);
CatchmentRenderer.Render(Path.Combine(outDir, $"{name}.png"), result, data.Structures, data.PipeNetwork);
File.WriteAllText(Path.Combine(outDir, $"{name}.grade.json"), GradeToJson(grade));
Console.WriteLine($"Wrote outputs to {outDir}/{name}.*");

return 0;

static string GradeToJson(GradeResult g)
{
    var ic = CultureInfo.InvariantCulture;
    return $$"""
    {
      "outlet_coverage_pct": {{g.OutletCoverageScore.ToString("0.##", ic)}},
      "gap_fraction": {{g.GapFraction.ToString("0.####", ic)}},
      "overlap_fraction": {{g.OverlapFraction.ToString("0.####", ic)}},
      "flow_path_correctness_pct": {{g.FlowPathCorrectness.ToString("0.##", ic)}},
      "sliver_count": {{g.SliverCount}},
      "micro_polygon_count": {{g.MicroPolygonCount}},
      "smoothness": {{g.SmoothnessScore.ToString("0.####", ic)}},
      "runtime_seconds": {{g.RuntimeSeconds.ToString("0.##", ic)}},
      "weighted_score": {{g.WeightedScore.ToString("0.##", ic)}}
    }
    """;
}

static TuningParameters ApplyOverride(TuningParameters p, string key, string val)
{
    var ic = CultureInfo.InvariantCulture;
    return key.ToLowerInvariant() switch
    {
        "cellsize" => p with { CellSize = double.Parse(val, ic) },
        "inletsnap" => p with { InletSnapRadiusCells = double.Parse(val, ic) },
        "depression" => p with { DepressionHandling = Enum.Parse<DepressionHandling>(val, true) },
        "maxbreach" => p with { MaxBreachDepth = double.Parse(val, ic) },
        "pondradius" => p with { PondCaptureRadiusCells = double.Parse(val, ic) },
        "routing" => p with { RoutingAlgorithm = Enum.Parse<RoutingAlgorithm>(val, true) },
        "fallback" => p with { FallbackMetric = Enum.Parse<FallbackMetric>(val, true) },
        "bias" => p with { DownhillBiasWeight = double.Parse(val, ic) },
        "rdp" => p with { RdpToleranceCellMultiplier = double.Parse(val, ic) },
        "chaikin" => p with { ChaikinIterations = int.Parse(val, ic) },
        "minarea" => p with { MinCatchmentArea = double.Parse(val, ic) },
        "snapdepth" => p with { InletSnapDepth = double.Parse(val, ic) },
        "minslope" => p with { MinSlopeThreshold = double.Parse(val, ic) },
        "burn" => p with { PipeBurnDepth = double.Parse(val, ic) },
        _ => p,
    };
}
