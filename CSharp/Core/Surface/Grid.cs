namespace CatchmentTool2.Surface;

/// <summary>
/// Square-cell raster aligned to (originX, originY). Cell (i,j) covers
/// [originX + i*cs, originX + (i+1)*cs) × [originY + j*cs, originY + (j+1)*cs).
/// Z is NaN for cells outside the TIN.
/// </summary>
public sealed class Grid
{
    public int Cols { get; }
    public int Rows { get; }
    public double CellSize { get; }
    public double OriginX { get; }
    public double OriginY { get; }
    public double[] Z { get; }

    public Grid(int cols, int rows, double cellSize, double originX, double originY)
    {
        Cols = cols; Rows = rows; CellSize = cellSize;
        OriginX = originX; OriginY = originY;
        Z = new double[cols * rows];
        Array.Fill(Z, double.NaN);
    }

    public int Index(int i, int j) => j * Cols + i;
    public bool InBounds(int i, int j) => i >= 0 && i < Cols && j >= 0 && j < Rows;
    public double CellArea => CellSize * CellSize;

    public Vec2 CellCenter(int i, int j) =>
        new(OriginX + (i + 0.5) * CellSize, OriginY + (j + 0.5) * CellSize);

    public (int i, int j) CellAt(double x, double y) =>
        ((int)Math.Floor((x - OriginX) / CellSize), (int)Math.Floor((y - OriginY) / CellSize));

    public bool HasData(int i, int j) => InBounds(i, j) && !double.IsNaN(Z[Index(i, j)]);

    // 8-neighbor offsets, ordered NE,E,SE,S,SW,W,NW,N (consistent encoding for D8)
    public static readonly (int di, int dj)[] N8 = {
        ( 1,  1), ( 1,  0), ( 1, -1), ( 0, -1),
        (-1, -1), (-1,  0), (-1,  1), ( 0,  1)
    };

    public static double NeighborDistance(int di, int dj) =>
        (di == 0 || dj == 0) ? 1.0 : Math.Sqrt(2);
}
