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

        /// <summary>Per-cell record of how each label was decided.</summary>
        public TinWalker.Resolution[,] Resolutions { get; private set; }

        public int TotalCells => Rows * Cols;
        public int TracedCells { get; private set; }
        public int UnresolvedCells { get; private set; }

        // Breakdown by resolution type (Phase 1 telemetry).
        public int DirectCells { get; private set; }
        public int SpilloverCells { get; private set; }
        public int OffSurfaceCells { get; private set; }
        public int OrphanCells { get; private set; }

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
        /// <param name="hierarchy">
        /// Optional spillover map. When provided, cells where the walker
        /// gets stuck or goes off-surface are resolved via rising-water
        /// routing instead of falling through unassigned. Strongly
        /// recommended for small sites with micro-sinks.
        /// </param>
        public double Build(Action<int, int> progress = null, BasinHierarchy hierarchy = null)
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
            Resolutions = new TinWalker.Resolution[Rows, Cols];
            TracedCells = 0;
            UnresolvedCells = 0;
            DirectCells = 0;
            SpilloverCells = 0;
            OffSurfaceCells = 0;
            OrphanCells = 0;

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
                        Resolutions[r, c] = TinWalker.Resolution.OffSurface;
                        continue;
                    }

                    // Prefer the D8 walker on the conditioned grid when a
                    // hierarchy is available — it succeeds far more often
                    // than the TIN walker on real fragmented surfaces
                    // because micro-sinks have been filled. Fall back to
                    // TinWalker only when no hierarchy was supplied.
                    TinWalker.TraceResult result = hierarchy != null
                        ? hierarchy.TraceD8(x, y, _walker.SnapTolerance)
                        : _walker.Trace(x, y);
                    int label = result.InletId;
                    var resolution = result.HowResolved;

                    // Walker gave us Direct / OffSurface / Orphan. For the
                    // last two, consult the spillover hierarchy: "if water
                    // rose high enough, where does this cell drain?"
                    if (label < 0 && hierarchy != null)
                    {
                        int spillInlet = hierarchy.GetInletForXY(x, y);
                        if (spillInlet >= 0)
                        {
                            label = spillInlet;
                            resolution = TinWalker.Resolution.Spillover;
                        }
                    }

                    Labels[r, c] = label;
                    Resolutions[r, c] = resolution;

                    if (label >= 0) TracedCells++;
                    else UnresolvedCells++;

                    switch (resolution)
                    {
                        case TinWalker.Resolution.Direct:      DirectCells++;     break;
                        case TinWalker.Resolution.Spillover:   SpilloverCells++;  break;
                        case TinWalker.Resolution.OffSurface:  OffSurfaceCells++; break;
                        case TinWalker.Resolution.Orphan:      OrphanCells++;     break;
                    }

                    if (progress != null && (TracedCells + UnresolvedCells) % 1000 == 0)
                        progress(TracedCells + UnresolvedCells, total);
                }
            }

            sw.Stop();
            return sw.Elapsed.TotalSeconds;
        }

        /// <summary>
        /// Post-pass: Voronoi-style assignment of every cell without a label
        /// to its Euclidean-nearest inlet, capped by a coverage radius.
        /// Fills (a) orphan inside-TIN cells priority-flood couldn't reach
        /// and (b) interior TIN holes (building footprints) — water on a
        /// building roof drains via storm piping to some inlet, so the
        /// footprint legitimately belongs to the nearest inlet's catchment.
        ///
        /// Cells beyond the coverage radius from every inlet stay excluded —
        /// that matches the user's rule "if it doesn't drain somewhere,
        /// don't include it." The radius is the larger of
        /// <paramref name="minRescueRadius"/> and an inlet-density-derived
        /// distance (4 × site_diag / sqrt(N)).
        /// </summary>
        /// <param name="inlets">Inlets to assign to.</param>
        /// <param name="minRescueRadius">
        /// Floor on the coverage radius, in drawing units. Caller is
        /// responsible for choosing a value appropriate for the drawing's
        /// unit system (e.g. 150 ft / 45 m). Pass 0 to use the
        /// inlet-density formula alone.
        /// </param>
        /// <returns>The number of cells reassigned.</returns>
        public int ResolveOrphansToNearestInlet(List<TinWalker.InletTarget> inlets,
                                                double minRescueRadius = 0.0)
        {
            if (inlets == null || inlets.Count == 0) return 0;

            double siteDiag = Math.Sqrt(
                (double)(Cols * _cellSize) * (Cols * _cellSize)
                + (double)(Rows * _cellSize) * (Rows * _cellSize));
            double maxDist = Math.Max(minRescueRadius,
                4.0 * siteDiag / Math.Sqrt(inlets.Count));
            double maxDist2 = maxDist * maxDist;

            int rescued = 0;
            for (int r = 0; r < Rows; r++)
            {
                double y = OriginY + r * _cellSize;
                for (int c = 0; c < Cols; c++)
                {
                    if (Labels[r, c] >= 0) continue;

                    double x = OriginX + c * _cellSize;
                    double bestD2 = double.MaxValue;
                    int bestId = -1;
                    for (int i = 0; i < inlets.Count; i++)
                    {
                        double dx = x - inlets[i].X;
                        double dy = y - inlets[i].Y;
                        double d2 = dx * dx + dy * dy;
                        if (d2 < bestD2)
                        {
                            bestD2 = d2;
                            bestId = inlets[i].Id;
                        }
                    }
                    if (bestId < 0 || bestD2 > maxDist2) continue;

                    Labels[r, c] = bestId;
                    var prev = Resolutions[r, c];
                    Resolutions[r, c] = TinWalker.Resolution.Spillover;
                    SpilloverCells++;
                    if (prev == TinWalker.Resolution.Orphan)
                    {
                        OrphanCells--;
                        TracedCells++;
                        UnresolvedCells--;
                    }
                    else if (prev == TinWalker.Resolution.OffSurface)
                    {
                        OffSurfaceCells--;
                        TracedCells++;
                    }
                    rescued++;
                }
            }
            return rescued;
        }

        /// <summary>
        /// One boundary polygon per (inlet, connected-component) pair. When
        /// an inlet's territory is split into disjoint pieces (common on
        /// fragmented TINs), each piece becomes its own polygon sharing the
        /// same InletId. Caller is responsible for mapping InletId to the
        /// corresponding Civil 3D structure.
        /// Components smaller than <paramref name="minCells"/> are skipped.
        /// </summary>
        public List<CatchmentPolygon> GetCatchmentPolygons(int minCells = 1)
        {
            var results = new List<CatchmentPolygon>();
            var visited = new bool[Rows, Cols];

            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++)
                {
                    if (visited[r, c]) continue;
                    int lab = Labels[r, c];
                    if (lab < 0) { visited[r, c] = true; continue; }

                    // BFS the connected component for this label.
                    var cells = new List<(int r, int c)>();
                    var queue = new Queue<(int r, int c)>();
                    queue.Enqueue((r, c));
                    visited[r, c] = true;
                    while (queue.Count > 0)
                    {
                        var (cr, cc) = queue.Dequeue();
                        cells.Add((cr, cc));
                        if (cr > 0 && !visited[cr - 1, cc] && Labels[cr - 1, cc] == lab)
                        { visited[cr - 1, cc] = true; queue.Enqueue((cr - 1, cc)); }
                        if (cr + 1 < Rows && !visited[cr + 1, cc] && Labels[cr + 1, cc] == lab)
                        { visited[cr + 1, cc] = true; queue.Enqueue((cr + 1, cc)); }
                        if (cc > 0 && !visited[cr, cc - 1] && Labels[cr, cc - 1] == lab)
                        { visited[cr, cc - 1] = true; queue.Enqueue((cr, cc - 1)); }
                        if (cc + 1 < Cols && !visited[cr, cc + 1] && Labels[cr, cc + 1] == lab)
                        { visited[cr, cc + 1] = true; queue.Enqueue((cr, cc + 1)); }
                    }

                    if (cells.Count < minCells) continue;

                    var ring = TraceComponentBoundary(cells, lab);
                    if (ring.Count < 3) continue;

                    results.Add(new CatchmentPolygon
                    {
                        InletId = lab,
                        CellCount = cells.Count,
                        Ring = ring,
                    });
                }
            }
            return results;
        }

        private List<Point2d> TraceComponentBoundary(
            List<(int r, int c)> cells, int label)
        {
            var edges = new List<(Point2d a, Point2d b)>();
            foreach (var (r, c) in cells)
            {
                double x0 = OriginX + c * _cellSize;
                double y0 = OriginY + r * _cellSize;
                double x1 = x0 + _cellSize;
                double y1 = y0 + _cellSize;
                var bl = new Point2d(x0, y0);
                var br = new Point2d(x1, y0);
                var tl = new Point2d(x0, y1);
                var tr = new Point2d(x1, y1);
                if (r == 0 || Labels[r - 1, c] != label)
                    edges.Add((bl, br));
                if (r == Rows - 1 || Labels[r + 1, c] != label)
                    edges.Add((tl, tr));
                if (c == 0 || Labels[r, c - 1] != label)
                    edges.Add((bl, tl));
                if (c == Cols - 1 || Labels[r, c + 1] != label)
                    edges.Add((br, tr));
            }
            return ChainEdges(edges);
        }

        /// <summary>
        /// Convert the label grid into catchment polygons using edge tracing.
        /// Adjacent catchments share exactly the same edge coordinates,
        /// guaranteeing snapped boundaries with no gaps or overlaps.
        /// Kept for back-compat; prefer GetCatchmentPolygons which supports
        /// multiple disjoint components per inlet.
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

        /// <summary>
        /// Merge connected components smaller than <paramref name="minCells"/>
        /// into the dominant neighbor label. Preserves full tessellation
        /// (no cells set to -1). Repeats until stable, capped at 5 passes.
        /// Returns number of small fragments merged away.
        /// </summary>
        public int MergeSmallComponents(int minCells)
        {
            if (minCells <= 1) return 0;
            int totalMerged = 0;
            for (int pass = 0; pass < 5; pass++)
            {
                bool changed = false;
                var visited = new bool[Rows, Cols];
                for (int r = 0; r < Rows; r++)
                {
                    for (int c = 0; c < Cols; c++)
                    {
                        if (visited[r, c]) continue;
                        int lab = Labels[r, c];
                        if (lab < 0) { visited[r, c] = true; continue; }

                        var cells = new List<(int r, int c)>();
                        var queue = new Queue<(int r, int c)>();
                        queue.Enqueue((r, c));
                        visited[r, c] = true;
                        while (queue.Count > 0)
                        {
                            var (cr, cc) = queue.Dequeue();
                            cells.Add((cr, cc));
                            if (cr > 0 && !visited[cr - 1, cc] && Labels[cr - 1, cc] == lab)
                            { visited[cr - 1, cc] = true; queue.Enqueue((cr - 1, cc)); }
                            if (cr + 1 < Rows && !visited[cr + 1, cc] && Labels[cr + 1, cc] == lab)
                            { visited[cr + 1, cc] = true; queue.Enqueue((cr + 1, cc)); }
                            if (cc > 0 && !visited[cr, cc - 1] && Labels[cr, cc - 1] == lab)
                            { visited[cr, cc - 1] = true; queue.Enqueue((cr, cc - 1)); }
                            if (cc + 1 < Cols && !visited[cr, cc + 1] && Labels[cr, cc + 1] == lab)
                            { visited[cr, cc + 1] = true; queue.Enqueue((cr, cc + 1)); }
                        }
                        if (cells.Count >= minCells) continue;

                        // Find dominant neighbor label across the component perimeter.
                        var votes = new Dictionary<int, int>();
                        foreach (var (cr, cc) in cells)
                        {
                            VoteNeighbor(votes, cr - 1, cc);
                            VoteNeighbor(votes, cr + 1, cc);
                            VoteNeighbor(votes, cr, cc - 1);
                            VoteNeighbor(votes, cr, cc + 1);
                        }
                        if (votes.Count == 0) continue;  // no labelled neighbor
                        int newLabel = lab;
                        int bestVotes = -1;
                        foreach (var kv in votes)
                        {
                            if (kv.Value > bestVotes)
                            {
                                bestVotes = kv.Value;
                                newLabel = kv.Key;
                            }
                        }
                        if (newLabel != lab)
                        {
                            foreach (var (cr, cc) in cells)
                                Labels[cr, cc] = newLabel;
                            totalMerged++;
                            changed = true;
                        }
                    }
                }
                if (!changed) break;
            }
            return totalMerged;
        }

        private void VoteNeighbor(Dictionary<int, int> votes, int r, int c)
        {
            if (r < 0 || r >= Rows || c < 0 || c >= Cols) return;
            int lab = Labels[r, c];
            if (lab < 0) return;
            votes[lab] = votes.TryGetValue(lab, out int v) ? v + 1 : 1;
        }
    }

    /// <summary>
    /// One catchment polygon: a connected region of cells all assigned to
    /// the same inlet. A single inlet can produce multiple polygons when
    /// its territory is split by TIN holes or divided by ridges.
    /// </summary>
    public class CatchmentPolygon
    {
        public int InletId;
        public int CellCount;
        public List<Point2d> Ring;
    }
}
