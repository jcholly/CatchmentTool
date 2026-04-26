using CatchmentTool2.Surface;

namespace CatchmentTool2.Pipeline;

public static class Phase2_Rasterize
{
    public static Grid Rasterize(Tin tin, IReadOnlyList<Vec2> boundary, double cellSize)
    {
        var b = Bounds.Of(boundary);
        int cols = Math.Max(1, (int)Math.Ceiling(b.Width / cellSize));
        int rows = Math.Max(1, (int)Math.Ceiling(b.Height / cellSize));
        var grid = new Grid(cols, rows, cellSize, b.MinX, b.MinY);
        var poly = new Geometry.Polygon(boundary);
        for (int j = 0; j < rows; j++)
        {
            for (int i = 0; i < cols; i++)
            {
                var c = grid.CellCenter(i, j);
                if (!poly.Contains(c)) continue;
                if (tin.TryGetElevation(c.X, c.Y, out double z))
                    grid.Z[grid.Index(i, j)] = z;
            }
        }
        return grid;
    }
}
