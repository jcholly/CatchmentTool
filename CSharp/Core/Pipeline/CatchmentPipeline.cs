using CatchmentTool2.Network;
using CatchmentTool2.Surface;

namespace CatchmentTool2.Pipeline;

public sealed record PipelineInput(Tin Tin, IReadOnlyList<Structure> Structures,
    PipeNetwork? Network, IReadOnlyList<Vec2>? UserBoundary);

public sealed record PipelineResult(
    List<CatchmentPolygon> Catchments,
    IReadOnlyList<Vec2> Boundary,
    Grid Grid,
    int TopoAssignedCells,
    int FallbackAssignedCells,
    int UnassignedCells,
    double RuntimeSeconds);

public static class CatchmentPipeline
{
    public static PipelineResult Run(PipelineInput input, TuningParameters p)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var boundary = Phase1_Boundary.DeriveBoundary(input.Tin, input.Structures, p, input.UserBoundary);
        var grid = Phase2_Rasterize.Rasterize(input.Tin, boundary, p.CellSize);
        Phase2b_PipeBurn.Burn(grid, input.Network, input.Structures, p.PipeBurnDepth);
        var map = Phase3_Condition.ConditionGrid(grid, input.Structures, p);
        var routed = Phase4_Route.Route(grid, map, p);

        // Snapshot topo-only labels for breakdown
        var topoLabels = (int[])routed.Labels.Clone();
        int topoAssignedCells = routed.AssignedCells;

        var labels = routed.Labels;
        Phase5_Fallback.FillUnassigned(grid, labels, map, p, routed.OffSite);

        // Compute per-label topo vs fallback counts
        var breakdown = new Dictionary<int, (int topo, int fallback)>();
        for (int i = 0; i < labels.Length; i++)
        {
            int lab = labels[i];
            if (lab == 0) continue;
            if (!grid.HasData(i % grid.Cols, i / grid.Cols)) continue;
            var (t, f) = breakdown.TryGetValue(lab, out var v) ? v : (0, 0);
            if (topoLabels[i] == lab) t++; else f++;
            breakdown[lab] = (t, f);
        }

        labels = Phase6_PipeUnion.UnionUpstream(labels, routed, input.Structures, input.Network);

        // Re-aggregate breakdown after union
        var unionedBreakdown = new Dictionary<int, (int topo, int fallback)>();
        for (int i = 0; i < labels.Length; i++)
        {
            int lab = labels[i];
            if (lab == 0) continue;
            if (!grid.HasData(i % grid.Cols, i / grid.Cols)) continue;
            var (t, f) = unionedBreakdown.TryGetValue(lab, out var v) ? v : (0, 0);
            if (topoLabels[i] == lab) t++; else f++;
            unionedBreakdown[lab] = (t, f);
        }

        var catchments = Phase7_Polygonize.Polygonize(grid, labels, routed.LabelToStructureId,
            unionedBreakdown, p);

        int unassigned = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            if (!grid.HasData(i % grid.Cols, i / grid.Cols)) continue;
            if (labels[i] == 0) unassigned++;
        }

        sw.Stop();
        int totalAssigned = unionedBreakdown.Values.Sum(v => v.topo + v.fallback);
        int topoAssigned = unionedBreakdown.Values.Sum(v => v.topo);
        return new PipelineResult(catchments, boundary, grid,
            topoAssigned, totalAssigned - topoAssigned, unassigned, sw.Elapsed.TotalSeconds);
    }
}
