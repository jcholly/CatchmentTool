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
        /// Convert the label grid into catchment polygons (Point2dCollection per inlet).
        /// Uses a simple boundary-tracing approach: for each inlet ID, collect all
        /// grid cells with that label and build a convex boundary.
        /// For cleaner polygons, export as raster and vectorize in Python.
        /// </summary>
        public Dictionary<int, List<Point2d>> GetCatchmentBoundaries()
        {
            var cellsByInlet = new Dictionary<int, List<(int r, int c)>>();

            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++)
                {
                    int label = Labels[r, c];
                    if (label < 0) continue;

                    if (!cellsByInlet.ContainsKey(label))
                        cellsByInlet[label] = new List<(int r, int c)>();
                    cellsByInlet[label].Add((r, c));
                }
            }

            var result = new Dictionary<int, List<Point2d>>();

            foreach (var kvp in cellsByInlet)
            {
                var boundary = ExtractBoundary(kvp.Value);
                if (boundary.Count >= 3)
                    result[kvp.Key] = boundary;
            }

            return result;
        }

        /// <summary>
        /// Extract the outer boundary of a set of grid cells.
        /// Finds cells that border a different label (edge cells),
        /// then orders them into a polygon via angular sweep.
        /// </summary>
        private List<Point2d> ExtractBoundary(List<(int r, int c)> cells)
        {
            var cellSet = new HashSet<(int r, int c)>(cells);
            var edgeCells = new List<(int r, int c)>();

            int[] dr = { -1, 1, 0, 0 };
            int[] dc = { 0, 0, -1, 1 };

            foreach (var (r, c) in cells)
            {
                for (int d = 0; d < 4; d++)
                {
                    var nb = (r + dr[d], c + dc[d]);
                    if (!cellSet.Contains(nb))
                    {
                        edgeCells.Add((r, c));
                        break;
                    }
                }
            }

            if (edgeCells.Count < 3)
            {
                return cells.Select(cell =>
                    new Point2d(OriginX + cell.c * _cellSize, OriginY + cell.r * _cellSize)
                ).ToList();
            }

            // Convert to world coordinates
            var pts = edgeCells.Select(cell =>
                new Point2d(OriginX + cell.c * _cellSize, OriginY + cell.r * _cellSize)
            ).ToList();

            // Order by angle from centroid for a clean polygon
            double cx = pts.Average(p => p.X);
            double cy = pts.Average(p => p.Y);
            pts.Sort((a, b) =>
            {
                double aa = Math.Atan2(a.Y - cy, a.X - cx);
                double ba = Math.Atan2(b.Y - cy, b.X - cx);
                return aa.CompareTo(ba);
            });

            return pts;
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
