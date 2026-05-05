using System.Globalization;
using System.Diagnostics;
using CatchmentTool2;
using CatchmentTool2.Grading;
using CatchmentTool2.LandXml;
using CatchmentTool2.Output;
using CatchmentTool2.Pipeline;

// Random-sampling tuner with adaptive contraction.
// Round 1: SamplesPerRound random points from the full parameter space.
// Round N: same count, but each continuous axis sampled from a window centered on
//          the current winner that shrinks each round (contractFactor^round).
//          Categorical axes lock to the winner from round 2 onward.
// Convergence: stop when round-best improvement is below convergenceDelta.
//
// Total runs: SamplesPerRound × loaded fixtures × rounds_to_converge.

string fixturesDir = args.Length > 0 ? args[0]
    : "/Users/jchollingsworth/Documents/CatchmentTool2/test_data/landxml";
string runsRoot = args.Length > 1 ? args[1]
    : "/Users/jchollingsworth/Documents/CatchmentTool2/runs";
int maxRounds = args.Length > 2 ? int.Parse(args[2]) : 8;
int sizeLimitBytes = args.Length > 3 ? int.Parse(args[3]) : 5_000_000;
double convergenceDelta = args.Length > 4 ? double.Parse(args[4], CultureInfo.InvariantCulture) : 0.5;
int searchFlowSamples = args.Length > 5 ? int.Parse(args[5]) : 12;
int samplesPerRound = args.Length > 6 ? int.Parse(args[6]) : 4000;
int numSeeds = args.Length > 7 ? int.Parse(args[7]) : 3;
int finalFlowSamples = args.Length > 8 ? int.Parse(args[8]) : 60;

var fixtureFiles = Directory.GetFiles(fixturesDir, "*.xml")
    .OrderBy(f => new FileInfo(f).Length)
    .ToList();
Console.WriteLine($"Discovered {fixtureFiles.Count} fixtures in {fixturesDir}");
foreach (var f in fixtureFiles)
{
    long sz = new FileInfo(f).Length;
    var skip = sz > sizeLimitBytes ? "  [SKIP — over size limit]" : "";
    Console.WriteLine($"  {Path.GetFileName(f)} ({sz / 1024:N0} KB){skip}");
}

var activeFixtures = fixtureFiles
    .Where(f => new FileInfo(f).Length <= sizeLimitBytes)
    .ToList();

Console.WriteLine($"\nLoading {activeFixtures.Count} fixtures into memory...");
var loaded = new List<(string name, LandXmlData data)>();
foreach (var f in activeFixtures)
{
    var sw = Stopwatch.StartNew();
    try
    {
        var d = LandXmlReader.Read(f);
        if (d.Tin.Triangles.Count == 0 || d.Structures.Count == 0)
        {
            Console.WriteLine($"  {Path.GetFileName(f)} — skipped (TIN tris={d.Tin.Triangles.Count}, structs={d.Structures.Count})");
            continue;
        }
        loaded.Add((Path.GetFileNameWithoutExtension(f), d));
        Console.WriteLine($"  {Path.GetFileName(f)} — {d.Tin.Triangles.Count:N0} tris, {d.Structures.Count} structs, {d.PipeNetwork?.Pipes.Count ?? 0} pipes ({sw.Elapsed.TotalSeconds:F1}s)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {Path.GetFileName(f)} — FAILED: {ex.Message}");
    }
}
if (loaded.Count == 0)
{
    Console.Error.WriteLine("No usable fixtures loaded; aborting.");
    return 1;
}

string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
string runDir = Path.Combine(runsRoot, runId);
Directory.CreateDirectory(runDir);
string scoresCsv = Path.Combine(runDir, "scores.csv");
File.WriteAllText(scoresCsv,
    "round,combo,fixture,weighted,outlet_pct,gap,overlap,flow_pct,slivers,micro,smoothness,runtime_s," +
    "cell_size,inlet_snap,depression,max_breach,pond_radius,routing,fallback,bias,rdp_mult,chaikin,min_area\n");
var csvLock = new object();

Console.WriteLine($"\n=== Tuning run {runId} ===");
Console.WriteLine($"Output: {runDir}");
Console.WriteLine($"Mode: random sampling, {samplesPerRound} samples/round, max {maxRounds} rounds, {numSeeds} seeds");
Console.WriteLine($"Parallelism: {Environment.ProcessorCount} cores, search flow samples: {searchFlowSamples}");

