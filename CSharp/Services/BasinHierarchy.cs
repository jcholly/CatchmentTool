using System;
using System.Collections.Generic;
using Autodesk.Civil.DatabaseServices;

namespace CatchmentTool.Services
{
    /// <summary>
    /// Answers the question: "If water rose high enough, which inlet would
    /// the point at (x,y) ultimately drain to?"
    ///
    /// Uses priority-flood (Barnes, Lehman & Mulla 2014) seeded from inlet
    /// locations. Every reachable cell is labelled with the inlet whose
    /// rising-water plane reaches it first at the lowest spillover elevation.
    /// This is topologically correct for spillover routing — a strict
    /// improvement over nearest-inlet fallback.
    ///
    /// Cost: O(n log n) in grid cells, run once per delineation. Lookups are
    /// O(1) thereafter.
    /// </summary>
    public class BasinHierarchy
    {
        private readonly TinSurface _surface;
        private readonly double _cellSize;
        private readonly List<TinWalker.InletTarget> _inlets;
        private readonly IReadOnlyList<PipeBurner.PipeSegment> _pipes;
        private readonly double _trenchDepth;

        public int Rows { get; private set; }
        public int Cols { get; private set; }
        public double OriginX { get; private set; }
        public double OriginY { get; private set; }

        /// <summary>Per-cell inlet label (-1 = outside TIN or unreachable orphan basin).</summary>
        public int[,] Labels { get; private set; }

        /// <summary>Number of cells labelled with a valid inlet.</summary>
        public int LabelledCells { get; private set; }

        /// <summary>Number of cells inside the TIN that are in orphan basins (no inlet reachable).</summary>
        public int OrphanCells { get; private set; }

        /// <summary>Cells lowered by pipe burning. Zero if no pipes supplied.</summary>
        public int BurnedCells { get; private set; }

        public BasinHierarchy(TinSurface surface, List<TinWalker.InletTarget> inlets, double cellSize,
                              IReadOnlyList<PipeBurner.PipeSegment> pipes = null,
                              double trenchDepth = 0.0)
        {
            _surface = surface;
            _inlets = inlets;
            _cellSize = cellSize;
            _pipes = pipes;
            _trenchDepth = trenchDepth;
        }

        /// <summary>
        /// Run priority-flood. Populates Labels[r,c] with the inlet ID that
        /// rising water would spill toward from that cell.
        /// </summary>
        /// <returns>Elapsed seconds.</returns>
        public double Build(Action<int, int> progress = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var props = _surface.GetGeneralProperties();

            // Match WatershedGrid's framing so XY→(r,c) conversions agree.
            double buffer = _cellSize;
            OriginX = props.MinimumCoordinateX + buffer;
            OriginY = props.MinimumCoordinateY + buffer;
            double extentX = (props.MaximumCoordinateX - buffer) - OriginX;
            double extentY = (props.MaximumCoordinateY - buffer) - OriginY;

            Cols = Math.Max(1, (int)(extentX / _cellSize) + 1);
            Rows = Math.Max(1, (int)(extentY / _cellSize) + 1);

            // Phase 1: sample elevations; NaN = off-surface (treated as barrier).
            var elev = new double[Rows, Cols];
            for (int r = 0; r < Rows; r++)
            {
                double y = OriginY + r * _cellSize;
                for (int c = 0; c < Cols; c++)
                {
                    double x = OriginX + c * _cellSize;
                    try { elev[r, c] = _surface.FindElevationAtXY(x, y); }
                    catch { elev[r, c] = double.NaN; }
                }
            }

            // Phase 1b: burn pipes into the elev grid. On designed sites
            // this is what keeps the lowest collector from claiming majority
            // of the priority-flood label space via rising-water spillover.
            BurnedCells = 0;
            if (_pipes != null && _pipes.Count > 0)
            {
                BurnedCells = PipeBurner.Burn(
                    elev, OriginX, OriginY, _cellSize, _pipes, _trenchDepth);
            }

            Labels = new int[Rows, Cols];
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    Labels[r, c] = -1;

            // Phase 2: seed priority queue with cells containing inlets.
            // Priority = the "water surface elevation" at which this cell is
            // reached. Seeded at the cell's own elevation; propagates
            // monotonically outward via max(neighbor_elev, current_priority).
            var pq = new PriorityQueue<(int r, int c, int inletId), double>();
            var enqueued = new bool[Rows, Cols];

            for (int i = 0; i < _inlets.Count; i++)
            {
                var inlet = _inlets[i];
                int c = (int)Math.Round((inlet.X - OriginX) / _cellSize);
                int r = (int)Math.Round((inlet.Y - OriginY) / _cellSize);
                if (r < 0 || r >= Rows || c < 0 || c >= Cols) continue;
                if (double.IsNaN(elev[r, c])) continue;

                // If two inlets land in the same cell, the later one wins —
                // same behaviour as the existing walker's snap logic; resolve
                // upstream via distinct cellSize or by deduplicating inlets.
                pq.Enqueue((r, c, inlet.Id), elev[r, c]);
                enqueued[r, c] = true;
                Labels[r, c] = inlet.Id;
            }

            // Phase 3: priority-flood. Each pop propagates its label to
            // 4-connected neighbours. Monotonic rising-water elevation
            // guarantees we visit saddles before the basins they spill into.
            int[] dr = { -1, 1, 0, 0 };
            int[] dc = { 0, 0, -1, 1 };
            int processed = 0;
            int totalInside = 0;
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    if (!double.IsNaN(elev[r, c])) totalInside++;

            while (pq.Count > 0)
            {
                pq.TryDequeue(out var cell, out double waterElev);
                processed++;

                for (int k = 0; k < 4; k++)
                {
                    int nr = cell.r + dr[k];
                    int nc = cell.c + dc[k];
                    if (nr < 0 || nr >= Rows || nc < 0 || nc >= Cols) continue;
                    if (enqueued[nr, nc]) continue;
                    if (double.IsNaN(elev[nr, nc])) continue;

                    // The neighbour fills to at least its own elevation, OR
                    // to the current water level if we're spilling uphill
                    // (which we never are here, but this is the canonical
                    // priority-flood monotonic step).
                    double neighbourWater = Math.Max(elev[nr, nc], waterElev);
                    Labels[nr, nc] = cell.inletId;
                    enqueued[nr, nc] = true;
                    pq.Enqueue((nr, nc, cell.inletId), neighbourWater);
                }

                if (progress != null && processed % 2000 == 0)
                    progress(processed, totalInside);
            }

            // Count outcomes.
            LabelledCells = 0;
            OrphanCells = 0;
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Cols; c++)
                {
                    if (double.IsNaN(elev[r, c])) continue;
                    if (Labels[r, c] >= 0) LabelledCells++;
                    else OrphanCells++;
                }
            }

            sw.Stop();
            return sw.Elapsed.TotalSeconds;
        }

        /// <summary>
        /// Look up the spillover inlet for a world-coordinate point.
        /// Returns -1 if the point is outside the TIN or in an orphan basin.
        /// </summary>
        public int GetInletForXY(double x, double y)
        {
            int c = (int)Math.Round((x - OriginX) / _cellSize);
            int r = (int)Math.Round((y - OriginY) / _cellSize);
            if (r < 0 || r >= Rows || c < 0 || c >= Cols) return -1;
            return Labels[r, c];
        }
    }
}
