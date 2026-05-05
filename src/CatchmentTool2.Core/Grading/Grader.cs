using CatchmentTool2.Geometry;
using CatchmentTool2.Network;
using CatchmentTool2.Pipeline;
using CatchmentTool2.Surface;

namespace CatchmentTool2.Grading;

public sealed record GradeResult(
    // Tier 1
    double OutletCoverageScore,         // % of selected outlets with a valid catchment (target 100)
    bool NativeCatchmentObjects,        // (always true here; binary)
    double GapFraction,                 // (boundary − union) / boundary; lower is better, target < 0.05
    double OverlapFraction,             // pairwise overlap / total catchment area; target < 0.02
    double FlowPathCorrectness,         // % sample points whose steepest descent terminates in correct catchment
    // Tier 2
    int SliverCount,                    // catchments with thinness < 0.05
    int MicroPolygonCount,              // catchments < 100 ft²
    double SmoothnessScore,             // mean turning angle metric (0..1, higher is smoother)
    double SumpContainmentScore,        // % of pond-catchment samples whose flow stays inside polygon
    // Composite v2 components
    double OutletCountMatch,
    double OutletCentroidScore,
    double AreaConservationScore,
    double FlowLengthSanityScore,
    double VertexDensityScore,
    // Tier 3
    double RuntimeSeconds,
    // Aggregate
    double WeightedScore                // composite; higher is better
);

public static class Grader
{
    public static GradeResult Grade(PipelineResult result, PipelineInput input,
        TuningParameters p, int flowSamplesPerCatchment = 25)
    {
        // Tier 1.1 — outlet coverage
        var selected = input.Structures.Where(s => s.UserSelected).ToList();
        var catchedIds = result.Catchments.Select(c => c.StructureId).ToHashSet();
        int validCatchments = 0;
        foreach (var s in selected)
        {
            var match = result.Catchments.FirstOrDefault(c => c.StructureId == s.Id);
            if (match != null && match.Geometry.Contains(s.Location)) validCatchments++;
        }
        double outletScore = selected.Count == 0 ? 100 : 100.0 * validCatchments / selected.Count;

        // Tier 1.3 — gap fraction
        var boundaryArea = new Polygon(result.Boundary).Area;
        double catchedArea = result.Catchments.Sum(c => c.Geometry.Area);
        double gap = Math.Max(0, (boundaryArea - catchedArea)) / Math.Max(1e-9, boundaryArea);

        // Tier 1.4 — overlaps (cheap proxy: compare sum-of-areas to bbox-merged area)
        // Better: rasterized overlap. Simple proxy via cell-grid: count cells with >1 polygon
        double overlap = ComputeOverlapFraction(result);

        // Tier 1.5 — flow-path correctness (Monte Carlo)
        double flowPath = ComputeFlowPathCorrectness(result, input, flowSamplesPerCatchment);

        // Tier 2.6 — slivers (still computed for reporting; weight 0 in composite v2)
        int slivers = result.Catchments.Count(c => c.Geometry.ThinnessRatio < 0.05);
        // Tier 2.7 — micro polygons (reporting only)
        int micro = result.Catchments.Count(c => c.Geometry.Area < 100);
        // Tier 2.8 — smoothness (reporting only)
        double smoothness = ComputeSmoothness(result.Catchments);
        // Tier 2.9 — sump containment
        double sumpContainment = ComputeSumpContainment(result, input, samplesPerPond: 25);

        // Composite v2 components
        // (a) outlet count match: how close is the catchment count to the selected count?
        double countMatch = selected.Count == 0 ? 100
            : 100.0 * Math.Min(result.Catchments.Count, selected.Count) / Math.Max(1, Math.Max(result.Catchments.Count, selected.Count));
        // (b) outlet centroid score: how close is each outlet to its catchment's centroid?
        double centroidScore = ComputeCentroidScore(result, selected);
        double outletCombined = 0.5 * countMatch + 0.5 * centroidScore;
        // Linear gap ramp: 100 at gap ≤ 2%, 0 at gap ≥ 10%
        double gapScore = 100 * Math.Clamp((0.10 - gap) / (0.10 - 0.02), 0, 1);
        // Area conservation: |sumCatch - boundary| / boundary, full credit at ≤2%, 0 at ≥12%
        double sumCatch = result.Catchments.Sum(c => c.Geometry.Area);
        double areaImbalance = boundaryArea <= 0 ? 0 : Math.Abs(sumCatch - boundaryArea) / boundaryArea;
        double areaConservation = 100 * Math.Clamp((0.12 - areaImbalance) / (0.12 - 0.02), 0, 1);
        // Flow length sanity: catchment elongation (bounding box diagonal / sqrt(area)).
        // Compact: 1.5–4. Elongated: 4–10. Weird: >10. Score 100 below 5, 0 above 12, linear.
        double flowLengthSanity = ComputeFlowLengthSanity(result.Catchments);
        // Vertex density: ideal 3–8 vertices per 100 ft of perimeter. Score 100 in band, drops outside.
        double vertexDensity = ComputeVertexDensityScore(result.Catchments);
        // Linear overlap ramp: 100 at 0%, 0 at 3%
        double overlapScore = 100 * Math.Clamp((0.03 - overlap) / 0.03, 0, 1);

        // Composite v2 weights (slivers/micros/smoothness deliberately zero — see CLAUDE.md research notes)
        double w = 0.36 * flowPath               // flow correctness — most engineering-meaningful
                 + 0.23 * gapScore               // coverage (linear ramp)
                 + 0.17 * outletCombined         // outlet count + centroid
                 + 0.07 * sumpContainment        // sump containment
                 + 0.05 * overlapScore           // overlap (linear ramp)
                 + 0.06 * areaConservation       // total area balance
                 + 0.03 * flowLengthSanity       // shape-elongation sanity
                 + 0.03 * vertexDensity;         // engineer-style vertex density
        // sum = 1.00
        w = Math.Clamp(w, 0, 100);

        return new GradeResult(
            OutletCoverageScore: outletScore,
            NativeCatchmentObjects: true,
            GapFraction: gap,
            OverlapFraction: overlap,
            FlowPathCorrectness: flowPath,
            SliverCount: slivers,
            MicroPolygonCount: micro,
            SmoothnessScore: smoothness,
            SumpContainmentScore: sumpContainment,
            OutletCountMatch: countMatch,
            OutletCentroidScore: centroidScore,
            AreaConservationScore: areaConservation,
            FlowLengthSanityScore: flowLengthSanity,
            VertexDensityScore: vertexDensity,
            RuntimeSeconds: result.RuntimeSeconds,
            WeightedScore: w);
    }

