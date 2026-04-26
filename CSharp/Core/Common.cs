namespace CatchmentTool2;

public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double DistanceTo(Vec2 o) => (this - o).Length;
    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
}

public readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double Area => Width * Height;
    public Vec2 Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2);
    public bool Contains(Vec2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
    public Bounds Expand(double d) => new(MinX - d, MinY - d, MaxX + d, MaxY + d);

    public static Bounds Of(IEnumerable<Vec2> pts)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new Bounds(minX, minY, maxX, maxY);
    }
}