// Continuous axis ranges (round 1 = full range; subsequent rounds contract around winner).
// Bounds calibrated against published literature (Zhang & Montgomery 1994, Vaze et al. 2010,
// Lindsay 2016, Springer 2024 on pipe burning, USGS StreamStats urban) — see CLAUDE.md.
// Cell size capped at 5 ft: above this, site-scale features (curbs, swales) become subgrid.
// Max breach capped at 2 ft: above this carves through standard retaining walls (3–4 ft).
// Pipe burn extended to 5 ft to match published practitioner depth.
var space = new SamplingSpace
{
    CellSize             = new Range(0.5, 5.0),
    InletSnapRadiusCells = new Range(0.5, 10.0),
    InletSnapDepth       = new Range(0.05, 1.0),
    MaxBreachDepth       = new Range(0.2, 2.0),
    PondCaptureRadius    = new Range(1.0, 8.0),
    RdpMultiplier        = new Range(0.1, 3.0),
    MinCatchmentArea     = new Range(10.0, 500.0),
    DownhillBiasWeight   = new Range(0.0, 5.0),
    MinSlopeThreshold    = new Range(0.0, 0.01),
    PipeBurnDepth        = new Range(0.0, 5.0),
    DepressionOptions    = new[] { CatchmentTool2.DepressionHandling.Fill,
                                   CatchmentTool2.DepressionHandling.Breach,
                                   CatchmentTool2.DepressionHandling.HybridBreachThenFill },
    RoutingOptions       = new[] { RoutingAlgorithm.D8, RoutingAlgorithm.DInf },
    FallbackOptions      = new[] { FallbackMetric.NearestEuclidean, FallbackMetric.DownhillWeighted },
    ChaikinOptions       = new[] { 0, 1, 2, 3 },
};

TuningParameters bestParams = new();
double bestScore = -1;
var roundSummaries = new List<RoundSummary>();
double contractFactor = 0.5; // round-2 window is 50% of round-1; round-3 is 25%, etc.

for (int seedIdx = 0; seedIdx < numSeeds; seedIdx++)
{
    int seed = 1234 + seedIdx * 7919;
    Console.WriteLine($"\n###### Seed {seedIdx + 1}/{numSeeds} (rng={seed}) ######");
    var rootRng = new Random(seed);
    TuningParameters seedBestParams = new();
    double seedBestScore = -1;
    double prevRoundBestScore = -1;
    int round = 0;
    while (round < maxRounds)
    {
    round++;
    var roundSpace = round == 1 ? space : space.Contract(seedBestParams, Math.Pow(contractFactor, round - 1));
    Console.WriteLine($"\n--- [seed {seedIdx + 1}] Round {round}/{maxRounds} (convergence Δ={convergenceDelta}) ---");
    Console.WriteLine($"  Sampling window: {roundSpace.Describe()}");

    var samples = new List<TuningParameters>(samplesPerRound);
    for (int i = 0; i < samplesPerRound; i++)
        samples.Add(roundSpace.Sample(rootRng, lockCategorical: round > 1, seedBestParams));

    var roundLock = new object();
    TuningParameters roundBest = bestParams;
    double roundBestScore = -1;
    int completed = 0;
    int reportEvery = Math.Max(1, samples.Count / 20);
    var roundSw = Stopwatch.StartNew();

    Parallel.ForEach(
        samples.Select((p, idx) => (p, idx)),
        new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
        item =>
        {
            var (p, idx) = item;
            double sumScore = 0;
            int n = 0;
            var rowsForCombo = new List<string>(loaded.Count);
            foreach (var (name, data) in loaded)
            {
                var input = new PipelineInput(data.Tin, data.Structures, data.PipeNetwork, null);
                try
                {
                    var result = CatchmentPipeline.Run(input, p);
                    var grade = Grader.Grade(result, input, p, flowSamplesPerCatchment: searchFlowSamples);
                    sumScore += grade.WeightedScore; n++;
                    rowsForCombo.Add(BuildCsvRow(round, idx + 1, name, grade, p));
                }
                catch
                {
                    rowsForCombo.Add(BuildCsvRow(round, idx + 1, name, EmptyGrade(), p));
                }
            }
            double avg = n == 0 ? 0 : sumScore / n;
            lock (csvLock) File.AppendAllLines(scoresCsv, rowsForCombo);
            lock (roundLock)
            {
                if (avg > roundBestScore) { roundBestScore = avg; roundBest = p; }
                completed++;
                if (completed % reportEvery == 0 || completed == samples.Count)
                {
                    var rate = completed / Math.Max(1e-3, roundSw.Elapsed.TotalSeconds);
                    var eta = (samples.Count - completed) / Math.Max(1e-3, rate);
                    Console.WriteLine($"  [{completed:N0}/{samples.Count:N0}] best so far {roundBestScore:F2}  rate {rate:F1}/s  eta {TimeSpan.FromSeconds(eta):mm\\:ss}");
                }
            }
        });

    if (roundBestScore > seedBestScore) { seedBestScore = roundBestScore; seedBestParams = roundBest; }
    if (roundBestScore > bestScore) { bestScore = roundBestScore; bestParams = roundBest; }
    roundSummaries.Add(new RoundSummary(round + seedIdx * 100, roundBestScore, roundBest));
    Console.WriteLine($"[seed {seedIdx + 1}] Round {round} best: {roundBestScore:F2}  {roundBest.ToCompactString()}");
    Console.WriteLine($"[seed {seedIdx + 1}] Round {round} took {roundSw.Elapsed.TotalSeconds:F1}s");

    double improvement = roundBestScore - prevRoundBestScore;
    if (round >= 2 && improvement < convergenceDelta)
    {
        Console.WriteLine($"[seed {seedIdx + 1}] Converged: improvement {improvement:F2} < Δ {convergenceDelta}");
        break;
    }
    prevRoundBestScore = roundBestScore;
    }
    Console.WriteLine($"###### Seed {seedIdx + 1} done — best {seedBestScore:F2}");
}