    private static double ComputeCentroidScore(PipelineResult result, IReadOnlyList<Network.Structure> selected)
    {
        if (result.Catchments.Count == 0) return 0;
        var byId = result.Catchments.ToDictionary(c => c.StructureId);
        double sum = 0;
        int n = 0;
        foreach (var s in selected)
        {
            if (!byId.TryGetValue(s.Id, out var c)) continue;
            var poly = c.Geometry;
            var centroid = PolygonCentroid(poly.Vertices);
            double dist = s.Location.DistanceTo(centroid);
            // Use sqrt(area * 4/π) as the polygon's "equivalent disk diameter"
            double diameter = Math.Sqrt(Math.Max(1, poly.Area) * 4.0 / Math.PI);
            double frac = Math.Min(1.0, dist / Math.Max(1e-6, diameter));
            sum += 100.0 * (1.0 - frac);
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    private static Vec2 PolygonCentroid(IReadOnlyList<Vec2> ring)
    {
        double a = 0, cx = 0, cy = 0;
        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % n];
            double cross = p.X * q.Y - q.X * p.Y;
            a += cross;
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
        }
        a *= 0.5;
        if (Math.Abs(a) < 1e-12) return ring.Count > 0 ? ring[0] : new Vec2(0, 0);
        return new Vec2(cx / (6 * a), cy / (6 * a));
    }

    private static double ComputeFlowLengthSanity(IReadOnlyList<Pipeline.CatchmentPolygon> catchments)
    {
        if (catchments.Count == 0) return 100;
        double sum = 0;
        foreach (var c in catchments)
        {
            var b = c.Geometry.Bounds;
            double diag = Math.Sqrt(b.Width * b.Width + b.Height * b.Height);
            double normalized = diag / Math.Sqrt(Math.Max(1, c.Geometry.Area));
            // Score 100 below 5.0, 0 above 12.0, linear in between
            sum += 100 * Math.Clamp((12.0 - normalized) / (12.0 - 5.0), 0, 1);
        }
        return sum / catchments.Count;
    }

