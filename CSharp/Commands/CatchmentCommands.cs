using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CatchmentTool.Services;
using CatchmentTool.UI;

using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CatchmentTool.Commands.CatchmentCommands))]

namespace CatchmentTool.Commands
{
    public class CatchmentCommands
    {
        // =====================================================================
        // COMMAND 1: CATCHMENTAUTO
        // Opens the automated delineation dialog (export surface + inlets,
        // run WhiteboxTools, import Civil 3D Catchment objects).
        // =====================================================================

        [CommandMethod("CATCHMENTAUTO")]
        public void CatchmentAutoDialog()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            try
            {
                var dialog = new AutoCatchmentDialog(doc);
                Application.ShowModalWindow(dialog);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nError opening dialog: {ex.Message}\n");
            }
        }

        // =====================================================================
        // COMMAND 2: MAKECATCHMENTS
        // Select existing closed polylines → find the pipe network structure
        // inside each polygon → create Civil 3D Catchment objects with that
        // structure assigned as the outlet.
        // =====================================================================

        [CommandMethod("MAKECATCHMENTS")]
        public void MakeCatchments()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc.Editor;
            var db  = doc.Database;

            try
            {
                ed.WriteMessage("\n=== MAKE CATCHMENTS FROM LINEWORK ===\n");
                ed.WriteMessage("Select closed polylines. Each will become a Civil 3D Catchment\n");
                ed.WriteMessage("with its outlet assigned to the structure found inside it.\n\n");

                // 1 — Select closed polylines
                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
                });
                var selOpts = new PromptSelectionOptions { MessageForAdding = "\nSelect closed polylines: " };
                var selResult = ed.GetSelection(selOpts, filter);
                if (selResult.Status != PromptStatus.OK || selResult.Value.Count == 0)
                {
                    ed.WriteMessage("No polylines selected.\n");
                    return;
                }

                // 2 — Reference surface
                ObjectId surfaceId = SelectSurface(ed, db);
                if (surfaceId.IsNull) return;

                // 3 — All structures from all pipe networks in the drawing
                var structures = GetAllStructures(db);
                if (structures.Count == 0)
                {
                    ed.WriteMessage("No pipe network structures found in this drawing.\n");
                    return;
                }
                ed.WriteMessage($"Found {structures.Count} structures across all pipe networks.\n\n");

                // 4 — Build polygon list: one entry per closed polyline
                var polygons = new List<CatchmentPolygonInfo>();

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    int num = 1;
                    foreach (SelectedObject so in selResult.Value)
                    {
                        var pline = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Polyline;
                        if (pline == null || !pline.Closed || pline.NumberOfVertices < 3) continue;

                        var pts = new List<Point2d>();
                        for (int i = 0; i < pline.NumberOfVertices; i++)
                            pts.Add(pline.GetPoint2dAt(i));

                        // Point-in-polygon: find ALL structures inside this polygon
                        var matchedStructures = new List<(ObjectId Id, string Name)>();
                        foreach (var (sId, sName, sPos) in structures)
                        {
                            if (PointInPolygon(new Point2d(sPos.X, sPos.Y), pts))
                            {
                                matchedStructures.Add((sId, sName));
                            }
                        }

                        ObjectId matchedId   = matchedStructures.Count > 0 ? matchedStructures[0].Id : ObjectId.Null;
                        string  matchedName  = matchedStructures.Count > 0 ? matchedStructures[0].Name : "";

                        polygons.Add(new CatchmentPolygonInfo
                        {
                            BoundaryPoints   = pts,
                            StructureName    = matchedName,
                            StructureObjectId = matchedId,
                        });

                        if (matchedStructures.Count > 1)
                        {
                            ed.WriteMessage($"  [{num:D2}] outlet → {matchedName} (WARNING: {matchedStructures.Count} structures inside polygon: {string.Join(", ", matchedStructures.Select(s => s.Name))})\n");
                        }
                        else if (matchedName.Length > 0)
                        {
                            ed.WriteMessage($"  [{num:D2}] outlet → {matchedName}\n");
                        }
                        else
                        {
                            ed.WriteMessage($"  [{num:D2}] no structure found inside — catchment will have no outlet\n");
                        }

                        num++;
                    }
                    tr.Commit();
                }

                if (polygons.Count == 0)
                {
                    ed.WriteMessage("No valid closed polylines found in selection.\n");
                    return;
                }

                // 5 — Create Civil 3D Catchment objects
                ed.WriteMessage($"\nCreating {polygons.Count} catchment(s)...\n");
                var creator = new CatchmentCreator(doc);
                var result  = creator.CreateCatchmentsFromPolylines(polygons, surfaceId);

                ed.WriteMessage($"\n=== COMPLETE: {result.CatchmentsCreated} catchment(s) created ===\n");
                foreach (var err in result.Errors)
                    ed.WriteMessage($"  Warning: {err}\n");
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nError: {ex.Message}\n");
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private ObjectId SelectSurface(Editor ed, Database db)
        {
            var civilDoc   = CivilApplication.ActiveDocument;
            var surfaceIds = civilDoc.GetSurfaceIds();

            if (surfaceIds.Count == 0)
            {
                ed.WriteMessage("\nNo surfaces found in this drawing.\n");
                return ObjectId.Null;
            }

            ed.WriteMessage("\nAvailable surfaces:\n");
            using (var tr = db.TransactionManager.StartTransaction())
            {
                int i = 1;
                foreach (ObjectId id in surfaceIds)
                {
                    var s = tr.GetObject(id, OpenMode.ForRead) as CivilSurface;
                    if (s != null) ed.WriteMessage($"  {i}. {s.Name}\n");
                    i++;
                }
                tr.Commit();
            }

            var opts = new PromptIntegerOptions("\nSelect surface number: ")
            {
                LowerLimit = 1,
                UpperLimit = surfaceIds.Count,
                AllowNone  = false
            };
            var res = ed.GetInteger(opts);
            return res.Status == PromptStatus.OK ? surfaceIds[res.Value - 1] : ObjectId.Null;
        }

        private List<(ObjectId Id, string Name, Point3d Position)> GetAllStructures(Database db)
        {
            var list      = new List<(ObjectId, string, Point3d)>();
            var civilDoc  = CivilApplication.ActiveDocument;
            var networkIds = civilDoc.GetPipeNetworkIds();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId netId in networkIds)
                {
                    var network = tr.GetObject(netId, OpenMode.ForRead) as Network;
                    if (network == null) continue;
                    foreach (ObjectId sId in network.GetStructureIds())
                    {
                        var s = tr.GetObject(sId, OpenMode.ForRead) as Structure;
                        if (s != null) list.Add((sId, s.Name, s.Position));
                    }
                }
                tr.Commit();
            }
            return list;
        }

        /// <summary>Ray-casting point-in-polygon test.</summary>
        private static bool PointInPolygon(Point2d pt, List<Point2d> poly)
        {
            bool inside = false;
            int  j = poly.Count - 1;
            for (int i = 0; i < poly.Count; i++)
            {
                var pi = poly[i]; var pj = poly[j];
                if (((pi.Y > pt.Y) != (pj.Y > pt.Y)) &&
                    (pt.X < (pj.X - pi.X) * (pt.Y - pi.Y) / (pj.Y - pi.Y) + pi.X))
                    inside = !inside;
                j = i;
            }
            return inside;
        }
    }
}
