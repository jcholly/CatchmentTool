namespace CatchmentTool2.Geometry;

public sealed record Polygon(IReadOnlyList<Vec2> Vertices)
{
    public double SignedArea
    {
        get
        {
            double s = 0;
            for (int i = 0, n = Vertices.Count; i < n; i++)
            {
                var a = Vertices[i];
                var b = Vertices[(i + 1) % n];
                s += a.X * b.Y - b.X * a.Y;
            }
            return s * 0.5;
        }
    }
    public double Area => Math.Abs(SignedArea);

    public double Perimeter
    {
        get
        {
            double p = 0;
            for (int i = 0, n = Vertices.Count; i < n; i++)
                p += Vertices[i].DistanceTo(Vertices[(i + 1) % n]);
            return p;
        }
    }

    /// <summary>Polsby-Popper / thinness ratio. 1.0 = circle, &lt; 0.05 = sliver.</summary>
    public double ThinnessRatio
    {
        get
        {
            var p = Perimeter;
            return p <= 0 ? 0 : 4.0 * Math.PI * Area / (p * p);
        }
    }

    public bool Contains(Vec2 p)
    {
        bool inside = false;
        for (int i = 0, j = Vertices.Count - 1; i < Vertices.Count; j = i++)
        {
            var a = Vertices[i]; var b = Vertices[j];
            if (((a.Y > p.Y) != (b.Y > p.Y)) &&
                (p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X))
                inside = !inside;
        }
        return inside;
    }

    public Bounds Bounds => CatchmentTool2.Bounds.Of(Vertices);

    /// <summary>True if any segment of this polygon crosses any segment of the other.</summary>
    public bool IntersectsBoundary(Polygon other)
    {
        for (int i = 0, n = Vertices.Count; i < n; i++)
        {
            var a1 = Vertices[i]; var a2 = Vertices[(i + 1) % n];
            for (int j = 0, m = other.Vertices.Count; j < m; j++)
            {
                var b1 = other.Vertices[j]; var b2 = other.Vertices[(j + 1) % m];
                if (SegmentsIntersect(a1, a2, b1, b2)) return true;
            }
        }
        return false;
    }

    private static bool SegmentsIntersect(Vec2 p, Vec2 p2, Vec2 q, Vec2 q2)
    {
        double d1 = Vec2.Cross(p2 - p, q - p);
        double d2 = Vec2.Cross(p2 - p, q2 - p);
        double d3 = Vec2.Cross(q2 - q, p - q);
        double d4 = Vec2.Cross(q2 - q, p2 - q);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}

public static class ConvexHull
{
    /// <summary>Andrew's monotone chain. Returns CCW hull, no closing duplicate.</summary>
    public static List<Vec2> Compute(IEnumerable<Vec2> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count <= 2) return pts;
        var lower = new List<Vec2>();
        foreach (var p in pts)
        {
            while (lower.Count >= 2 && Vec2.Cross(lower[^1] - lower[^2], p - lower[^2]) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }
        var upper = new List<Vec2>();
        for (int i = pts.Count - 1; i >= 0; i--)
        {
            var p = pts[i];
            while (upper.Count >= 2 && Vec2.Cross(upper[^1] - upper[^2], p - upper[^2]) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    /// <summary>Buffer a closed CCW ring by d (positive = outward).</summary>
    public static List<Vec2> Buffer(IReadOnlyList<Vec2> ring, double d)
    {
        var result = new List<Vec2>(ring.Count);
        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            var prev = ring[(i - 1 + n) % n];
            var cur = ring[i];
            var next = ring[(i + 1) % n];
            var n1 = OutwardNormal(prev, cur);
            var n2 = OutwardNormal(cur, next);
            var avg = new Vec2((n1.X + n2.X) / 2, (n1.Y + n2.Y) / 2);
            var len = avg.Length;
            if (len < 1e-12) { result.Add(cur); continue; }
            var unit = new Vec2(avg.X / len, avg.Y / len);
            result.Add(new Vec2(cur.X + unit.X * d, cur.Y + unit.Y * d));
        }
        return result;
    }

    private static Vec2 OutwardNormal(Vec2 a, Vec2 b)
    {
        var d = b - a;
        var len = d.Length;
        if (len < 1e-12) return new Vec2(0, 0);
        // CCW polygon: outward normal is right of edge direction
        return new Vec2(d.Y / len, -d.X / len);
    }
}