    private static double ComputeVertexDensityScore(IReadOnlyList<Pipeline.CatchmentPolygon> catchments)
    {
        if (catchments.Count == 0) return 100;
        double sum = 0;
        foreach (var c in catchments)
        {
            var perim = c.Geometry.Perimeter;
            if (perim <= 0) { sum += 0; continue; }
            double densityPer100 = c.Geometry.Vertices.Count * 100.0 / perim;
            // Sweet spot: 3-8 per 100 ft. Score:
            //   <1 or >20 = 0
            //   3-8 = 100
            //   linear in between
            double score;
            if (densityPer100 >= 3 && densityPer100 <= 8) score = 100;
            else if (densityPer100 < 3) score = 100 * Math.Clamp((densityPer100 - 1) / (3 - 1), 0, 1);
            else score = 100 * Math.Clamp((20 - densityPer100) / (20 - 8), 0, 1);
            sum += score;
        }
        return sum / catchments.Count;
    }

    /// <summary>
    /// Tier 2 #9 — for each user-selected pond catchment, sample points inside the polygon
    /// and run D8 forward. Score = % of samples whose flow stays *inside* the polygon
    /// until termination (or reaches the pond cell). Catches "leaky" catchments where the
    /// boundary doesn't actually contain the drainage area.
    /// </summary>
    private static double ComputeSumpContainment(PipelineResult result, PipelineInput input, int samplesPerPond)
    {
        var ponds = input.Structures
            .Where(s => s.Kind == Network.StructureKind.Pond && s.UserSelected)
            .ToList();
        if (ponds.Count == 0) return 100;
        var rng = new Random(123);
        var grid = result.Grid;
        double scoreSum = 0;
        int pondCount = 0;
        foreach (var pond in ponds)
        {
            var catchment = result.Catchments.FirstOrDefault(c => c.StructureId == pond.Id);
            if (catchment == null) continue;
            var poly = catchment.Geometry;
            var bbox = poly.Bounds;
            int sampled = 0, leaks = 0, tries = 0;
            while (sampled < samplesPerPond && tries < samplesPerPond * 20)
            {
                tries++;
                double x = bbox.MinX + rng.NextDouble() * bbox.Width;
                double y = bbox.MinY + rng.NextDouble() * bbox.Height;
                var pt = new Vec2(x, y);
                if (!poly.Contains(pt)) continue;
                var (i, j) = grid.CellAt(x, y);
                if (!grid.HasData(i, j)) continue;
                sampled++;
                if (TraceLeaksOut(grid, i, j, poly, pond.Location)) leaks++;
            }
            if (sampled > 0)
            {
                scoreSum += 100.0 * (1.0 - (double)leaks / sampled);
                pondCount++;
            }
        }
        return pondCount == 0 ? 100 : scoreSum / pondCount;
    }

    private static bool TraceLeaksOut(Surface.Grid g, int i, int j, Geometry.Polygon poly, Vec2 pondLoc)
    {
        int safety = Math.Min(g.Cols * g.Rows, 5000);
        while (safety-- > 0)
        {
            var c = g.CellCenter(i, j);
            if (!poly.Contains(c)) return true; // leaked outside
            if (c.DistanceTo(pondLoc) < g.CellSize * 1.5) return false; // reached pond
            double z = g.Z[g.Index(i, j)];
            int bestI = -1, bestJ = -1; double bestSlope = 0;
            foreach (var (di, dj) in Surface.Grid.N8)
            {
                int i2 = i + di, j2 = j + dj;
                if (!g.HasData(i2, j2)) continue;
                double dz = z - g.Z[g.Index(i2, j2)];
                if (dz <= 0) continue;
                double slope = dz / Surface.Grid.NeighborDistance(di, dj);
                if (slope > bestSlope) { bestSlope = slope; bestI = i2; bestJ = j2; }
            }
            if (bestI < 0) return false; // dead-end inside polygon — not a leak
            i = bestI; j = bestJ;
        }
        return false;
    }

    private static double ComputeOverlapFraction(PipelineResult result)
    {
        // Sample-based: lay a coarse test grid over the bbox; count cells in >1 polygon.
        if (result.Catchments.Count == 0) return 0;
        var b = Bounds.Of(result.Boundary);
        int n = 60;
        double cs = Math.Max(b.Width, b.Height) / n;
        if (cs <= 0) return 0;
        int total = 0, multi = 0;
        for (double y = b.MinY + cs / 2; y < b.MaxY; y += cs)
            for (double x = b.MinX + cs / 2; x < b.MaxX; x += cs)
            {
                int hits = 0;
                var pt = new Vec2(x, y);
                foreach (var c in result.Catchments)
                {
                    if (c.Geometry.Contains(pt)) { hits++; if (hits > 1) break; }
                }
                if (hits >= 1) total++;
                if (hits > 1) multi++;
            }
        return total == 0 ? 0 : (double)multi / total;
    }

