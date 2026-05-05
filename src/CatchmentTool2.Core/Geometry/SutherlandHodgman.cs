namespace CatchmentTool2.Geometry;

/// <summary>
/// Sutherland-Hodgman convex-polygon clipping. Clips a subject polygon (any) against
/// a CCW convex clip polygon and returns the intersection.
/// </summary>
public static class SutherlandHodgman
{
    /// <param name="subject">Polygon to clip (CCW). No closing duplicate.</param>
    /// <param name="convexClip">Convex clip polygon (CCW). No closing duplicate.</param>
    public static List<Vec2> ClipConvex(IReadOnlyList<Vec2> subject, IReadOnlyList<Vec2> convexClip)
    {
        var output = subject.ToList();
        int cn = convexClip.Count;
        for (int i = 0; i < cn && output.Count > 0; i++)
        {
            var a = convexClip[i];
            var b = convexClip[(i + 1) % cn];
            var input = output;
            output = new List<Vec2>();
            for (int j = 0; j < input.Count; j++)
            {
                var p = input[j];
                var q = input[(j + 1) % input.Count];
                bool pIn = IsInside(p, a, b);
                bool qIn = IsInside(q, a, b);
                if (pIn)
                {
                    output.Add(p);
                    if (!qIn) output.Add(Intersect(p, q, a, b));
                }
                else if (qIn)
                {
                    output.Add(Intersect(p, q, a, b));
                }
            }
        }
        return output;
    }

    private static bool IsInside(Vec2 p, Vec2 a, Vec2 b)
    {
        // For CCW clip polygon, "inside" is left of edge a→b (cross > 0). Use ≥ for boundary tolerance.
        return Vec2.Cross(b - a, p - a) >= -1e-9;
    }

    private static Vec2 Intersect(Vec2 p, Vec2 q, Vec2 a, Vec2 b)
    {
        var r = q - p;
        var s = b - a;
        double denom = Vec2.Cross(r, s);
        if (Math.Abs(denom) < 1e-12) return p;
        double t = Vec2.Cross(a - p, s) / denom;
        return new Vec2(p.X + t * r.X, p.Y + t * r.Y);
    }
}
