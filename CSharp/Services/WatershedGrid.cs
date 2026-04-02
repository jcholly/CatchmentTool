using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace CatchmentTool.Services
{
    /// <summary>
    /// Builds a labeled watershed grid by tracing TIN flow from every cell
    /// in a regular grid. Each cell gets the ID of the inlet it drains to.
    /// The grid can then be vectorized into catchment polygons.
    /// </summary>
    public class WatershedGrid
    {
        private readonly TinSurface _surface;
        private readonly TinWalker _walker;
        private readonly double _cellSize;

        public int Rows { get; private set; }
        public int Cols { get; private set; }
        public double OriginX { get; private set; }
        public double OriginY { get; private set; }
        public int[,] Labels { get; private set; }

        public int TotalCells => Rows * Cols;
        public int TracedCells { get; private set; }
        public int UnresolvedCells { get; private set; }

        /// <param name="surface">Civil 3D TIN surface</param>
        /// <param name="walker">Configured TinWalker with inlets</param>
        /// <param name="cellSize">Grid cell size in drawing units</param>
        public WatershedGrid(TinSurface surface, TinWalker walker, double cellSize)
        {
            _surface = surface;
            _walker = walker;
            _cellSize = cellSize;
        }

        /// <summary>
        /// Trace every grid cell to build the watershed label grid.
        /// Returns elapsed time in seconds.
        /// </summary>
        /// <param name="progress">Optional callback: (cellsCompleted, totalCells)</param>
        public double Build(Action<int, int> progress = null)
        {
            var props = _surface.GetGeneralProperties();
            double minX = props.MinimumCoordinateX;
            double minY = props.MinimumCoordinateY;
            double maxX = props.MaximumCoordinateX;
            double maxY = props.MaximumCoordinateY;

            // Add small buffer so edge cells are inside surface
            double buffer = _cellSize;
            OriginX = minX + buffer;
            OriginY = minY + buffer;
            double extentX = (maxX - buffer) - OriginX;
            double extentY = (maxY - buffer) - OriginY;

            Cols = Math.Max(1, (int)(extentX / _cellSize) + 1);
            Rows = Math.Max(1, (int)(extentY / _cellSize) + 1);

            Labels = new int[Rows, Cols];
            TracedCells = 0;
            UnresolvedCells = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int total = Rows * Cols;

            for (int r = 0; r < Rows; r++)
            {
                double y = OriginY + r * _cellSize;
                for (int c = 0; c < Cols; c++)
                {
                    double x = OriginX + c * _cellSize;

                    // Skip cells outside the TIN boundary
                    try
                    {
                        _surface.FindElevationAtXY(x, y);
                    }
                    catch
                    {
                        Labels[r, c] = -1;
                        continue;
                    }

                    var result = _walker.Trace(x, y);
                    Labels[r, c] = result.InletId;

                    if (result.InletId >= 0)
                        TracedCells++;
                    else
                        UnresolvedCells++;

                    if (progress != null && (TracedCells + UnresolvedCells) % 1000 == 0)
                        progress(TracedCells + UnresolvedCells, total);
                }
            }

            sw.Stop();
            return sw.Elapsed.TotalSeconds;
        }

        /// <summary>
        /// Convert the label grid into catchment polygons using edge tracing.
        /// Adjacent catchments share exactly the same edge coordinates,
        /// guaranteeing snapped boundaries with no gaps or overlaps.
        /// </summary>
        public Dictionary<int, List<Point2d>> GetCatchmentBoundaries()
        {
            // Collect boundary edges for each label. An edge is a segment
            // between two cell-corner vertices that separates label L from
            // a different label (or the grid boundary).
            // Cell (r,c) has corners at:
            //   BL = (OriginX + c*cell, OriginY + r*cell)
            //   BR = (OriginX + (c+1)*cell, OriginY + r*cell)
            //   TL = (OriginX + c*cell, OriginY + (r+1)*cell)
            //   TR = (OriginX + (c+1)*cell, OriginY + (r+1)*cell)

            var edgesByLabel = new Dictionary<int, List<(Point2d a, Point2d b)>>();

            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++)
                {
                    int label = Labels[r, c];
                    if (label < 0) continue;

                    if (!edgesByLabel.ContainsKey(label))
                        edgesByLabel[label] = new List<(Point2d, Point2d)>();

                    double x0 = OriginX + c * _cellSize;
                    double y0 = OriginY + r * _cellSize;
                    double x1 = x0 + _cellSize;
                    double y1 = y0 + _cellSize;

                    var bl = new Point2d(x0, y0);
                    var br = new Point2d(x1, y0);
                    var tl = new Point2d(x0, y1);
                    var tr = new Point2d(x1, y1);

                    // Bottom edge: neighbor at (r-1, c)
                    if (r == 0 || Labels[r - 1, c] != label)
                        edgesByLabel[label].Add((bl, br));
                    // Top edge: neighbor at (r+1, c)
                    if (r == Rows - 1 || Labels[r + 1, c] != label)
                        edgesByLabel[label].Add((tl, tr));
                    // Left edge: neighbor at (r, c-1)
                    if (c == 0 || Labels[r, c - 1] != label)
                        edgesByLabel[label].Add((bl, tl));
                    // Right edge: neighbor at (r, c+1)
                    if (c == Cols - 1 || Labels[r, c + 1] != label)
                        edgesByLabel[label].Add((br, tr));
                }
            }

            var result = new Dictionary<int, List<Point2d>>();

            foreach (var kvp in edgesByLabel)
            {
                var ring = ChainEdges(kvp.Value);
                if (ring.Count >= 3)
                    result[kvp.Key] = ring;
            }

            return result;
        }

        /// <summary>
        /// Chain unordered edge segments into a closed polygon ring.
        /// Builds an adjacency map from each vertex to its connected edges,
        /// then walks the chain to produce an ordered ring.
        /// </summary>
        private List<Point2d> ChainEdges(List<(Point2d a, Point2d b)> edges)
        {
            if (edges.Count == 0) return new List<Point2d>();

            // Build adjacency: vertex → list of connected vertices
            var adj = new Dictionary<(long, long), List<(long, long)>>();

            (long, long) Key(Point2d p)
            {
                // Quantize to avoid floating-point mismatches
                return ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000));
            }

            Point2d FromKey((long, long) k)
            {
                return new Point2d(k.Item1 / 1000.0, k.Item2 / 1000.0);
            }

            foreach (var (a, b) in edges)
            {
                var ka = Key(a);
                var kb = Key(b);
                if (!adj.ContainsKey(ka)) adj[ka] = new List<(long, long)>();
                if (!adj.ContainsKey(kb)) adj[kb] = new List<(long, long)>();
                adj[ka].Add(kb);
                adj[kb].Add(ka);
            }

            // Walk the longest ring starting from the first vertex
            var visited = new HashSet<(long, long)>();
            var ring = new List<Point2d>();
            var start = adj.Keys.First();
            var current = start;

            do
            {
                visited.Add(current);
                ring.Add(FromKey(current));

                // Pick the next unvisited neighbor
                (long, long) next = (0, 0);
                bool found = false;
                foreach (var nb in adj[current])
                {
                    if (!visited.Contains(nb))
                    {
                        next = nb;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    // Check if we can close the ring
                    if (adj[current].Contains(start) && ring.Count > 2)
                        break;
                    else
                        break;
                }

                current = next;
            }
            while (current != start && ring.Count < edges.Count * 2);

            return ring;
        }

        /// <summary>
        /// Export the label grid as a GeoJSON FeatureCollection of polygons.
        /// Each feature has an "inlet_id" property.
        /// </summary>
        public string ExportGeoJson(Dictionary<int, string> inletNames = null)
        {
            var boundaries = GetCatchmentBoundaries();
            var features = new System.Text.StringBuilder();
            features.Append("{\"type\":\"FeatureCollection\",\"features\":[");

            bool first = true;
            foreach (var kvp in boundaries)
            {
                if (!first) features.Append(",");
                first = false;

                string name = inletNames != null && inletNames.ContainsKey(kvp.Key)
                    ? inletNames[kvp.Key] : $"Inlet_{kvp.Key}";

                features.Append("{\"type\":\"Feature\",");
                features.Append($"\"properties\":{{\"inlet_id\":{kvp.Key},\"name\":\"{name}\",\"area\":{ComputeArea(kvp.Value):F1}}},");
                features.Append("\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[");

                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    if (i > 0) features.Append(",");
                    features.Append($"[{kvp.Value[i].X:F4},{kvp.Value[i].Y:F4}]");
                }
                // Close the ring
                features.Append($",[{kvp.Value[0].X:F4},{kvp.Value[0].Y:F4}]");
                features.Append("]]}}");
            }

            features.Append("]}");
            return features.ToString();
        }

        private static double ComputeArea(List<Point2d> pts)
        {
            double area = 0;
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                area += pts[i].X * pts[j].Y;
                area -= pts[j].X * pts[i].Y;
            }
            return Math.Abs(area) / 2.0;
        }
    }
}
