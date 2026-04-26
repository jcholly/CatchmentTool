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
        private readonly double _maxFillDepth;
        private readonly double _skipRadius;
        private double[,] _conditionedElev;

        /// <summary>
        /// Conditioned elevation grid (after pipe burning + light
        /// conditioning). Same shape as Labels. NaN where off-surface.
        /// Available after Build() runs. Used by D8 walker.
        /// </summary>
        public double[,] ConditionedElev => _conditionedElev;

        public int RaisedCells { get; private set; }

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

        /// <param name="surface">Civil 3D TIN surface.</param>
        /// <param name="inlets">Inlet locations with IDs.</param>
        /// <param name="cellSize">Grid cell size in drawing units.</param>
        /// <param name="pipes">Optional pipe segments to burn into the elev grid.</param>
        /// <param name="trenchDepth">Trench depth in drawing units (passed to PipeBurner).</param>
        /// <param name="maxFillDepth">
        /// Cap on how high light-conditioning may raise any single cell, in
        /// drawing units. Pass 0 to skip light-conditioning entirely.
        /// Caller is responsible for choosing a value appropriate for the
        /// drawing's unit system (e.g. 0.5 ft / 0.15 m).
        /// </param>
        /// <param name="skipRadius">
        /// Cells within this radius of any inlet are exempt from light-
        /// conditioning (preserves designed low points), in drawing units.
        /// Pass 0 to disable the inlet-skip behavior. Caller chooses a
        /// unit-appropriate value (e.g. 10 ft / 3 m).
        /// </param>
        public BasinHierarchy(TinSurface surface, List<TinWalker.InletTarget> inlets, double cellSize,
                              IReadOnlyList<PipeBurner.PipeSegment> pipes = null,
                              double trenchDepth = 0.0,
                              double maxFillDepth = 0.0,
                              double skipRadius = 0.0)
        {
            _surface = surface;
            _inlets = inlets;
            _cellSize = cellSize;
            _pipes = pipes;
            _trenchDepth = trenchDepth;
            _maxFillDepth = maxFillDepth;
            _skipRadius = skipRadius;
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

            // Phase 1c: light conditioning. Fill micro-sinks (depth less
            // than _maxFillDepth) so the D8 walker doesn't die in TIN
            // noise. Skip-near-inlet leaves designed low points alone.
            RaisedCells = 0;
            if (_maxFillDepth > 0)
                RaisedCells = LightCondition(elev);

            // Stash the conditioned grid so external callers (e.g. the
            // D8 walker) can use it for steepest-descent.
            _conditionedElev = elev;

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

        /// <summary>
        /// Planchon-Darboux fill with a depth cap and skip-near-inlet:
        /// raises every cell in a basin to the saddle elevation it would
        /// spill from, but only up to <c>_maxFillDepth</c> above its
        /// original elevation. Cells within <c>_skipRadius</c> of any
        /// inlet stay at original elevation (designed sinks).
        ///
        /// Note: this is intentionally NOT canonical priority-flood. Both
        /// the depth cap and skip-near-inlet enqueue cells with priorities
        /// below the parent's water level, violating the strict-monotonic
        /// invariant. The result is "leaky" filling — designed outlets and
        /// hard-capped saddles let water escape downstream at lower-than-
        /// flood elevation, which matches "light" conditioning intent. Do
        /// not "fix" the violations without rethinking the semantics.
        /// </summary>
        /// <returns>Count of cells whose elevation was raised.</returns>
        private int LightCondition(double[,] elev)
        {
            var orig = (double[,])elev.Clone();
            var visited = new bool[Rows, Cols];
            var pq = new PriorityQueue<(int r, int c), double>();

            // Seed: grid boundary cells + inlet cells.
            for (int c = 0; c < Cols; c++)
            {
                if (!double.IsNaN(elev[0, c]))
                { pq.Enqueue((0, c), elev[0, c]); visited[0, c] = true; }
                if (!double.IsNaN(elev[Rows - 1, c]))
                { pq.Enqueue((Rows - 1, c), elev[Rows - 1, c]); visited[Rows - 1, c] = true; }
            }
            for (int r = 0; r < Rows; r++)
            {
                if (!visited[r, 0] && !double.IsNaN(elev[r, 0]))
                { pq.Enqueue((r, 0), elev[r, 0]); visited[r, 0] = true; }
                if (!visited[r, Cols - 1] && !double.IsNaN(elev[r, Cols - 1]))
                { pq.Enqueue((r, Cols - 1), elev[r, Cols - 1]); visited[r, Cols - 1] = true; }
            }
            for (int i = 0; i < _inlets.Count; i++)
            {
                int c = (int)Math.Round((_inlets[i].X - OriginX) / _cellSize);
                int r = (int)Math.Round((_inlets[i].Y - OriginY) / _cellSize);
                if (r < 0 || r >= Rows || c < 0 || c >= Cols) continue;
                if (double.IsNaN(elev[r, c])) continue;
                if (!visited[r, c])
                { pq.Enqueue((r, c), elev[r, c]); visited[r, c] = true; }
            }

            // Precompute a per-cell near-inlet mask so the inner loop is
            // O(1) instead of O(N inlets). One pass paints a disk around
            // each inlet — total cost O(N * r²) where r = skipRadius/cellSize,
            // typically << R*C*N.
            bool[,] nearInletMask = null;
            if (_skipRadius > 0)
            {
                nearInletMask = new bool[Rows, Cols];
                int rad = (int)Math.Ceiling(_skipRadius / _cellSize);
                double skipR2 = _skipRadius * _skipRadius;
                for (int i = 0; i < _inlets.Count; i++)
                {
                    double inletX = _inlets[i].X;
                    double inletY = _inlets[i].Y;
                    int ic = (int)Math.Round((inletX - OriginX) / _cellSize);
                    int ir = (int)Math.Round((inletY - OriginY) / _cellSize);
                    int r0 = Math.Max(0, ir - rad);
                    int r1 = Math.Min(Rows - 1, ir + rad);
                    int c0 = Math.Max(0, ic - rad);
                    int c1 = Math.Min(Cols - 1, ic + rad);
                    for (int r = r0; r <= r1; r++)
                    {
                        double dy = (OriginY + r * _cellSize) - inletY;
                        double dy2 = dy * dy;
                        for (int c = c0; c <= c1; c++)
                        {
                            if (nearInletMask[r, c]) continue;
                            double dx = (OriginX + c * _cellSize) - inletX;
                            if (dx * dx + dy2 < skipR2)
                                nearInletMask[r, c] = true;
                        }
                    }
                }
            }

            int[] dr = { -1, 1, 0, 0 };
            int[] dc = { 0, 0, -1, 1 };
            int raised = 0;

            while (pq.Count > 0)
            {
                pq.TryDequeue(out var cell, out double waterZ);
                for (int k = 0; k < 4; k++)
                {
                    int nr = cell.r + dr[k];
                    int nc = cell.c + dc[k];
                    if (nr < 0 || nr >= Rows || nc < 0 || nc >= Cols) continue;
                    if (visited[nr, nc]) continue;
                    if (double.IsNaN(elev[nr, nc])) continue;
                    visited[nr, nc] = true;

                    double cellOrig = orig[nr, nc];

                    // Skip-near-inlet: don't fill near designed low points.
                    if (nearInletMask != null && nearInletMask[nr, nc])
                    {
                        pq.Enqueue((nr, nc), cellOrig);
                        continue;
                    }

                    double target = Math.Max(cellOrig, waterZ);
                    double cap = cellOrig + _maxFillDepth;
                    if (target > cap) target = cap;
                    if (target > cellOrig)
                    {
                        elev[nr, nc] = target;
                        raised++;
                    }
                    pq.Enqueue((nr, nc), target);
                }
            }
            return raised;
        }

        /// <summary>
        /// D8 steepest-descent walker on the conditioned grid. Returns a
        /// TraceResult with HowResolved=Direct if it physically reached an
        /// inlet (within snapTol), Orphan if it stalled in a local min,
        /// or OffSurface if it left the grid.
        /// Uses ConditionedElev — call after Build().
        /// </summary>
        public TinWalker.TraceResult TraceD8(double startX, double startY,
                                             double snapTol, int maxSteps = 5000)
        {
            if (_conditionedElev == null)
                throw new InvalidOperationException(
                    "TraceD8 requires Build() first.");

            int c = (int)Math.Round((startX - OriginX) / _cellSize);
            int r = (int)Math.Round((startY - OriginY) / _cellSize);
            double cx = OriginX + c * _cellSize;
            double cy = OriginY + r * _cellSize;

            if (r < 0 || r >= Rows || c < 0 || c >= Cols
                || double.IsNaN(_conditionedElev[r, c]))
            {
                return new TinWalker.TraceResult
                {
                    InletId = -1, StepCount = 0, FinalX = cx, FinalY = cy,
                    HowResolved = TinWalker.Resolution.OffSurface, Path = null
                };
            }

            double diag = Math.Sqrt(2.0);
            int[] ndr = { -1, 1, 0, 0, -1, -1, 1, 1 };
            int[] ndc = { 0, 0, -1, 1, -1, 1, -1, 1 };
            double[] ndist = { 1.0, 1.0, 1.0, 1.0, diag, diag, diag, diag };

            for (int step = 0; step < maxSteps; step++)
            {
                int hitId = NearestInletWithin(cx, cy, snapTol);
                if (hitId >= 0)
                {
                    return new TinWalker.TraceResult
                    {
                        InletId = hitId, StepCount = step,
                        FinalX = cx, FinalY = cy,
                        HowResolved = TinWalker.Resolution.Direct, Path = null
                    };
                }

                double curZ = _conditionedElev[r, c];
                double bestSlope = 0.0;
                int bestK = -1;
                for (int k = 0; k < 8; k++)
                {
                    int nr = r + ndr[k];
                    int nc = c + ndc[k];
                    if (nr < 0 || nr >= Rows || nc < 0 || nc >= Cols) continue;
                    double nz = _conditionedElev[nr, nc];
                    if (double.IsNaN(nz)) continue;
                    double slope = (curZ - nz) / (ndist[k] * _cellSize);
                    if (slope > bestSlope) { bestSlope = slope; bestK = k; }
                }
                if (bestK < 0)
                {
                    return new TinWalker.TraceResult
                    {
                        InletId = -1, StepCount = step,
                        FinalX = cx, FinalY = cy,
                        HowResolved = TinWalker.Resolution.Orphan, Path = null
                    };
                }

                double newX = cx + ndc[bestK] * _cellSize;
                double newY = cy + ndr[bestK] * _cellSize;

                // Inlet interception along the segment.
                if (TryInterceptSegment(cx, cy, newX, newY, snapTol,
                                        out int interceptId,
                                        out double hx, out double hy))
                {
                    return new TinWalker.TraceResult
                    {
                        InletId = interceptId, StepCount = step,
                        FinalX = hx, FinalY = hy,
                        HowResolved = TinWalker.Resolution.Direct, Path = null
                    };
                }

                r += ndr[bestK];
                c += ndc[bestK];
                cx = newX;
                cy = newY;
            }
            return new TinWalker.TraceResult
            {
                InletId = -1, StepCount = maxSteps,
                FinalX = cx, FinalY = cy,
                HowResolved = TinWalker.Resolution.Orphan, Path = null
            };
        }

        private int NearestInletWithin(double x, double y, double snap)
        {
            double bestD = snap;
            int bestId = -1;
            for (int i = 0; i < _inlets.Count; i++)
            {
                double dx = x - _inlets[i].X;
                double dy = y - _inlets[i].Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < bestD) { bestD = d; bestId = _inlets[i].Id; }
            }
            return bestId;
        }

        private bool TryInterceptSegment(double sx, double sy,
                                         double ex, double ey, double snap,
                                         out int hitId,
                                         out double hx, out double hy)
        {
            hitId = -1; hx = 0; hy = 0;
            double dx = ex - sx, dy = ey - sy;
            double seg2 = dx * dx + dy * dy;
            if (seg2 < 1e-18) return false;
            double snap2 = snap * snap;
            double bestT = 2.0;
            for (int i = 0; i < _inlets.Count; i++)
            {
                double t = ((_inlets[i].X - sx) * dx
                            + (_inlets[i].Y - sy) * dy) / seg2;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                double px = sx + t * dx;
                double py = sy + t * dy;
                double ddx = _inlets[i].X - px;
                double ddy = _inlets[i].Y - py;
                double d2 = ddx * ddx + ddy * ddy;
                if (d2 < snap2 && t < bestT)
                {
                    bestT = t; hitId = _inlets[i].Id;
                    hx = px; hy = py;
                }
            }
            return hitId >= 0;
        }
    }
}
