using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CatchmentTool2.Pipeline;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;

namespace CatchmentTool.Services
{
    /// <summary>
    /// Emit native Civil 3D Catchment objects from pipeline results. Adapted from
    /// the legacy CatchmentCreator.cs:
    ///   - Use Point3dCollection (not 2D Polyline) with Z draped from the TIN.
    ///   - Ensure boundary ring is explicitly closed (first == last).
    ///   - Retry with increasing simplify tolerances on "not simple polygon" errors.
    ///   - Use reflection to discover Catchment.Create overloads (API differs
    ///     between Civil 3D versions).
    ///   - Set ReferencePipeNetworkId BEFORE ReferencePipeNetworkStructureId.
    ///   - Fall back to ReferenceDischargeObjectId, then DischargePoint setters.
    /// </summary>
    public static class CatchmentEmitter
    {
        public static int Emit(Document doc, ObjectId surfaceId, ObjectId networkId,
            IReadOnlyList<CatchmentTool2.Network.Structure> structures,
            PipelineResult result, Dictionary<string, ObjectId> handleToObjectId,
            Action<string, Brush> log)
        {
            var db = doc.Database;
            var civilDoc = CivilApplication.ActiveDocument;
            int created = 0;

            using var tr = db.TransactionManager.StartTransaction();
            ObjectId groupId = GetOrCreateGroup(civilDoc, tr, $"CT_{DateTime.Now:HHmmss}");
            ObjectId styleId = GetFirstCatchmentStyle(civilDoc);
            TinSurface tinSurf = null;
            try { tinSurf = tr.GetObject(surfaceId, OpenMode.ForRead) as TinSurface; }
            catch { }

            foreach (var c in result.Catchments)
            {
                var boundary = BuildBoundary3d(c.Geometry.Vertices, tinSurf);
                ObjectId catchId = TryCreate($"CT_{c.StructureId}", styleId, groupId, surfaceId, boundary, log);
                if (catchId.IsNull) continue;
                created++;
                try
                {
                    var ct = (Catchment)tr.GetObject(catchId, OpenMode.ForWrite);
                    try { ct.Name = $"CT_{c.StructureId}"; } catch { }
                    SetReferences(ct, surfaceId, networkId,
                        handleToObjectId.GetValueOrDefault(c.StructureId, ObjectId.Null), tr);
                }
                catch (AcadException ex)
                {
                    log($"  warn: link {c.StructureId}: {ex.Message}", Brushes.Yellow);
                }
            }
            tr.Commit();
            return created;
        }

        private static Point3dCollection BuildBoundary3d(IReadOnlyList<CatchmentTool2.Vec2> ring, TinSurface tin)
        {
            var coll = new Point3dCollection();
            foreach (var v in ring)
            {
                double z = 0;
                if (tin != null)
                {
                    try { z = tin.FindElevationAtXY(v.X, v.Y); } catch { z = 0; }
                }
                coll.Add(new Point3d(v.X, v.Y, z));
            }
            // Civil 3D requires explicitly closed ring.
            if (coll.Count > 0 &&
                !coll[0].IsEqualTo(coll[coll.Count - 1], new Tolerance(1e-6, 1e-6)))
                coll.Add(coll[0]);
            return coll;
        }

        private static ObjectId GetOrCreateGroup(CivilDocument civilDoc, Transaction tr, string name)
        {
            try
            {
                var prop = civilDoc.GetType().GetProperty("CatchmentGroups");
                if (prop != null)
                {
                    var groups = prop.GetValue(civilDoc);
                    if (groups != null)
                    {
                        var addMethod = groups.GetType().GetMethod("Add", new[] { typeof(string) });
                        if (addMethod != null)
                        {
                            var r = addMethod.Invoke(groups, new object[] { name });
                            if (r is ObjectId id && !id.IsNull) return id;
                        }
                        foreach (ObjectId gid in (System.Collections.IEnumerable)groups)
                        {
                            var g = (CatchmentGroup)tr.GetObject(gid, OpenMode.ForRead);
                            if (g.Name == name) return gid;
                        }
                    }
                }
            }
            catch { }
            return ObjectId.Null;
        }