    private static double ComputeFlowPathCorrectness(PipelineResult result, PipelineInput input,
        int samplesPerCatchment)
    {
        if (result.Catchments.Count == 0) return 100;
        var rng = new Random(42);
        int correct = 0, total = 0;
        var grid = result.Grid;
        var structById = input.Structures.ToDictionary(s => s.Id);
        var selected = input.Structures.Where(s => s.UserSelected).Select(s => s.Id).ToHashSet();
        // Only sample where the TIN actually has data — otherwise we conflate algorithm error
        // with TIN incompleteness. Use the TIN convex hull as the validity gate.
        var tinHull = Geometry.ConvexHull.Compute(input.Tin.Vertices.Select(v => v.XY));
        var tinHullPoly = tinHull.Count >= 3 ? new Geometry.Polygon(tinHull) : null;
        foreach (var catchment in result.Catchments)
        {
            var bbox = catchment.Geometry.Bounds;
            int tries = 0; int sampled = 0;
            while (sampled < samplesPerCatchment && tries < samplesPerCatchment * 20)
            {
                tries++;
                double x = bbox.MinX + rng.NextDouble() * bbox.Width;
                double y = bbox.MinY + rng.NextDouble() * bbox.Height;
                var pt = new Vec2(x, y);
                if (!catchment.Geometry.Contains(pt)) continue;
                if (tinHullPoly != null && !tinHullPoly.Contains(pt)) continue;
                var (i, j) = grid.CellAt(x, y);
                if (!grid.HasData(i, j)) continue;
                sampled++;
                string? hit = TraceD8(grid, i, j, structById);
                if (hit == null) { total++; continue; }
                string? terminus = WalkToSelected(hit, input.Network, selected);
                total++;
                if (terminus == catchment.StructureId) correct++;
            }
        }
        return total == 0 ? 100 : 100.0 * correct / total;
    }

    private static string? WalkToSelected(string startId, Network.PipeNetwork? net, HashSet<string> selected)
    {
        if (selected.Contains(startId)) return startId;
        if (net == null) return null;
        var seen = new HashSet<string>();
        string cur = startId;
        while (seen.Add(cur))
        {
            var down = net.Downstream(cur);
            if (down.Count == 0) return cur; // terminal sink
            cur = down[0];
            if (selected.Contains(cur)) return cur;
        }
        return null;
    }

    private static string? TraceD8(Grid g, int i, int j, Dictionary<string, Network.Structure> structById)
    {
        // Trace down following steepest descent on the conditioned grid.
        int safety = g.Cols * g.Rows;
        int curI = i, curJ = j;
        while (safety-- > 0)
        {
            // If current cell coincides with a structure, return its id.
            foreach (var s in structById.Values)
            {
                var (si, sj) = g.CellAt(s.Location.X, s.Location.Y);
                if (si == curI && sj == curJ) return s.Id;
            }
            double z = g.Z[g.Index(curI, curJ)];
            int bestI = -1, bestJ = -1; double bestSlope = 0;
            foreach (var (di, dj) in Grid.N8)
            {
                int i2 = curI + di, j2 = curJ + dj;
                if (!g.HasData(i2, j2)) continue;
                double dz = z - g.Z[g.Index(i2, j2)];
                if (dz <= 0) continue;
                double slope = dz / Grid.NeighborDistance(di, dj);
                if (slope > bestSlope) { bestSlope = slope; bestI = i2; bestJ = j2; }
            }
            if (bestI < 0) return null;
            curI = bestI; curJ = bestJ;
        }
        return null;
    }

    private static double ComputeSmoothness(IReadOnlyList<CatchmentPolygon> catchments)
    {
        if (catchments.Count == 0) return 1.0;
        double sum = 0;
        int count = 0;
        foreach (var c in catchments)
        {
            var v = c.Geometry.Vertices;
            int n = v.Count;
            if (n < 3) continue;
            for (int i = 0; i < n; i++)
            {
                var a = v[(i - 1 + n) % n];
                var b = v[i];
                var d = v[(i + 1) % n];
                var u1 = b - a; var u2 = d - b;
                double l1 = u1.Length, l2 = u2.Length;
                if (l1 < 1e-9 || l2 < 1e-9) continue;
                double cos = (u1.X * u2.X + u1.Y * u2.Y) / (l1 * l2);
                cos = Math.Clamp(cos, -1, 1);
                // Reward small turn angles (cos near 1)
                sum += (cos + 1) / 2; // map [-1,1] -> [0,1]
                count++;
            }
        }
        return count == 0 ? 1.0 : sum / count;
    }
}
