using CatchmentTool2.Surface;

namespace CatchmentTool2.Pipeline;

/// <summary>
/// Flow routing. Per-cell direction is computed by either D8 (steepest neighbor) or
/// D-infinity-snap-to-D8 (steepest of 8 triangular facets, snapped to closest D8 neighbor).
/// Each cell is then traced downstream until it reaches a labeled structure cell or runs
/// off the data area. min_slope_threshold rejects directions whose slope is below threshold
/// — important on near-flat surfaces where micro-slope noise produces parallel-flow artifacts.
/// </summary>
public static class Phase4_Route
{
    public sealed record RouteResult(int[] Labels, Dictionary<int, string> LabelToStructureId,
        int AssignedCells, int UnassignedCells, bool[] OffSite);

    public static RouteResult Route(Grid grid, StructureCellMap map, TuningParameters p)
    {
        var labels = new int[grid.Cols * grid.Rows];
        var labelToId = new Dictionary<int, string>();
        var idToLabel = new Dictionary<string, int>();
        int next = 1;
        foreach (var kv in map.StructureToCell)
        {
            idToLabel[kv.Key] = next;
            labelToId[next] = kv.Key;
            int idx = grid.Index(kv.Value.i, kv.Value.j);
            labels[idx] = next;
            next++;
        }

        var down = new int[grid.Cols * grid.Rows];
        Array.Fill(down, -1);
        for (int j = 0; j < grid.Rows; j++)
            for (int i = 0; i < grid.Cols; i++)
            {
                if (!grid.HasData(i, j)) continue;
                int idx = grid.Index(i, j);
                if (labels[idx] != 0) continue; // labeled cells terminate flow
                down[idx] = p.RoutingAlgorithm == RoutingAlgorithm.DInf
                    ? ComputeDinfDirection(grid, i, j, p.MinSlopeThreshold)
                    : ComputeD8Direction(grid, i, j, p.MinSlopeThreshold);
            }

        const int InProgress = -2;
        var trace = new int[grid.Cols * grid.Rows];
        Array.Fill(trace, -1);
        for (int idx = 0; idx < labels.Length; idx++)
        {
            if (labels[idx] != 0) { trace[idx] = labels[idx]; continue; }
            if (down[idx] < 0) { trace[idx] = 0; continue; }
        }
        for (int idx = 0; idx < labels.Length; idx++)
        {
            if (trace[idx] != -1) continue;
            int label = ResolveTrace(idx, down, labels, trace, InProgress);
            trace[idx] = label;
        }

        int assigned = 0, unassigned = 0;
        for (int idx = 0; idx < labels.Length; idx++)
        {
            int t = trace[idx];
            if (t > 0) { labels[idx] = t; assigned++; }
            if (grid.HasData(idx % grid.Cols, idx / grid.Cols))
            {
                if (labels[idx] == 0) unassigned++;
            }
        }

        // Off-site detection: only LITERAL boundary cells are off-site — interior cells
        // (even if their notional D8 path eventually reaches the boundary) should still
        // fall through to Voronoi fallback. We only exclude the thin layer of edge cells
        // that genuinely "drain off the surface". Interior pits get filled normally.
        var offsite = new bool[labels.Length];
        for (int idx = 0; idx < labels.Length; idx++)
        {
            if (labels[idx] != 0) continue;
            int i = idx % grid.Cols, j = idx / grid.Cols;
            if (!grid.HasData(i, j)) continue;
            // Boundary cell = adjacent to NoData (or grid edge) AND has no downhill in-data neighbor.
            // This excludes interior flats from being marked off-site.
            if (down[idx] != -1) continue;
            bool atBoundary = false;
            foreach (var (di, dj) in Grid.N8)
            {
                int i2 = i + di, j2 = j + dj;
                if (!grid.InBounds(i2, j2) || !grid.HasData(i2, j2)) { atBoundary = true; break; }
            }
            offsite[idx] = atBoundary;
        }
        return new RouteResult(labels, labelToId, assigned, unassigned, offsite);
    }

