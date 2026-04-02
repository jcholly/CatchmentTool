using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace CatchmentTool.Services
{
    /// <summary>
    /// Traces water flow across a TIN surface using steepest descent.
    /// For each starting point, walks triangle-by-triangle downhill until
    /// reaching a known inlet (sink) or a local minimum.
    /// </summary>
    public class TinWalker
    {
        private readonly TinSurface _surface;
        private readonly List<InletTarget> _inlets;
        private readonly double _snapTolerance;
        private readonly int _maxSteps;

        public struct InletTarget
        {
            public int Id;
            public double X;
            public double Y;
            public double Z;
            public string Name;
        }

        public struct TraceResult
        {
            public int InletId;      // -1 if unresolved
            public int StepCount;
            public double FinalX;
            public double FinalY;
        }

        /// <param name="surface">The TIN surface to walk on</param>
        /// <param name="inlets">Known inlet locations with IDs</param>
        /// <param name="snapTolerance">Distance (ft/m) to consider "arrived at inlet"</param>
        /// <param name="maxSteps">Safety limit to prevent infinite loops</param>
        public TinWalker(TinSurface surface, List<InletTarget> inlets,
                         double snapTolerance = 5.0, int maxSteps = 5000)
        {
            _surface = surface;
            _inlets = inlets;
            _snapTolerance = snapTolerance;
            _maxSteps = maxSteps;
        }

        /// <summary>
        /// Trace a single water drop from (startX, startY) downhill to an inlet.
        /// Returns the inlet ID it reaches, or -1 if it dead-ends.
        /// </summary>
        public TraceResult Trace(double startX, double startY)
        {
            double cx = startX, cy = startY;

            for (int step = 0; step < _maxSteps; step++)
            {
                // Check if we've arrived at an inlet
                int nearInlet = FindNearestInlet(cx, cy);
                if (nearInlet >= 0)
                    return new TraceResult { InletId = nearInlet, StepCount = step, FinalX = cx, FinalY = cy };

                // Find the triangle at current position
                TinSurfaceTriangle tri;
                try
                {
                    tri = _surface.FindTriangleAtXY(cx, cy);
                }
                catch
                {
                    // Off the surface — assign to nearest inlet
                    return ResolveOffSurface(cx, cy, step);
                }

                if (tri == null)
                    return ResolveOffSurface(cx, cy, step);

                // Get triangle vertices
                var p1 = tri.Vertex1.Location;
                var p2 = tri.Vertex2.Location;
                var p3 = tri.Vertex3.Location;

                // Compute steepest descent direction on this triangle plane
                var (dx, dy) = ComputeGradient(p1, p2, p3);

                // Flat triangle — local minimum
                double gradMag = Math.Sqrt(dx * dx + dy * dy);
                if (gradMag < 1e-10)
                    return ResolveStuck(cx, cy, step);

                // Normalize and step. Step size = distance to farthest edge
                // of the triangle to ensure we cross into the next one.
                double stepSize = EstimateTriangleSize(p1, p2, p3);
                double nx = -dx / gradMag;
                double ny = -dy / gradMag;

                double newX = cx + nx * stepSize;
                double newY = cy + ny * stepSize;

                // Verify we actually descended
                double curZ = InterpolateZ(p1, p2, p3, cx, cy);
                double newZ;
                try
                {
                    newZ = _surface.FindElevationAtXY(newX, newY);
                }
                catch
                {
                    return ResolveOffSurface(cx, cy, step);
                }

                if (newZ >= curZ - 1e-6)
                {
                    // Didn't descend — try a smaller step to stay on this triangle
                    stepSize *= 0.25;
                    newX = cx + nx * stepSize;
                    newY = cy + ny * stepSize;
                    try
                    {
                        newZ = _surface.FindElevationAtXY(newX, newY);
                    }
                    catch
                    {
                        return ResolveStuck(cx, cy, step);
                    }

                    if (newZ >= curZ - 1e-6)
                        return ResolveStuck(cx, cy, step);
                }

                cx = newX;
                cy = newY;
            }

            // Max steps exceeded
            return ResolveStuck(cx, cy, _maxSteps);
        }

        /// <summary>
        /// Compute the gradient (dz/dx, dz/dy) of the plane defined by 3 vertices.
        /// Returns the direction of steepest ASCENT; negate for descent.
        /// </summary>
        private static (double dx, double dy) ComputeGradient(Point3d p1, Point3d p2, Point3d p3)
        {
            // Plane equation: z = ax + by + c
            // Solve from 3 points using cross product of edge vectors
            double e1x = p2.X - p1.X, e1y = p2.Y - p1.Y, e1z = p2.Z - p1.Z;
            double e2x = p3.X - p1.X, e2y = p3.Y - p1.Y, e2z = p3.Z - p1.Z;

            // Normal = e1 x e2
            double nz = e1x * e2y - e1y * e2x;

            if (Math.Abs(nz) < 1e-12)
                return (0, 0); // degenerate triangle

            double nx = e1y * e2z - e1z * e2y;
            double ny = e1z * e2x - e1x * e2z;

            // dz/dx = -nx/nz,  dz/dy = -ny/nz
            return (-nx / nz, -ny / nz);
        }

        private static double InterpolateZ(Point3d p1, Point3d p2, Point3d p3,
                                            double x, double y)
        {
            var (dzdx, dzdy) = ComputeGradient(p1, p2, p3);
            return p1.Z + dzdx * (x - p1.X) + dzdy * (y - p1.Y);
        }

        private static double EstimateTriangleSize(Point3d p1, Point3d p2, Point3d p3)
        {
            double d1 = Dist2D(p1, p2);
            double d2 = Dist2D(p2, p3);
            double d3 = Dist2D(p3, p1);
            // Use median edge length as step size — large enough to cross
            // into the next triangle, not so large we overshoot
            double max = Math.Max(d1, Math.Max(d2, d3));
            double min = Math.Min(d1, Math.Min(d2, d3));
            return (d1 + d2 + d3 - max - min); // median
        }

        private static double Dist2D(Point3d a, Point3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private int FindNearestInlet(double x, double y)
        {
            double bestDist = _snapTolerance;
            int bestId = -1;
            for (int i = 0; i < _inlets.Count; i++)
            {
                double dx = x - _inlets[i].X;
                double dy = y - _inlets[i].Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestId = _inlets[i].Id;
                }
            }
            return bestId;
        }

        private TraceResult ResolveOffSurface(double x, double y, int steps)
        {
            int nearest = FindNearestInletUnlimited(x, y);
            return new TraceResult { InletId = nearest, StepCount = steps, FinalX = x, FinalY = y };
        }

        private TraceResult ResolveStuck(double x, double y, int steps)
        {
            // At a local minimum that's not an inlet — assign to nearest inlet
            int nearest = FindNearestInletUnlimited(x, y);
            return new TraceResult { InletId = nearest, StepCount = steps, FinalX = x, FinalY = y };
        }

        private int FindNearestInletUnlimited(double x, double y)
        {
            double bestDist = double.MaxValue;
            int bestId = -1;
            for (int i = 0; i < _inlets.Count; i++)
            {
                double dx = x - _inlets[i].X;
                double dy = y - _inlets[i].Y;
                double d = dx * dx + dy * dy;
                if (d < bestDist)
                {
                    bestDist = d;
                    bestId = _inlets[i].Id;
                }
            }
            return bestId;
        }
    }
}
