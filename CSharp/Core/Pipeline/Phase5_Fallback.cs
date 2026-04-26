using CatchmentTool2.Surface;

namespace CatchmentTool2.Pipeline;

/// <summary>
/// Multi-source Dijkstra from each labeled structure cell over the grid graph.
/// Cost from cell A to neighbor B = euclidean distance × (1 + k × max(0, zB - zA)/L).
/// Each cell gets the structure with lowest cost. Cells beyond MaxFallbackDistance
/// stay unassigned.
/// </summary>
public static class Phase5_Fallback
{
    public static int FillUnassigned(Grid grid, int[] labels, StructureCellMap map,
        TuningParameters p, bool[]? offsite = null)
    {
        if (p.FallbackMetric == FallbackMetric.NearestEuclidean)
            return FillNearestEuclidean(grid, labels, map, p.MaxFallbackDistance, offsite);
        return FillDownhillWeighted(grid, labels, map, p, offsite);
    }

    private static int FillNearestEuclidean(Grid g, int[] labels, StructureCellMap map, double maxDist,
        bool[]? offsite = null)
    {
        int filled = 0;
        for (int j = 0; j < g.Rows; j++)
            for (int i = 0; i < g.Cols; i++)
            {
                int idx = g.Index(i, j);
                if (!g.HasData(i, j) || labels[idx] != 0) continue;
                if (offsite != null && offsite[idx]) continue;
                var c = g.CellCenter(i, j);
                int bestLabel = 0; double bestD = double.PositiveInfinity;
                int label = 0;
                foreach (var kv in map.StructureToCell)
                {
                    label++;
                    var sc = g.CellCenter(kv.Value.i, kv.Value.j);
                    double d = c.DistanceTo(sc);
                    if (d < bestD) { bestD = d; bestLabel = label; }
                }
                if (bestD <= maxDist) { labels[idx] = bestLabel; filled++; }
            }
        return filled;
    }

    private static int FillDownhillWeighted(Grid g, int[] labels, StructureCellMap map,
        TuningParameters p, bool[]? offsite = null)
    {
        var cost = new double[g.Cols * g.Rows];
        Array.Fill(cost, double.PositiveInfinity);
        var pq = new PriorityQueue<int, double>();

        // Map cells to existing labels
        int idxLabel = 1;
        foreach (var kv in map.StructureToCell)
        {
            int idx = g.Index(kv.Value.i, kv.Value.j);
            cost[idx] = 0;
            // labels[idx] was set by Phase4
            pq.Enqueue(idx, 0);
            idxLabel++;
        }
        // Also seed any cell already assigned by D8 (cost 0 — propagate its label outward)
        for (int idx = 0; idx < labels.Length; idx++)
        {
            if (labels[idx] != 0 && double.IsPositiveInfinity(cost[idx]))
            {
                cost[idx] = 0;
                pq.Enqueue(idx, 0);
            }
        }

        double cs = g.CellSize;
        double k = p.DownhillBiasWeight;
        double L = Math.Max(1e-6, p.DownhillBiasLengthScale);
        double maxD = p.MaxFallbackDistance;

        int filled = 0;
        while (pq.TryDequeue(out int idx, out double c))
        {
            if (c > cost[idx]) continue;
            if (c > maxD) break;
            int i = idx % g.Cols, j = idx / g.Cols;
            double zHere = g.HasData(i, j) ? g.Z[idx] : double.NaN;
            int label = labels[idx];
            foreach (var (di, dj) in Grid.N8)
            {
                int i2 = i + di, j2 = j + dj;
                if (!g.HasData(i2, j2)) continue;
                int idx2 = g.Index(i2, j2);
                if (offsite != null && offsite[idx2]) continue; // skip off-site cells
                double dist = Grid.NeighborDistance(di, dj) * cs;
                double dz = double.IsNaN(zHere) ? 0 : Math.Max(0, g.Z[idx2] - zHere);
                double w = 1.0 + k * dz / L;
                double nc = c + dist * w;
                if (nc < cost[idx2])
                {
                    cost[idx2] = nc;
                    if (labels[idx2] == 0) { labels[idx2] = label; filled++; }
                    pq.Enqueue(idx2, nc);
                }
            }
        }
        return filled;
    }
}