    private static int ComputeD8Direction(Grid grid, int i, int j, double minSlope)
    {
        double z = grid.Z[grid.Index(i, j)];
        double bestSlope = minSlope;
        int bestIdx = -1;
        foreach (var (di, dj) in Grid.N8)
        {
            int i2 = i + di, j2 = j + dj;
            if (!grid.HasData(i2, j2)) continue;
            double dz = z - grid.Z[grid.Index(i2, j2)];
            if (dz <= 0) continue;
            double dist = Grid.NeighborDistance(di, dj) * grid.CellSize;
            double slope = dz / dist;
            if (slope > bestSlope) { bestSlope = slope; bestIdx = grid.Index(i2, j2); }
        }
        return bestIdx;
    }

    /// <summary>
    /// D-infinity (Tarboton 1997) snapped to D8. For each of the 8 triangular facets
    /// formed by the cell and two adjacent neighbors, compute the in-plane gradient and
    /// pick the facet with steepest descent. Snap the descent direction to the closest
    /// D8 neighbor (so output is still an integer cell index).
    /// </summary>
    private static int ComputeDinfDirection(Grid grid, int i, int j, double minSlope)
    {
        double zc = grid.Z[grid.Index(i, j)];
        double bestSlope = minSlope;
        int bestIdx = -1;
        for (int k = 0; k < 8; k++)
        {
            var (di1, dj1) = Grid.N8[k];
            var (di2, dj2) = Grid.N8[(k + 1) % 8];
            int i1 = i + di1, j1 = j + dj1;
            int i2 = i + di2, j2 = j + dj2;
            if (!grid.HasData(i1, j1) || !grid.HasData(i2, j2)) continue;
            double z1 = grid.Z[grid.Index(i1, j1)];
            double z2 = grid.Z[grid.Index(i2, j2)];
            double dx1 = di1 * grid.CellSize, dy1 = dj1 * grid.CellSize;
            double dx2 = di2 * grid.CellSize, dy2 = dj2 * grid.CellSize;
            double dz1 = z1 - zc, dz2 = z2 - zc;
            double det = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(det) < 1e-9) continue;
            double gx = (dz1 * dy2 - dz2 * dy1) / det;
            double gy = (dx1 * dz2 - dx2 * dz1) / det;
            double slope = Math.Sqrt(gx * gx + gy * gy);
            if (slope <= bestSlope) continue;
            // Steepest descent direction = -gradient (in 2D)
            double angle = Math.Atan2(-gy, -gx);
            int nearestK = SnapAngleToD8(angle);
            var (ndi, ndj) = Grid.N8[nearestK];
            int ni = i + ndi, nj = j + ndj;
            if (!grid.HasData(ni, nj)) continue;
            if (grid.Z[grid.Index(ni, nj)] >= zc) continue; // must be downhill
            bestSlope = slope;
            bestIdx = grid.Index(ni, nj);
        }
        return bestIdx;
    }

    private static int SnapAngleToD8(double angle)
    {
        // D8 angles (radians): NE=0.785, E=0, SE=-0.785, S=-1.571, SW=-2.356, W=π, NW=2.356, N=1.571
        // Index follows Grid.N8: 0=NE,1=E,2=SE,3=S,4=SW,5=W,6=NW,7=N
        var d8Angles = new[] {
            Math.PI / 4, 0.0, -Math.PI / 4, -Math.PI / 2,
            -3 * Math.PI / 4, Math.PI, 3 * Math.PI / 4, Math.PI / 2,
        };
        int best = 0;
        double bestDiff = double.PositiveInfinity;
        for (int k = 0; k < 8; k++)
        {
            double d = Math.Abs(NormalizeAngle(angle - d8Angles[k]));
            if (d < bestDiff) { bestDiff = d; best = k; }
        }
        return best;
    }

    private static double NormalizeAngle(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }

    private static int ResolveTrace(int idx, int[] down, int[] labels, int[] trace, int inProgress)
    {
        var stack = new Stack<int>();
        int cur = idx;
        while (true)
        {
            if (cur < 0) { ApplyResult(stack, trace, 0); return 0; }
            if (trace[cur] != -1 && trace[cur] != inProgress)
            {
                int result = trace[cur];
                ApplyResult(stack, trace, result);
                return result;
            }
            if (trace[cur] == inProgress) { ApplyResult(stack, trace, 0); return 0; }
            trace[cur] = inProgress;
            stack.Push(cur);
            if (labels[cur] != 0) { ApplyResult(stack, trace, labels[cur]); return labels[cur]; }
            cur = down[cur];
        }
    }

    private static void ApplyResult(Stack<int> stack, int[] trace, int result)
    {
        while (stack.Count > 0) trace[stack.Pop()] = result;
    }
}
