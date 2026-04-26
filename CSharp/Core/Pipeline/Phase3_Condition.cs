using CatchmentTool2.Network;
using CatchmentTool2.Surface;

namespace CatchmentTool2.Pipeline;

/// <summary>
/// Conditioning: identify pond cells (protected sinks), snap inlets to local minima,
/// then breach/fill unprotected depressions per the configured strategy.
///
/// Pond detection: cells within pondCaptureRadiusCells of a pond structure are flagged
/// "protected" — depression handling skips them so the real pond bottom is preserved.
/// </summary>
public sealed class StructureCellMap
{
    public Dictionary<string, (int i, int j)> StructureToCell { get; } = new();
    public Dictionary<int, string> CellToStructure { get; } = new();
    public HashSet<int> ProtectedCells { get; } = new();
}

public static class Phase3_Condition
{
    public static StructureCellMap ConditionGrid(Grid grid, IReadOnlyList<Structure> structures,
        TuningParameters p)
    {
        var map = new StructureCellMap();

        foreach (var s in structures)
        {
            var (i, j) = grid.CellAt(s.Location.X, s.Location.Y);
            if (!grid.HasData(i, j))
            {
                // search outward for a valid cell
                if (!FindNearestData(grid, i, j, 5, out i, out j)) continue;
            }
            map.StructureToCell[s.Id] = (i, j);
            map.CellToStructure[grid.Index(i, j)] = s.Id;
        }

        // Protect pond cells (within capture radius of any pond structure).
        double rCells = p.PondCaptureRadiusCells;
        int rInt = (int)Math.Ceiling(rCells);
        foreach (var s in structures.Where(s => s.Kind == StructureKind.Pond))
        {
            if (!map.StructureToCell.TryGetValue(s.Id, out var center)) continue;
            for (int dj = -rInt; dj <= rInt; dj++)
                for (int di = -rInt; di <= rInt; di++)
                {
                    int i2 = center.i + di, j2 = center.j + dj;
                    if (!grid.HasData(i2, j2)) continue;
                    if (di * di + dj * dj > rCells * rCells) continue;
                    map.ProtectedCells.Add(grid.Index(i2, j2));
                }
        }

        SnapInlets(grid, structures, map, p);

        switch (p.DepressionHandling)
        {
            case DepressionHandling.Fill:
                PriorityFloodFill(grid, map.ProtectedCells);
                break;
            case DepressionHandling.Breach:
                Breach(grid, map.ProtectedCells, p.MaxBreachDepth);
                break;
            case DepressionHandling.HybridBreachThenFill:
                Breach(grid, map.ProtectedCells, p.MaxBreachDepth);
                PriorityFloodFill(grid, map.ProtectedCells);
                break;
        }
        return map;
    }

    private static bool FindNearestData(Grid g, int i, int j, int maxR, out int oi, out int oj)
    {
        for (int r = 0; r <= maxR; r++)
        {
            for (int dj = -r; dj <= r; dj++)
                for (int di = -r; di <= r; di++)
                {
                    if (Math.Abs(di) != r && Math.Abs(dj) != r) continue;
                    if (g.HasData(i + di, j + dj)) { oi = i + di; oj = j + dj; return true; }
                }
        }
        oi = i; oj = j; return false;
    }

    private static void SnapInlets(Grid grid, IReadOnlyList<Structure> structures,
        StructureCellMap map, TuningParameters p)
    {
        int rInt = (int)Math.Ceiling(p.InletSnapRadiusCells);
        foreach (var s in structures.Where(s => s.Kind != StructureKind.Pond))
        {
            if (!map.StructureToCell.TryGetValue(s.Id, out var c)) continue;
            // Find the lowest neighbor in radius
            double bestZ = grid.Z[grid.Index(c.i, c.j)];
            for (int dj = -rInt; dj <= rInt; dj++)
                for (int di = -rInt; di <= rInt; di++)
                {
                    int i2 = c.i + di, j2 = c.j + dj;
                    if (!grid.HasData(i2, j2)) continue;
                    if (di * di + dj * dj > p.InletSnapRadiusCells * p.InletSnapRadiusCells) continue;
                    var z = grid.Z[grid.Index(i2, j2)];
                    if (z < bestZ) bestZ = z;
                }
            // Force inlet cell to be strictly below local min
            grid.Z[grid.Index(c.i, c.j)] = bestZ - p.InletSnapDepth;
        }
    }

