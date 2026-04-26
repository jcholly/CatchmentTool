using CatchmentTool2.Network;
using CatchmentTool2.Surface;

namespace CatchmentTool2.Pipeline;

/// <summary>
/// "Pipe burning" — lower the rasterized surface along each pipe alignment by a fixed
/// depth before depression handling. Standard MS4 GIS practice (Saunders 1999, Lindsay 2016
/// hydro-conditioning). Forces overland flow to be captured by the inlet upstream of any
/// crossing, sidestepping D8's inability to model pipe capture.
///
/// Implementation: Bresenham line trace per pipe, deepening each crossed cell by burnDepth.
/// Operates on the scratch raster only — never touches the source TIN.
/// </summary>
public static class Phase2b_PipeBurn
{
    public static void Burn(Grid grid, PipeNetwork? network,
        IReadOnlyList<Structure> structures, double burnDepth)
    {
        if (network == null || burnDepth <= 0) return;
        var byId = structures.ToDictionary(s => s.Id);
        foreach (var pipe in network.Pipes)
        {
            if (!byId.TryGetValue(pipe.StartStructureId, out var a)) continue;
            if (!byId.TryGetValue(pipe.EndStructureId, out var b)) continue;
            BurnLine(grid, a.Location, b.Location, burnDepth);
        }
    }

    private static void BurnLine(Grid g, Vec2 a, Vec2 b, double depth)
    {
        var (i0, j0) = g.CellAt(a.X, a.Y);
        var (i1, j1) = g.CellAt(b.X, b.Y);
        int dx = Math.Abs(i1 - i0), dy = Math.Abs(j1 - j0);
        int sx = i0 < i1 ? 1 : -1, sy = j0 < j1 ? 1 : -1;
        int err = dx - dy;
        int safety = (dx + dy) * 2 + 16;
        while (safety-- > 0)
        {
            if (g.HasData(i0, j0))
                g.Z[g.Index(i0, j0)] -= depth;
            if (i0 == i1 && j0 == j1) break;
            int e2 = err * 2;
            if (e2 > -dy) { err -= dy; i0 += sx; }
            if (e2 < dx) { err += dx; j0 += sy; }
        }
    }
}
