using CatchmentTool2.Geometry;
using CatchmentTool2.Network;
using CatchmentTool2.Surface;

namespace CatchmentTool2.Pipeline;

public static class Phase1_Boundary
{
    public static List<Vec2> DeriveBoundary(Tin tin, IEnumerable<Structure> structures,
        TuningParameters p, IReadOnlyList<Vec2>? userPolygon)
    {
        var tinHull = ConvexHull.Compute(tin.Vertices.Select(v => v.XY));
        List<Vec2> raw = p.BoundarySource switch
        {
            BoundarySource.UserPolygon when userPolygon != null && userPolygon.Count >= 3
                => userPolygon.ToList(),
            BoundarySource.TinHull => tinHull,
            _ => BuildStructureBufferHull(tin, structures, p),
        };
        // Always clip against the TIN convex hull. Cells outside the TIN have no Z and only
        // generate spurious "gap" — clipping makes scoring fair and the boundary meaningful.
        if (tinHull.Count >= 3)
        {
            var clipped = SutherlandHodgman.ClipConvex(raw, tinHull);
            if (clipped.Count >= 3) return clipped;
        }
        return raw;
    }

    private static List<Vec2> BuildStructureBufferHull(Tin tin, IEnumerable<Structure> structures, TuningParameters p)
    {
        var pts = structures.Select(s => s.Location).ToList();
        if (pts.Count < 3) pts.AddRange(tin.Vertices.Select(v => v.XY));
        var hull = ConvexHull.Compute(pts);
        return ConvexHull.Buffer(hull, p.StructureBufferDistance);
    }
}
