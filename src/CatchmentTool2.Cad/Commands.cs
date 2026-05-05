// Civil 3D NETLOAD plugin entry point.
// To use: in Civil 3D, run NETLOAD and select this DLL, then type CT2_DELINEATE.
//
// Catchment-creation patterns adapted from the legacy CatchmentTool/CatchmentCreator.cs:
//   - Use Point3dCollection (not 2D Polyline) with Z draped from the TIN surface
//   - Ensure boundary ring is explicitly closed (first == last point)
//   - Retry with increasing simplification tolerances (0, 0.5, 1.0, 2.0) on
//     "not a simple polygon" / "not closed" errors
//   - Use reflection to discover Catchment.Create overloads — Civil 3D's Catchment
//     API surface has shifted between versions
//   - Set ReferencePipeNetworkId BEFORE ReferencePipeNetworkStructureId
//   - Fall back to ReferenceDischargeObjectId, then DischargePoint property setters

using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CatchmentTool2;
using CatchmentTool2.Network;
using CatchmentTool2.Pipeline;
using CatchmentTool2.Surface;
using AcadException = Autodesk.AutoCAD.Runtime.Exception;

[assembly: CommandClass(typeof(CatchmentTool2.Cad.Commands))]

namespace CatchmentTool2.Cad;

public class Commands
{
    [CommandMethod("CT2_DELINEATE")]
    public void Delineate()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;
        var db = doc.Database;
        var civilDoc = CivilApplication.ActiveDocument;

        var peo = new PromptEntityOptions("\nSelect Civil 3D Surface (TIN): ");
        peo.SetRejectMessage("\nMust be a TinSurface.");
        peo.AddAllowedClass(typeof(TinSurface), exactMatch: false);
        var per = ed.GetEntity(peo);
        if (per.Status != PromptStatus.OK) return;

        ObjectId networkId = ObjectId.Null;
        var pno = new PromptEntityOptions("\nSelect Pipe Network (or ENTER for none): ");
        pno.SetRejectMessage("\nMust be a Network.");
        pno.AddAllowedClass(typeof(Autodesk.Civil.DatabaseServices.Network), exactMatch: false);
        pno.AllowNone = true;
        var pnr = ed.GetEntity(pno);
        if (pnr.Status == PromptStatus.OK) networkId = pnr.ObjectId;