Console.WriteLine($"\n=== Tuning complete ===");
Console.WriteLine($"Best score: {bestScore:F2}  {bestParams.ToCompactString()}");

// Final pass — best params on every fixture, render PNGs and report.
Console.WriteLine($"\nRendering final report with best params (full flow samples)...");
var finalDir = Path.Combine(runDir, "final");
Directory.CreateDirectory(finalDir);
var finalRows = new List<FinalFixtureRow>();
foreach (var (name, data) in loaded)
{
    var input = new PipelineInput(data.Tin, data.Structures, data.PipeNetwork, null);
    var result = CatchmentPipeline.Run(input, bestParams);
    var grade = Grader.Grade(result, input, bestParams, flowSamplesPerCatchment: finalFlowSamples);
    string pngPath = Path.Combine(finalDir, $"{name}.png");
    CatchmentRenderer.Render(pngPath, result, data.Structures, data.PipeNetwork);
    GeoJsonWriter.Write(Path.Combine(finalDir, $"{name}.geojson"), result, data.Structures);
    finalRows.Add(new FinalFixtureRow(name, result.Catchments.Count, grade, pngPath));
    Console.WriteLine($"  {name}: score={grade.WeightedScore:F1} catchments={result.Catchments.Count}");
}

ReportWriter.WriteHtml(Path.Combine(runDir, "report.html"), runId, bestParams, bestScore,
    finalRows, roundSummaries);
ReportWriter.WriteMarkdown(Path.Combine(runDir, "report.md"), runId, bestParams, bestScore,
    finalRows, roundSummaries);

Console.WriteLine($"\nReport: {Path.Combine(runDir, "report.html")}");
return 0;

static string BuildCsvRow(int round, int combo, string fixture, GradeResult g, TuningParameters p)
{
    var ic = CultureInfo.InvariantCulture;
    return $"{round},{combo},{fixture},{g.WeightedScore.ToString("0.##", ic)},{g.OutletCoverageScore.ToString("0.##", ic)},{g.GapFraction.ToString("0.####", ic)},{g.OverlapFraction.ToString("0.####", ic)},{g.FlowPathCorrectness.ToString("0.##", ic)},{g.SliverCount},{g.MicroPolygonCount},{g.SmoothnessScore.ToString("0.####", ic)},{g.RuntimeSeconds.ToString("0.##", ic)}," +
        $"{p.CellSize.ToString("0.##", ic)},{p.InletSnapRadiusCells.ToString("0.##", ic)},{p.DepressionHandling},{p.MaxBreachDepth.ToString("0.##", ic)},{p.PondCaptureRadiusCells.ToString("0.##", ic)},{p.RoutingAlgorithm},{p.FallbackMetric},{p.DownhillBiasWeight.ToString("0.##", ic)},{p.RdpToleranceCellMultiplier.ToString("0.##", ic)},{p.ChaikinIterations},{p.MinCatchmentArea.ToString("0.##", ic)}";
}

