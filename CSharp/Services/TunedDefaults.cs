using CatchmentTool2;

namespace CatchmentTool.Services
{
    /// <summary>
    /// Tuned algorithm parameters baked in from offline tuning runs (random
    /// sampling × multi-seed × 17 fixtures, composite v2 grading). These are
    /// the production values; the in-Civil-3D dialog does not expose them.
    /// To re-tune: run tools/CatchmentTool2.Tuner against new fixtures and
    /// update these constants.
    /// </summary>
    public static class TunedDefaults
    {
        public static readonly TuningParameters V2 = new TuningParameters
        {
            // Phase 1 — boundary
            BoundarySource             = BoundarySource.StructureBufferHull,
            StructureBufferDistance    = 50.0,
            // Phase 2 — raster
            CellSize                   = 13.2,   // best-of-tuning seed 1
            // Phase 3 — conditioning
            InletSnapRadiusCells       = 6.04,
            InletSnapDepth             = 0.30,
            DepressionHandling         = DepressionHandling.Fill,
            MaxBreachDepth             = 2.79,
            PondCaptureRadiusCells     = 3.74,
            PipeBurnDepth              = 1.5,    // standard MS4 GIS practice
            // Phase 4 — routing
            RoutingAlgorithm           = RoutingAlgorithm.D8,
            MinSlopeThreshold          = 0.001,
            // Phase 5 — fallback
            FallbackMetric             = FallbackMetric.DownhillWeighted,
            DownhillBiasWeight         = 0.59,
            DownhillBiasLengthScale    = 100.0,
            MinCatchmentArea           = 191.0,
            // Phase 7 — smoothing (note: Phase7 also forces 2 Chaikin iterations
            // unconditionally for plan presentability, even when this is 0).
            RdpToleranceCellMultiplier = 0.22,
            ChaikinIterations          = 0,
            MaxAreaDriftPercent        = 2.0,
        };
    }
}
