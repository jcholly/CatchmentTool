// Standalone harness: runs CatchmentTool.Services.PipeBurner on a CSV-provided
// elevation grid + pipe list so the Python reference can diff the output
// cell-by-cell. No Civil 3D deps — PipeBurner is pure math on double[,].
//
// Usage:
//   dotnet run --project . -- <elev_in.csv> <pipes.csv> <elev_out.csv> <meta.csv> [trench]
//
// elev_in.csv:   CSV of doubles, one row per grid row. "nan" allowed.
// pipes.csv:     header "sx,sy,sz,ex,ey,ez" then one pipe per line.
// meta.csv:      header "origin_x,origin_y,cell_size" then one line of values.
// elev_out.csv:  written on exit. Same shape as elev_in.csv.

using System;
using System.Globalization;
using System.IO;
using CatchmentTool.Services;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: <elev_in> <pipes> <elev_out> <meta> [trench]");
    return 2;
}
string elevInPath = args[0];
string pipesPath = args[1];
string elevOutPath = args[2];
string metaPath = args[3];
double trench = args.Length >= 5
    ? double.Parse(args[4], CultureInfo.InvariantCulture) : 0.0;

var meta = ReadMeta(metaPath);
double originX = meta.originX;
double originY = meta.originY;
double cellSize = meta.cellSize;

var elev = ReadGrid(elevInPath);
int rows = elev.GetLength(0);
int cols = elev.GetLength(1);

var pipes = ReadPipes(pipesPath);

int modified = PipeBurner.Burn(elev, originX, originY, cellSize, pipes, trench);
Console.WriteLine($"burned {modified} cells; grid {rows}x{cols}; pipes {pipes.Count}");

WriteGrid(elevOutPath, elev);
return 0;

static (double originX, double originY, double cellSize) ReadMeta(string path)
{
    var lines = File.ReadAllLines(path);
    if (lines.Length < 2) throw new Exception($"bad meta file: {path}");
    var p = lines[1].Split(',');
    return (
        double.Parse(p[0], CultureInfo.InvariantCulture),
        double.Parse(p[1], CultureInfo.InvariantCulture),
        double.Parse(p[2], CultureInfo.InvariantCulture)
    );
}

static double[,] ReadGrid(string path)
{
    var lines = File.ReadAllLines(path);
    int rows = lines.Length;
    int cols = lines[0].Split(',').Length;
    var grid = new double[rows, cols];
    for (int r = 0; r < rows; r++)
    {
        var toks = lines[r].Split(',');
        if (toks.Length != cols)
            throw new Exception($"ragged grid at row {r}: {toks.Length} vs {cols}");
        for (int c = 0; c < cols; c++)
        {
            string t = toks[c].Trim();
            grid[r, c] = (t == "nan" || t == "NaN" || t == "")
                ? double.NaN
                : double.Parse(t, CultureInfo.InvariantCulture);
        }
    }
    return grid;
}

static System.Collections.Generic.List<PipeBurner.PipeSegment> ReadPipes(string path)
{
    var list = new System.Collections.Generic.List<PipeBurner.PipeSegment>();
    var lines = File.ReadAllLines(path);
    for (int i = 1; i < lines.Length; i++)  // skip header
    {
        var line = lines[i].Trim();
        if (line.Length == 0) continue;
        var t = line.Split(',');
        list.Add(new PipeBurner.PipeSegment
        {
            StartX = double.Parse(t[0], CultureInfo.InvariantCulture),
            StartY = double.Parse(t[1], CultureInfo.InvariantCulture),
            StartInvert = double.Parse(t[2], CultureInfo.InvariantCulture),
            EndX = double.Parse(t[3], CultureInfo.InvariantCulture),
            EndY = double.Parse(t[4], CultureInfo.InvariantCulture),
            EndInvert = double.Parse(t[5], CultureInfo.InvariantCulture),
        });
    }
    return list;
}

static void WriteGrid(string path, double[,] grid)
{
    int rows = grid.GetLength(0);
    int cols = grid.GetLength(1);
    using var w = new StreamWriter(path);
    var sb = new System.Text.StringBuilder();
    for (int r = 0; r < rows; r++)
    {
        sb.Clear();
        for (int c = 0; c < cols; c++)
        {
            if (c > 0) sb.Append(',');
            double v = grid[r, c];
            if (double.IsNaN(v)) sb.Append("nan");
            else sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
        }
        w.WriteLine(sb.ToString());
    }
}