static GradeResult EmptyGrade() => new(0, false, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

internal readonly record struct Range(double Lo, double Hi)
{
    public double Sample(Random r) => Lo + r.NextDouble() * (Hi - Lo);
    public Range Contract(double center, double factor)
    {
        double half = (Hi - Lo) * factor / 2.0;
        return new Range(Math.Max(Lo, center - half), Math.Min(Hi, center + half));
    }
    public override string ToString() => $"[{Lo:0.##}–{Hi:0.##}]";
}

internal sealed class SamplingSpace
{
    public required Range CellSize { get; init; }
    public required Range InletSnapRadiusCells { get; init; }
    public required Range InletSnapDepth { get; init; }
    public required Range MaxBreachDepth { get; init; }
    public required Range PondCaptureRadius { get; init; }
    public required Range RdpMultiplier { get; init; }
    public required Range MinCatchmentArea { get; init; }
    public required Range DownhillBiasWeight { get; init; }
    public required Range MinSlopeThreshold { get; init; }
    public required Range PipeBurnDepth { get; init; }
    public required DepressionHandling[] DepressionOptions { get; init; }
    public required RoutingAlgorithm[] RoutingOptions { get; init; }
    public required FallbackMetric[] FallbackOptions { get; init; }
    public required int[] ChaikinOptions { get; init; }

    // Hard cap on inlet snap radius in world units (feet). Prevents the optimizer from
    // chasing physically absurd snap distances when cs is large.
    public const double MaxInletSnapFeet = 50.0;

    public TuningParameters Sample(Random r, bool lockCategorical, TuningParameters bestParams)
    {
        double cs = CellSize.Sample(r);
        double snapMaxCells = MaxInletSnapFeet / cs;
        double snapCells = Math.Min(InletSnapRadiusCells.Sample(r), snapMaxCells);
        return new TuningParameters
        {
            CellSize = cs,
            InletSnapRadiusCells = snapCells,
            InletSnapDepth = InletSnapDepth.Sample(r),
            MaxBreachDepth = MaxBreachDepth.Sample(r),
            PondCaptureRadiusCells = PondCaptureRadius.Sample(r),
            RdpToleranceCellMultiplier = RdpMultiplier.Sample(r),
            MinCatchmentArea = MinCatchmentArea.Sample(r),
            DownhillBiasWeight = DownhillBiasWeight.Sample(r),
            MinSlopeThreshold = MinSlopeThreshold.Sample(r),
            PipeBurnDepth = PipeBurnDepth.Sample(r),
            DepressionHandling = lockCategorical ? bestParams.DepressionHandling : DepressionOptions[r.Next(DepressionOptions.Length)],
            RoutingAlgorithm   = lockCategorical ? bestParams.RoutingAlgorithm   : RoutingOptions[r.Next(RoutingOptions.Length)],
            FallbackMetric     = lockCategorical ? bestParams.FallbackMetric     : FallbackOptions[r.Next(FallbackOptions.Length)],
            ChaikinIterations  = lockCategorical ? bestParams.ChaikinIterations  : ChaikinOptions[r.Next(ChaikinOptions.Length)],
        };
    }

    public SamplingSpace Contract(TuningParameters best, double factor)
    {
        return new SamplingSpace
        {
            CellSize             = CellSize.Contract(best.CellSize, factor),
            InletSnapRadiusCells = InletSnapRadiusCells.Contract(best.InletSnapRadiusCells, factor),
            InletSnapDepth       = InletSnapDepth.Contract(best.InletSnapDepth, factor),
            MaxBreachDepth       = MaxBreachDepth.Contract(best.MaxBreachDepth, factor),
            PondCaptureRadius    = PondCaptureRadius.Contract(best.PondCaptureRadiusCells, factor),
            RdpMultiplier        = RdpMultiplier.Contract(best.RdpToleranceCellMultiplier, factor),
            MinCatchmentArea     = MinCatchmentArea.Contract(best.MinCatchmentArea, factor),
            DownhillBiasWeight   = DownhillBiasWeight.Contract(best.DownhillBiasWeight, factor),
            MinSlopeThreshold    = MinSlopeThreshold.Contract(best.MinSlopeThreshold, factor),
            PipeBurnDepth        = PipeBurnDepth.Contract(best.PipeBurnDepth, factor),
            DepressionOptions    = new[] { best.DepressionHandling },
            RoutingOptions       = new[] { best.RoutingAlgorithm },
            FallbackOptions      = new[] { best.FallbackMetric },
            ChaikinOptions       = new[] { best.ChaikinIterations },
        };
    }

    public string Describe() =>
        $"cs={CellSize} snap={InletSnapRadiusCells} snapZ={InletSnapDepth} brch={MaxBreachDepth} pond={PondCaptureRadius} rdp={RdpMultiplier} minA={MinCatchmentArea} bias={DownhillBiasWeight} minSlope={MinSlopeThreshold} burn={PipeBurnDepth} dep=[{string.Join(",", DepressionOptions)}] rout=[{string.Join(",", RoutingOptions)}] fb=[{string.Join(",", FallbackOptions)}] chk=[{string.Join(",", ChaikinOptions)}]";
}

internal sealed record RoundSummary(int Round, double BestScore, TuningParameters BestParams);
internal sealed record FinalFixtureRow(string Fixture, int CatchmentCount, GradeResult Grade, string PngPath);
