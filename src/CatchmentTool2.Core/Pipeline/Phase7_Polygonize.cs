using CatchmentTool2.Geometry;
using CatchmentTool2.Surface;

namespace CatchmentTool2.Pipeline;

public sealed record CatchmentPolygon(string StructureId, Polygon Geometry,
    double TopoAssignedCells, double FallbackAssignedCells);

public static class Phase7_Polygonize
{
    public static List<CatchmentPolygon> Polygonize(Grid grid, int[] labels,
        Dictionary<int, string> labelToStructureId,
        Dictionary<int, (int topo, int fallback)> assignmentBreakdown,
        TuningParameters p)
    {
        // Topology-aware smoothing: apply a 3x3 majority filter to the labeled raster
        // BEFORE marching-squares. Each cell still has a single label after filtering,
        // so polygons tile by construction (no gaps, no overlaps). Per-polygon Chaikin
        // smoothing breaks this — adjacent polygons displace shared boundaries
        // independently, creating slivers between them. Raster-level filtering smooths
        // everything once and consistently.
        var smoothedLabels = ApplyMajorityFilter(labels, grid.Cols, grid.Rows, iterations: 2);

        var rings = MarchingSquares.Trace(smoothedLabels, grid.Cols, grid.Rows,
            grid.OriginX, grid.OriginY, grid.CellSize);

        var rdpTol = p.RdpToleranceCellMultiplier * grid.CellSize;
        // Light per-polygon RDP simplification only (no Chaikin) — RDP collinear-vertex
        // removal preserves shared edges if the same edge had the same vertex spacing
        // in both polygons (which marching-squares guarantees on a labeled raster).
        int chaikinIters = 0;
        var result = new List<CatchmentPolygon>();
        foreach (var (label, ring) in rings)
        {
            if (!labelToStructureId.TryGetValue(label, out var sid)) continue;
            var smoothed = Smoothing.SmoothWithGuard(ring, rdpTol, chaikinIters,
                p.MaxAreaDriftPercent);
            var poly = new Polygon(smoothed);
            if (poly.Area < p.MinCatchmentArea) continue;
            var (topo, fb) = assignmentBreakdown.TryGetValue(label, out var br) ? br : (0, 0);
            result.Add(new CatchmentPolygon(sid, poly, topo, fb));
        }
        return result;
    }

    /// <summary>
    /// 3×3 majority filter on the labeled raster. For each cell, if a different label
    /// holds the strict majority of the 9-cell window (≥5 cells), reassign the cell.
    /// Smooths cell-boundary staircases without breaking the partition (every cell
    /// still has exactly one label, so polygons tile by construction).
    /// </summary>
    private static int[] ApplyMajorityFilter(int[] labels, int cols, int rows, int iterations)
    {
        if (iterations <= 0) return labels;
        var current = (int[])labels.Clone();
        var next = new int[current.Length];
        for (int iter = 0; iter < iterations; iter++)
        {
            Array.Copy(current, next, current.Length);
            for (int j = 1; j < rows - 1; j++)
            {
                for (int i = 1; i < cols - 1; i++)
                {
                    int idx = j * cols + i;
                    if (current[idx] == 0) continue;
                    int bestLabel = current[idx];
                    int bestCount = 0;
                    var counts = new Dictionary<int, int>();
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int nidx = (j + dj) * cols + (i + di);
                            int label = current[nidx];
                            if (label == 0) continue;
                            int c = counts.GetValueOrDefault(label, 0) + 1;
                            counts[label] = c;
                            if (c > bestCount) { bestCount = c; bestLabel = label; }
                        }
                    // Strict majority of the 9-cell window.
                    if (bestCount >= 5 && bestLabel != current[idx])
                        next[idx] = bestLabel;
                }
            }
            (current, next) = (next, current);
        }
        return current;
    }
}
