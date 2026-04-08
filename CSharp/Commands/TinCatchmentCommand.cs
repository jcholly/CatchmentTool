using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

[assembly: CommandClass(typeof(CatchmentTool.Commands.TinCatchmentCommand))]

namespace CatchmentTool.Commands
{
    /// <summary>
    /// CATCHMENTTIN — TIN-based catchment delineation.
    /// Walks the TIN surface directly (no rasterization, no WhiteboxTools).
    /// Traces steepest descent from a regular grid of points to determine
    /// which inlet each location drains to, then creates Civil 3D Catchments.
    /// </summary>
    public class TinCatchmentCommand
    {
        [CommandMethod("CATCHMENTTIN")]
        public void CatchmentTin()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            try
            {
                var dialog = new TinCatchmentDialog(doc);
                Application.ShowModalWindow(dialog);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nError opening dialog: {ex.Message}\n");
            }
        }

        private ObjectId SelectTinSurface(Editor ed, Database db)
        {
            var civilDoc = CivilApplication.ActiveDocument;
            var surfaceIds = civilDoc.GetSurfaceIds();
            var tinIds = new List<ObjectId>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                ed.WriteMessage("\nAvailable TIN surfaces:\n");
                int i = 1;
                foreach (ObjectId id in surfaceIds)
                {
                    var s = tr.GetObject(id, OpenMode.ForRead) as TinSurface;
                    if (s != null)
                    {
                        tinIds.Add(id);
                        var props = s.GetGeneralProperties();
                        ed.WriteMessage($"  {i}. {s.Name} ({props.NumberOfPoints:N0} points)\n");
                        i++;
                    }
                }
                tr.Commit();
            }

            if (tinIds.Count == 0)
            {
                ed.WriteMessage("No TIN surfaces found.\n");
                return ObjectId.Null;
            }

            if (tinIds.Count == 1)
            {
                ed.WriteMessage($"  (auto-selected only TIN surface)\n");
                return tinIds[0];
            }

            var opts = new PromptIntegerOptions("\nSelect surface number: ")
            { LowerLimit = 1, UpperLimit = tinIds.Count, AllowNone = false };
            var res = ed.GetInteger(opts);
            return res.Status == PromptStatus.OK ? tinIds[res.Value - 1] : ObjectId.Null;
        }

        private ObjectId SelectNetwork(Editor ed, Database db)
        {
            var civilDoc = CivilApplication.ActiveDocument;
            var networkIds = civilDoc.GetPipeNetworkIds();

            if (networkIds.Count == 0)
            {
                ed.WriteMessage("No pipe networks found.\n");
                return ObjectId.Null;
            }

            var netList = new List<ObjectId>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                ed.WriteMessage("\nAvailable pipe networks:\n");
                int i = 1;
                foreach (ObjectId id in networkIds)
                {
                    var net = tr.GetObject(id, OpenMode.ForRead) as Network;
                    if (net != null)
                    {
                        netList.Add(id);
                        int structs = net.GetStructureIds().Count;
                        ed.WriteMessage($"  {i}. {net.Name} ({structs} structures)\n");
                        i++;
                    }
                }
                tr.Commit();
            }

            if (netList.Count == 1)
            {
                ed.WriteMessage($"  (auto-selected only network)\n");
                return netList[0];
            }

            var opts = new PromptIntegerOptions("\nSelect network number: ")
            { LowerLimit = 1, UpperLimit = netList.Count, AllowNone = false };
            var res = ed.GetInteger(opts);
            return res.Status == PromptStatus.OK ? netList[res.Value - 1] : ObjectId.Null;
        }

        private double PromptCellSize(Editor ed)
        {
            var opts = new PromptDoubleOptions("\nGrid cell size [smaller = more accurate, slower]: ")
            {
                DefaultValue = 2.0,
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = true
            };
            var res = ed.GetDouble(opts);
            return res.Status == PromptStatus.OK ? res.Value : 2.0;
        }

        private struct InletInfo
        {
            public ObjectId ObjectId;
            public string Name;
            public Point3d Position;
        }

        private List<InletInfo> ExtractInlets(Database db, ObjectId networkId)
        {
            var inlets = new List<InletInfo>();
            var extractor = new StructureExtractor(
                Application.DocumentManager.MdiActiveDocument);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var network = tr.GetObject(networkId, OpenMode.ForRead) as Network;
                if (network == null) return inlets;

                foreach (ObjectId sId in network.GetStructureIds())
                {
                    var s = tr.GetObject(sId, OpenMode.ForRead) as Structure;
                    if (s == null) continue;

                    // Use all structures (not just inlets) as potential outlets
                    // since on a designed site, all structures are pour points
                    inlets.Add(new InletInfo
                    {
                        ObjectId = sId,
                        Name = s.Name,
                        Position = s.Position
                    });
                }
                tr.Commit();
            }

            return inlets;
        }

        private string GetUnits(Database db)
        {
            return db.Insunits == UnitsValue.Meters ? "m" : "ft";
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