        Tin tin;
        List<Network.Structure> structures;
        Network.PipeNetwork? cnet = null;
        var handleToObjectId = new Dictionary<string, ObjectId>();
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var surf = (TinSurface)tr.GetObject(per.ObjectId, OpenMode.ForRead);
            tin = ReadTin(surf);
            (structures, cnet, handleToObjectId) = networkId == ObjectId.Null
                ? (new List<Network.Structure>(), null, new Dictionary<string, ObjectId>())
                : ReadNetwork(tr, networkId);
            tr.Commit();
        }

        if (structures.Count == 0)
        {
            ed.WriteMessage("\nNo structures found in pipe network. Aborting.");
            return;
        }

        var p = new TuningParameters();
        var input = new PipelineInput(tin, structures, cnet, null);
        ed.WriteMessage($"\nDelineating: {structures.Count} structures, {tin.Triangles.Count:N0} triangles");
        var result = CatchmentPipeline.Run(input, p);
        ed.WriteMessage($"\n{result.Catchments.Count} catchments produced ({result.RuntimeSeconds:F1}s)");
        ed.WriteMessage($"\nTopo-assigned: {result.TopoAssignedCells:N0} cells, fallback: {result.FallbackAssignedCells:N0}, unassigned: {result.UnassignedCells:N0}");

        EmitCatchments(db, per.ObjectId, networkId, structures, result, handleToObjectId, ed);
    }

    private static Tin ReadTin(TinSurface surf)
    {
        var verts = new List<TinVertex>();
        var indexById = new Dictionary<long, int>();
        foreach (TinSurfaceVertex v in surf.Vertices)
        {
            indexById[v.VertexNumber] = verts.Count;
            verts.Add(new TinVertex(v.Location.X, v.Location.Y, v.Location.Z));
        }
        var tris = new List<TinTriangle>();
        foreach (TinSurfaceTriangle t in surf.Triangles)
        {
            try
            {
                int a = indexById[t.Vertex1.VertexNumber];
                int b = indexById[t.Vertex2.VertexNumber];
                int c = indexById[t.Vertex3.VertexNumber];
                tris.Add(new TinTriangle(a, b, c));
            }
            catch { }
        }
        return new Tin(verts, tris);
    }

    private static (List<Network.Structure>, Network.PipeNetwork, Dictionary<string, ObjectId>) ReadNetwork(
        Transaction tr, ObjectId networkId)
    {
        var structures = new List<Network.Structure>();
        var pipes = new List<Network.Pipe>();
        var handleToId = new Dictionary<string, ObjectId>();
        var net = (Autodesk.Civil.DatabaseServices.Network)tr.GetObject(networkId, OpenMode.ForRead);
        foreach (ObjectId sid in net.GetStructureIds())
        {
            var s = (Autodesk.Civil.DatabaseServices.Structure)tr.GetObject(sid, OpenMode.ForRead);
            string handle = s.Handle.ToString();
            handleToId[handle] = sid;
            structures.Add(new Network.Structure(
                Id: handle,
                Location: new Vec2(s.Position.X, s.Position.Y),
                RimElevation: s.RimElevation,
                Kind: Network.StructureKind.Inlet,
                UserSelected: true)); // select-all default per Tier 1 #1
        }
        foreach (ObjectId pid in net.GetPipeIds())
        {
            var p = (Autodesk.Civil.DatabaseServices.Pipe)tr.GetObject(pid, OpenMode.ForRead);
            string sid = p.StartStructureId == ObjectId.Null ? "" :
                ((Autodesk.Civil.DatabaseServices.Structure)tr.GetObject(p.StartStructureId, OpenMode.ForRead)).Handle.ToString();
            string eid = p.EndStructureId == ObjectId.Null ? "" :
                ((Autodesk.Civil.DatabaseServices.Structure)tr.GetObject(p.EndStructureId, OpenMode.ForRead)).Handle.ToString();
            if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(eid)) continue;
            pipes.Add(new Network.Pipe(p.Handle.ToString(), sid, eid, p.StartPoint.Z, p.EndPoint.Z));
        }
        var hasDown = pipes.Select(x => x.StartStructureId).ToHashSet();
        var classified = structures.Select(s =>
            s with
            {
                Kind = hasDown.Contains(s.Id) ? Network.StructureKind.Inlet : Network.StructureKind.Pond,
                UserSelected = true,
            }).ToList();
        return (classified, new Network.PipeNetwork(classified, pipes), handleToId);
    }

    private static void EmitCatchments(Database db, ObjectId surfaceId, ObjectId networkId,
        IReadOnlyList<Network.Structure> structures, PipelineResult result,
        Dictionary<string, ObjectId> handleToObjectId, Editor ed)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var civilDoc = CivilApplication.ActiveDocument;
        var groupId = GetOrCreateCatchmentGroup(civilDoc, tr, $"CT2_{DateTime.Now:HHmmss}");
        if (groupId == ObjectId.Null)
        {
            ed.WriteMessage("\nERROR: could not create catchment group.");
            return;
        }
        ObjectId styleId = GetFirstCatchmentStyle(civilDoc, tr);

        TinSurface? tinSurf = null;
        try { tinSurf = tr.GetObject(surfaceId, OpenMode.ForRead) as TinSurface; }
        catch { }

        int created = 0, linked = 0;
        foreach (var c in result.Catchments)
        {
            // Drape Z onto boundary points from the TIN surface.
            var boundary = new Point3dCollection();
            foreach (var v in c.Geometry.Vertices)
            {
                double z = 0;
                if (tinSurf != null)
                {
                    try { z = tinSurf.FindElevationAtXY(v.X, v.Y); }
                    catch { z = 0; }
                }
                boundary.Add(new Point3d(v.X, v.Y, z));
            }
            // Explicitly close the ring (Civil 3D requires first == last).
            if (boundary.Count > 0)
            {
                var first = boundary[0];
                var last = boundary[boundary.Count - 1];
                if (!first.IsEqualTo(last, new Tolerance(1e-6, 1e-6)))
                    boundary.Add(first);
            }

            ObjectId catchId = TryCreateCatchment(c.StructureId, styleId, groupId, surfaceId, boundary, ed);
            if (catchId.IsNull) continue;
            created++;

            // Link the catchment to its outlet structure (network must be set first).
            try
            {
                var ct = (Catchment)tr.GetObject(catchId, OpenMode.ForWrite);
                try { ct.Name = $"CT2_{c.StructureId}"; } catch { }
                bool ok = SetCatchmentReferences(ct, surfaceId, networkId,
                    handleToObjectId.GetValueOrDefault(c.StructureId, ObjectId.Null), tr, ed);
                if (ok) linked++;
            }
            catch (AcadException ex)
            {
                ed.WriteMessage($"\n  warn: could not link {c.StructureId}: {ex.Message}");
            }
        }
        tr.Commit();
        ed.WriteMessage($"\nCreated {created} Catchment objects ({linked} linked to structures).");
    }

    private static ObjectId GetOrCreateCatchmentGroup(CivilDocument civilDoc, Transaction tr, string name)
    {
        try
        {
            return civilDoc.CatchmentGroups.Add(name);
        }
        catch (AcadException) { /* group exists */ }
        try
        {
            foreach (ObjectId gid in civilDoc.CatchmentGroups)
            {
                var g = (CatchmentGroup)tr.GetObject(gid, OpenMode.ForRead);
                if (g.Name == name) return gid;
            }
        }
        catch { }
        return ObjectId.Null;
    }

    private static ObjectId GetFirstCatchmentStyle(CivilDocument civilDoc, Transaction tr)
    {
        try
        {
            var styles = civilDoc.Styles.CatchmentStyles;
            foreach (ObjectId sid in styles)
                if (!sid.IsNull) return sid;
        }
        catch { }
        return ObjectId.Null;
    }

    /// <summary>
    /// Try Catchment.Create with the actual boundary; on "not simple" / "not closed"
    /// failures, retry with increasing simplification tolerances. Use reflection to
    /// match whichever Catchment.Create overload the installed Civil 3D version exposes.
    /// </summary>
    private static ObjectId TryCreateCatchment(string name, ObjectId styleId, ObjectId groupId,
        ObjectId surfaceId, Point3dCollection boundary, Editor ed)
    {
        var createMethods = typeof(Catchment).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Create").ToList();
        double[] simplifyTolerances = { 0, 0.5, 1.0, 2.0 };
        string? lastErr = null;

        foreach (var tol in simplifyTolerances)
        {
            var pts = tol > 0 ? SimplifyRing(boundary, tol) : boundary;
            foreach (var m in createMethods)
            {
                var parms = m.GetParameters();
                object[]? args = null;
                if (parms.Length == 5 && parms[0].ParameterType == typeof(string) &&
                    parms[4].ParameterType == typeof(Point3dCollection))
                {
                    args = new object[] { name, styleId, groupId, surfaceId, pts };
                }
                else if (parms.Length == 4 && parms[0].ParameterType == typeof(string) &&
                         parms[3].ParameterType == typeof(Point3dCollection))
                {
                    args = new object[] { name, styleId, groupId, pts };
                }
                if (args == null) continue;
                try
                {
                    var result = m.Invoke(null, args);
                    if (result is ObjectId id && !id.IsNull) return id;
                }
                catch (System.Exception ex)
                {
                    lastErr = ex.InnerException?.Message ?? ex.Message;
                }
            }
            // Only retry with higher tolerance for geometry errors.
            if (lastErr == null) break;
            if (!(lastErr.Contains("simple") || lastErr.Contains("closed") || lastErr.Contains("Boundary")))
                break;
        }
        if (lastErr != null) ed.WriteMessage($"\n  Catchment.Create failed: {lastErr}");
        return ObjectId.Null;
    }

    private static Point3dCollection SimplifyRing(Point3dCollection ring, double tol)
    {
        if (ring.Count < 4) return ring;
        // Distance-based decimation: keep first point, then any point at least `tol` from last kept.
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

    /// <summary>
    /// Set the catchment's reference surface and outlet structure. The pipe network
    /// must be assigned before the structure (Civil 3D enforces this dependency).
    /// Falls back to ReferenceDischargeObjectId and the various DischargePoint
    /// properties if the primary structure-link API is unavailable.
    /// </summary>
    private static bool SetCatchmentReferences(Catchment ct, ObjectId surfaceId, ObjectId networkId,
        ObjectId structureId, Transaction tr, Editor ed)
    {
        var ctType = ct.GetType();
        // Reference surface
        if (!surfaceId.IsNull)
        {
            var surfProp = ctType.GetProperty("ReferenceSurfaceId") ?? ctType.GetProperty("SurfaceId");
            try { surfProp?.SetValue(ct, surfaceId); } catch { }
        }
        if (structureId.IsNull) return false;

        // Step 1: pipe network (parent of the structure)
        if (!networkId.IsNull)
        {
            var netProp = ctType.GetProperty("ReferencePipeNetworkId");
            try { netProp?.SetValue(ct, networkId); } catch { }
        }
        // Step 2: structure reference — primary path
        bool linked = false;
        var structProp = ctType.GetProperty("ReferencePipeNetworkStructureId");
        if (structProp != null && structProp.CanWrite)
        {
            try { structProp.SetValue(ct, structureId); linked = true; } catch { }
        }
        // Step 3: discharge object reference — Civil 3D 2026.2+ alternative
        if (!linked)
        {
            var dischProp = ctType.GetProperty("ReferenceDischargeObjectId");
            if (dischProp != null && dischProp.CanWrite)
            {
                try { dischProp.SetValue(ct, structureId); linked = true; } catch { }
            }
        }
        // Step 4: discharge point at structure location (multiple property names by version)
        try
        {
            var s = (Autodesk.Civil.DatabaseServices.Structure)tr.GetObject(structureId, OpenMode.ForRead);
            var pos = s.Position;
            string[] propNames = { "DischargePoint", "DischargePointLocation", "OutletPoint", "DischargeLocation" };
            foreach (var pn in propNames)
            {
                var prop = ctType.GetProperty(pn);
                if (prop == null || !prop.CanWrite) continue;
                try
                {
                    if (prop.PropertyType == typeof(Point2d))
                        prop.SetValue(ct, new Point2d(pos.X, pos.Y));
                    else if (prop.PropertyType == typeof(Point3d))
                        prop.SetValue(ct, pos);
                    break;
                }
                catch { }
            }
            // Method-based fallback
            var setMethod = ctType.GetMethod("SetDischargePoint");
            if (setMethod != null)
            {
                var parms = setMethod.GetParameters();
                if (parms.Length == 1)
                {
                    try
                    {
                        if (parms[0].ParameterType == typeof(Point2d))
                            setMethod.Invoke(ct, new object[] { new Point2d(pos.X, pos.Y) });
                        else if (parms[0].ParameterType == typeof(Point3d))
                            setMethod.Invoke(ct, new object[] { pos });
                    }
                    catch { }
                }
            }
        }
        catch { }

        return linked;
    }
}
