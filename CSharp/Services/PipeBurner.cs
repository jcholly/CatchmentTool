using System;
using System.Collections.Generic;

namespace CatchmentTool.Services
{
    /// <summary>
    /// Pipe burning for the BasinHierarchy elevation grid.
    ///
    /// Rationale: the raw TIN gives the lowest downstream collector an unfair
    /// share of the priority-flood label space — rising water from that single
    /// inlet reaches most upstream cells first, even when a designed pipe
    /// network would have intercepted them at an upstream inlet. Burning
    /// pipes to their invert elevations in the sampled grid creates low
    /// corridors that let upstream inlets compete for their true service area.
    ///
    /// Empirically (see tools/harness on testing.xml, cell=10 ft):
    ///   unburned — dominant inlet owns 49% of inside-TIN cells
    ///   burned   — dominant inlet owns 39%, 14 inlets each claim 2%+
    /// </summary>
    public static class PipeBurner
    {
        public struct PipeSegment
        {
            public double StartX, StartY, StartInvert;
            public double EndX, EndY, EndInvert;
        }

        /// <summary>
        /// Lower each grid cell along every pipe segment to the linearly
        /// interpolated invert elevation (minus optional trench depth), but
        /// only if the pipe value is below the current surface elevation.
        /// </summary>
        /// <returns>Number of cells whose elevation was lowered.</returns>
        public static int Burn(double[,] elev, double originX, double originY,
                               double cellSize, IEnumerable<PipeSegment> pipes,
                               double trenchDepth = 0.0)
        {
            if (pipes == null) return 0;
            int rows = elev.GetLength(0);
            int cols = elev.GetLength(1);
            int modified = 0;
            var seen = new HashSet<long>();

            foreach (var pipe in pipes)
            {
                if (!IsFinite(pipe.StartInvert) || !IsFinite(pipe.EndInvert))
                    continue;

                double dx = pipe.EndX - pipe.StartX;
                double dy = pipe.EndY - pipe.StartY;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-6) continue;

                // Two samples per cell so diagonal runs land on every grid
                // square they cross.
                int nSamples = Math.Max(2, (int)(length / (cellSize * 0.5)) + 1);
                seen.Clear();
                for (int i = 0; i < nSamples; i++)
                {
                    double t = (double)i / (nSamples - 1);
                    double x = pipe.StartX + t * dx;
                    double y = pipe.StartY + t * dy;
                    double zIv = pipe.StartInvert
                                 + t * (pipe.EndInvert - pipe.StartInvert)
                                 - trenchDepth;

                    int c = (int)Math.Round((x - originX) / cellSize);
                    int r = (int)Math.Round((y - originY) / cellSize);
                    if (r < 0 || r >= rows || c < 0 || c >= cols) continue;

                    long key = ((long)r << 32) | (uint)c;
                    if (!seen.Add(key)) continue;

                    double current = elev[r, c];
                    if (double.IsNaN(current)) continue;
                    if (zIv < current)
                    {
                        elev[r, c] = zIv;
                        modified++;
                    }
                }
            }
            return modified;
        }

        private static bool IsFinite(double v) =>
            !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