    /// <summary>
    /// Wang-Liu priority flood fill (raise sinks to spill-point elevation).
    /// Protected cells are not modified but still participate as edges.
    /// </summary>
    private static void PriorityFloodFill(Grid g, HashSet<int> protectedCells)
    {
        var pq = new PriorityQueue<int, double>();
        var visited = new bool[g.Cols * g.Rows];
        // Seed: all boundary cells with data
        for (int j = 0; j < g.Rows; j++)
            for (int i = 0; i < g.Cols; i++)
            {
                if (!g.HasData(i, j)) continue;
                bool isEdge = i == 0 || j == 0 || i == g.Cols - 1 || j == g.Rows - 1
                    || !g.HasData(i - 1, j) || !g.HasData(i + 1, j)
                    || !g.HasData(i, j - 1) || !g.HasData(i, j + 1);
                if (isEdge)
                {
                    int idx = g.Index(i, j);
                    pq.Enqueue(idx, g.Z[idx]);
                    visited[idx] = true;
                }
            }
        while (pq.Count > 0)
        {
            pq.TryDequeue(out int idx, out double spill);
            int i = idx % g.Cols, j = idx / g.Cols;
            foreach (var (di, dj) in Grid.N8)
            {
                int i2 = i + di, j2 = j + dj;
                if (!g.HasData(i2, j2)) continue;
                int idx2 = g.Index(i2, j2);
                if (visited[idx2]) continue;
                visited[idx2] = true;
                if (!protectedCells.Contains(idx2) && g.Z[idx2] < spill)
                    g.Z[idx2] = spill;
                pq.Enqueue(idx2, g.Z[idx2]);
            }
        }
    }

    /// <summary>
    /// Lindsay-style least-cost-path breach (Lindsay 2016).
    /// For each unprotected sink, run priority-queue Dijkstra outward where edge cost is
    /// max(0, sinkZ - neighborZ) — i.e., how deep the path must be carved at that cell.
    /// The "cost" of a path is the max breach-depth along it. Stop when we reach a cell
    /// strictly lower than the sink. If the path's max cost ≤ maxBreachDepth, lower the
    /// path to a monotonically descending profile from sink to outlet.
    /// </summary>
    private static void Breach(Grid g, HashSet<int> protectedCells, double maxBreachDepth)
    {
        var sinks = new List<int>();
        for (int j = 0; j < g.Rows; j++)
            for (int i = 0; i < g.Cols; i++)
            {
                if (!g.HasData(i, j)) continue;
                int idx = g.Index(i, j);
                if (protectedCells.Contains(idx)) continue;
                if (IsLocalMin(g, i, j)) sinks.Add(idx);
            }

        foreach (var sink in sinks)
        {
            double sinkZ = g.Z[sink];
            var bestCost = new Dictionary<int, double> { [sink] = 0 };
            var parent = new Dictionary<int, int>();
            var pq = new PriorityQueue<int, double>();
            pq.Enqueue(sink, 0);
            int target = -1;
            int safety = 8000;
            while (pq.TryDequeue(out int c, out double cost) && safety-- > 0)
            {
                if (cost > bestCost[c]) continue;
                if (cost > maxBreachDepth) break;
                int ci = c % g.Cols, cj = c / g.Cols;
                foreach (var (di, dj) in Grid.N8)
                {
                    int i2 = ci + di, j2 = cj + dj;
                    if (!g.HasData(i2, j2)) continue;
                    int idx2 = g.Index(i2, j2);
                    double zN = g.Z[idx2];
                    if (zN < sinkZ) { parent[idx2] = c; target = idx2; goto found; }
                    // Cost = max breach depth to traverse so far
                    double edgeCost = Math.Max(0, sinkZ - zN);
                    double newCost = Math.Max(cost, edgeCost);
                    if (newCost > maxBreachDepth) continue;
                    if (bestCost.TryGetValue(idx2, out double prev) && prev <= newCost) continue;
                    bestCost[idx2] = newCost;
                    parent[idx2] = c;
                    pq.Enqueue(idx2, newCost);
                }
            }
            found:
            if (target < 0) continue;
            var path = new List<int>();
            int cur = target;
            while (cur != sink) { path.Add(cur); cur = parent[cur]; }
            path.Add(sink);
            double tz = g.Z[target];
            double drop = sinkZ - tz;
            for (int k = 0; k < path.Count; k++)
            {
                int pidx = path[k];
                if (protectedCells.Contains(pidx)) continue;
                double frac = (double)k / Math.Max(1, path.Count - 1);
                double newZ = sinkZ - drop * (1 - frac);
                if (newZ < g.Z[pidx]) g.Z[pidx] = newZ;
            }
        }
    }

    private static bool IsLocalMin(Grid g, int i, int j)
    {
        double z = g.Z[g.Index(i, j)];
        foreach (var (di, dj) in Grid.N8)
        {
            int i2 = i + di, j2 = j + dj;
            if (!g.HasData(i2, j2)) continue;
            if (g.Z[g.Index(i2, j2)] < z) return false;
        }
        return true;
    }
}