        private static ObjectId GetFirstCatchmentStyle(CivilDocument civilDoc)
        {
            try
            {
                foreach (ObjectId sid in civilDoc.Styles.CatchmentStyles)
                    if (!sid.IsNull) return sid;
            }
            catch { }
            return ObjectId.Null;
        }

        private static ObjectId TryCreate(string name, ObjectId styleId, ObjectId groupId,
            ObjectId surfaceId, Point3dCollection boundary, Action<string, Brush> log)
        {
            var methods = typeof(Catchment).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Create").ToList();
            double[] tolerances = { 0, 0.5, 1.0, 2.0 };
            string lastErr = null;

            foreach (var tol in tolerances)
            {
                var pts = tol > 0 ? Simplify(boundary, tol) : boundary;
                foreach (var m in methods)
                {
                    var parms = m.GetParameters();
                    object[] args = null;
                    if (parms.Length == 5 && parms[0].ParameterType == typeof(string) &&
                        parms[4].ParameterType == typeof(Point3dCollection))
                        args = new object[] { name, styleId, groupId, surfaceId, pts };
                    else if (parms.Length == 4 && parms[0].ParameterType == typeof(string) &&
                             parms[3].ParameterType == typeof(Point3dCollection))
                        args = new object[] { name, styleId, groupId, pts };
                    if (args == null) continue;
                    try
                    {
                        var r = m.Invoke(null, args);
                        if (r is ObjectId id && !id.IsNull) return id;
                    }
                    catch (System.Exception ex)
                    {
                        lastErr = ex.InnerException?.Message ?? ex.Message;
                    }
                }
                if (lastErr == null) break;
                if (!(lastErr.Contains("simple") || lastErr.Contains("closed") || lastErr.Contains("Boundary"))) break;
            }
            if (lastErr != null) log($"  Catchment.Create failed: {lastErr}", Brushes.Yellow);
            return ObjectId.Null;
        }

        private static Point3dCollection Simplify(Point3dCollection ring, double tol)
        {
            if (ring.Count < 4) return ring;
            var result = new Point3dCollection { ring[0] };
            for (int i = 1; i < ring.Count - 1; i++)
            {
                var prev = result[result.Count - 1];
                var p = ring[i];
                if (prev.DistanceTo(p) >= tol) result.Add(p);
            }
            result.Add(ring[ring.Count - 1]);
            return result;
        }

        private static bool SetReferences(Catchment ct, ObjectId surfaceId, ObjectId networkId,
            ObjectId structureId, Transaction tr)
        {
            var ctType = ct.GetType();
            if (!surfaceId.IsNull)
            {
                var p = ctType.GetProperty("ReferenceSurfaceId") ?? ctType.GetProperty("SurfaceId");
                try { p?.SetValue(ct, surfaceId); } catch { }
            }
            if (structureId.IsNull) return false;

            if (!networkId.IsNull)
            {
                var np = ctType.GetProperty("ReferencePipeNetworkId");
                try { np?.SetValue(ct, networkId); } catch { }
            }
            bool linked = false;
            var sp = ctType.GetProperty("ReferencePipeNetworkStructureId");
            if (sp != null && sp.CanWrite)
            {
                try { sp.SetValue(ct, structureId); linked = true; } catch { }
            }
            if (!linked)
            {
                var dp = ctType.GetProperty("ReferenceDischargeObjectId");
                if (dp != null && dp.CanWrite)
                {
                    try { dp.SetValue(ct, structureId); linked = true; } catch { }
                }
            }
            try
            {
                var s = (Autodesk.Civil.DatabaseServices.Structure)tr.GetObject(structureId, OpenMode.ForRead);
                var pos = s.Position;
                foreach (var pn in new[] { "DischargePoint", "DischargePointLocation", "OutletPoint", "DischargeLocation" })
                {
                    var prop = ctType.GetProperty(pn);
                    if (prop == null || !prop.CanWrite) continue;
                    try
                    {
                        if (prop.PropertyType == typeof(Point2d)) prop.SetValue(ct, new Point2d(pos.X, pos.Y));
                        else if (prop.PropertyType == typeof(Point3d)) prop.SetValue(ct, pos);
                        break;
                    }
                    catch { }
                }
            }
            catch { }
            return linked;
        }
    }
}
