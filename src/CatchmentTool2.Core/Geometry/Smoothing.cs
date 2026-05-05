namespace CatchmentTool2.Geometry;

public static class Smoothing
{
    /// <summary>Ramer-Douglas-Peucker on a closed ring (no closing duplicate).</summary>
    public static List<Vec2> RdpClosed(IReadOnlyList<Vec2> ring, double tol)
    {
        if (ring.Count < 4 || tol <= 0) return ring.ToList();
        // Open by duplicating first vertex at end, run RDP on segments,
        // then join. Simpler: pick the two farthest-apart vertices and split.
        int n = ring.Count;
        int a = 0, b = 1;
        double bestD2 = -1;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                var d = ring[i] - ring[j];
                double d2 = d.X * d.X + d.Y * d.Y;
                if (d2 > bestD2) { bestD2 = d2; a = i; b = j; }
            }
        }
        var arc1 = new List<Vec2>();
        for (int k = a; ; k = (k + 1) % n) { arc1.Add(ring[k]); if (k == b) break; }
        var arc2 = new List<Vec2>();
        for (int k = b; ; k = (k + 1) % n) { arc2.Add(ring[k]); if (k == a) break; }
        var s1 = RdpOpen(arc1, tol);
        var s2 = RdpOpen(arc2, tol);
        var result = new List<Vec2>(s1.Count + s2.Count - 2);
        result.AddRange(s1.Take(s1.Count - 1));
        result.AddRange(s2.Take(s2.Count - 1));
        return result;
    }

    private static List<Vec2> RdpOpen(List<Vec2> pts, double tol)
    {
        if (pts.Count < 3) return new List<Vec2>(pts);
        var keep = new bool[pts.Count];
        keep[0] = true; keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (lo, hi) = stack.Pop();
            double maxD = 0; int idx = -1;
            for (int i = lo + 1; i < hi; i++)
            {
                double d = PerpendicularDistance(pts[i], pts[lo], pts[hi]);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (maxD > tol && idx > 0)
            {
                keep[idx] = true;
                stack.Push((lo, idx));
                stack.Push((idx, hi));
            }
        }
        var result = new List<Vec2>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
        return result;
    }

    private static double PerpendicularDistance(Vec2 p, Vec2 a, Vec2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-12) return p.DistanceTo(a);
        return Math.Abs(dy * p.X - dx * p.Y + b.X * a.Y - b.Y * a.X) / len;
    }

    /// <summary>Chaikin corner-cutting on a closed ring. Each iteration ~4x the vertex count.</summary>
    public static List<Vec2> ChaikinClosed(IReadOnlyList<Vec2> ring, int iterations)
    {
        var current = ring.ToList();
        for (int k = 0; k < iterations; k++)
        {
            var next = new List<Vec2>(current.Count * 2);
            int n = current.Count;
            for (int i = 0; i < n; i++)
            {
                var a = current[i];
                var b = current[(i + 1) % n];
                next.Add(new Vec2(0.75 * a.X + 0.25 * b.X, 0.75 * a.Y + 0.25 * b.Y));
                next.Add(new Vec2(0.25 * a.X + 0.75 * b.X, 0.25 * a.Y + 0.75 * b.Y));
            }
            current = next;
        }
        return current;
    }

    /// <summary>Run RDP then Chaikin, rolling back if area drift exceeds maxDriftPct%.</summary>
    public static List<Vec2> SmoothWithGuard(IReadOnlyList<Vec2> ring, double rdpTol,
        int chaikinIterations, double maxDriftPct)
    {
        var original = new Polygon(ring);
        var rdp = RdpClosed(ring, rdpTol);
        if (rdp.Count < 4) rdp = ring.ToList();
        for (int it = chaikinIterations; it >= 0; it--)
        {
            var smoothed = it > 0 ? ChaikinClosed(rdp, it) : rdp;
            var pgon = new Polygon(smoothed);
            double drift = original.Area > 0
                ? Math.Abs(pgon.Area - original.Area) / original.Area * 100.0
                : 0;
            if (drift <= maxDriftPct) return smoothed;
        }
        return rdp;
    }
}
