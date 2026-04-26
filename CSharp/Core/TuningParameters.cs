namespace CatchmentTool2;

public enum DepressionHandling { Fill, Breach, HybridBreachThenFill }
public enum RoutingAlgorithm { D8, DInf }
public enum FallbackMetric { NearestEuclidean, DownhillWeighted, DropTailInfluenced }
public enum BoundarySource { UserPolygon, TinHull, StructureBufferHull }

/// <summary>
/// Tunable parameters for the catchment delineation pipeline.
/// Top 10 high-impact knobs identified in CLAUDE.md "Tuning Parameters" section.
/// All others use built-in defaults; expose if a future sensitivity run says they matter.
/// </summary>
public sealed record TuningParameters
{
    // Phase 1 — boundary
    public BoundarySource BoundarySource { get; init; } = BoundarySource.StructureBufferHull;
    public double StructureBufferDistance { get; init; } = 50.0;

    // Phase 2 — raster (rank #1)
    public double CellSize { get; init; } = 1.0;

    // Phase 3 — conditioning (ranks #2, #3, #4, #5)
    public double InletSnapRadiusCells { get; init; } = 3.0;
    public double InletSnapDepth { get; init; } = 0.2;
    public DepressionHandling DepressionHandling { get; init; } = DepressionHandling.HybridBreachThenFill;
    public double MaxBreachDepth { get; init; } = 1.0;
    public double PondCaptureRadiusCells { get; init; } = 3.0;

    // Phase 4 — routing (rank #6)
    public RoutingAlgorithm RoutingAlgorithm { get; init; } = RoutingAlgorithm.D8;
    /// <summary>
    /// D8/D-inf ignores slopes below this threshold (treats them as flat). Important on
    /// near-flat surfaces where noise-induced micro-slope produces parallel-flow artifacts.
    /// </summary>
    public double MinSlopeThreshold { get; init; } = 0.0;

    /// <summary>
    /// Depth (ft) to lower the rasterized surface along each pipe alignment, before
    /// depression handling. Forces overland flow that crosses a pipe trace to be captured
    /// by the inlet upstream. 0 disables pipe burning. Standard MS4 GIS workflow.
    /// </summary>
    public double PipeBurnDepth { get; init; } = 0.0;

    // Phase 5 — fallback (ranks #9, #10)
    public FallbackMetric FallbackMetric { get; init; } = FallbackMetric.DownhillWeighted;
    public double DownhillBiasWeight { get; init; } = 1.0;
    public double DownhillBiasLengthScale { get; init; } = 100.0;
    public double MaxFallbackDistance { get; init; } = double.PositiveInfinity;
    public double MinCatchmentArea { get; init; } = 100.0;

    // Phase 7 — smoothing (ranks #7, #8)
    public double RdpToleranceCellMultiplier { get; init; } = 1.0;
    public int ChaikinIterations { get; init; } = 2;
    public double MaxAreaDriftPercent { get; init; } = 2.0;

    public string ToCompactString() =>
        $"cs={CellSize:0.##} snap={InletSnapRadiusCells:0.##} dep={DepressionHandling} brch={MaxBreachDepth:0.##} pond={PondCaptureRadiusCells:0.##} alg={RoutingAlgorithm} fb={FallbackMetric} bias={DownhillBiasWeight:0.##} rdp={RdpToleranceCellMultiplier:0.##} chk={ChaikinIterations} minA={MinCatchmentArea:0}";
}
