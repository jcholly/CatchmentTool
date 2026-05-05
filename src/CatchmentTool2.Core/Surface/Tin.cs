namespace CatchmentTool2.Surface;

public readonly record struct TinVertex(double X, double Y, double Z)
{
    public Vec2 XY => new(X, Y);
}

public readonly record struct TinTriangle(int A, int B, int C);

/// <summary>
/// Read-only TIN with axis-aligned bin index for O(1) point-in-triangle lookup.
/// </summary>
public sealed class Tin
{
    public IReadOnlyList<TinVertex> Vertices { get; }
    public IReadOnlyList<TinTriangle> Triangles { get; }
    public Bounds Bounds { get; }

    private readonly List<int>[,] _bins;
    private readonly int _binsX;
    private readonly int _binsY;
    private readonly double _binSize;

    public Tin(IReadOnlyList<TinVertex> vertices, IReadOnlyList<TinTriangle> triangles)
    {
        Vertices = vertices;
        Triangles = triangles;
        if (vertices.Count == 0)
        {
            Bounds = new Bounds(0, 0, 0, 0);
            _bins = new List<int>[1, 1];
            _bins[0, 0] = new List<int>();
            _binsX = _binsY = 1;
            _binSize = 1;
            return;
        }
        Bounds = Bounds.Of(vertices.Select(v => v.XY));
        // Bin into ~sqrt(N) cells per axis
        var n = Math.Max(1, (int)Math.Sqrt(triangles.Count));
        _binsX = n;
        _binsY = n;
        _binSize = Math.Max(Bounds.Width, Bounds.Height) / n;
        if (_binSize <= 0) _binSize = 1;
        _bins = new List<int>[_binsX, _binsY];
        for (int i = 0; i < _binsX; i++)
            for (int j = 0; j < _binsY; j++)
                _bins[i, j] = new List<int>();
        for (int t = 0; t < triangles.Count; t++)
        {
            var tri = triangles[t];
            var a = vertices[tri.A]; var b = vertices[tri.B]; var c = vertices[tri.C];
            double tminX = Math.Min(a.X, Math.Min(b.X, c.X));
            double tmaxX = Math.Max(a.X, Math.Max(b.X, c.X));
            double tminY = Math.Min(a.Y, Math.Min(b.Y, c.Y));
            double tmaxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            int i0 = Math.Clamp((int)((tminX - Bounds.MinX) / _binSize), 0, _binsX - 1);
            int i1 = Math.Clamp((int)((tmaxX - Bounds.MinX) / _binSize), 0, _binsX - 1);
            int j0 = Math.Clamp((int)((tminY - Bounds.MinY) / _binSize), 0, _binsY - 1);
            int j1 = Math.Clamp((int)((tmaxY - Bounds.MinY) / _binSize), 0, _binsY - 1);
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                    _bins[i, j].Add(t);
        }
    }

    /// <summary>
    /// Returns true and writes z if (x,y) is inside any triangle, otherwise false.
    /// </summary>
    public bool TryGetElevation(double x, double y, out double z)
    {
        z = double.NaN;
        if (!Bounds.Contains(new Vec2(x, y))) return false;
        int i = Math.Clamp((int)((x - Bounds.MinX) / _binSize), 0, _binsX - 1);
        int j = Math.Clamp((int)((y - Bounds.MinY) / _binSize), 0, _binsY - 1);
        foreach (var t in _bins[i, j])
        {
            var tri = Triangles[t];
            var a = Vertices[tri.A]; var b = Vertices[tri.B]; var c = Vertices[tri.C];
            if (Barycentric(x, y, a, b, c, out double wa, out double wb, out double wc))
            {
                z = wa * a.Z + wb * b.Z + wc * c.Z;
                return true;
            }
        }
        return false;
    }

    public static bool Barycentric(double px, double py, TinVertex a, TinVertex b, TinVertex c,
        out double wa, out double wb, out double wc)
    {
        double v0x = b.X - a.X, v0y = b.Y - a.Y;
        double v1x = c.X - a.X, v1y = c.Y - a.Y;
        double v2x = px - a.X, v2y = py - a.Y;
        double d00 = v0x * v0x + v0y * v0y;
        double d01 = v0x * v1x + v0y * v1y;
        double d11 = v1x * v1x + v1y * v1y;
        double d20 = v2x * v0x + v2y * v0y;
        double d21 = v2x * v1x + v2y * v1y;
        double denom = d00 * d11 - d01 * d01;
        if (Math.Abs(denom) < 1e-18) { wa = wb = wc = 0; return false; }
        wb = (d11 * d20 - d01 * d21) / denom;
        wc = (d00 * d21 - d01 * d20) / denom;
        wa = 1.0 - wb - wc;
        const double eps = -1e-9;
        return wa >= eps && wb >= eps && wc >= eps;
    }
}
