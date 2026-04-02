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
            var ed = doc.Editor;
            var db = doc.Database;

            try
            {
                ed.WriteMessage("\n╔══════════════════════════════════════════════╗");
                ed.WriteMessage("\n║   TIN-BASED CATCHMENT DELINEATION           ║");
                ed.WriteMessage("\n║   No rasterization — works on exact TIN     ║");
                ed.WriteMessage("\n╚══════════════════════════════════════════════╝\n");

                // 1 — Select TIN surface
                ObjectId surfaceId = SelectTinSurface(ed, db);
                if (surfaceId.IsNull) return;

                // 2 — Select pipe network
                ObjectId networkId = SelectNetwork(ed, db);
                if (networkId.IsNull) return;

                // 3 — Get cell size
                double cellSize = PromptCellSize(ed);
                if (cellSize <= 0) return;

                // 4 — Extract inlets
                ed.WriteMessage("\n[1/4] Extracting inlet structures...\n");
                var inlets = ExtractInlets(db, networkId);
                if (inlets.Count == 0)
                {
                    ed.WriteMessage("  No inlet structures found. Aborting.\n");
                    return;
                }
                ed.WriteMessage($"  Found {inlets.Count} inlets\n");

                // 5 — Build TinWalker
                TinSurface surface;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    surface = tr.GetObject(surfaceId, OpenMode.ForRead) as TinSurface;
                    tr.Commit();
                }

                var walkerInlets = new List<TinWalker.InletTarget>();
                for (int i = 0; i < inlets.Count; i++)
                {
                    walkerInlets.Add(new TinWalker.InletTarget
                    {
                        Id = i,
                        X = inlets[i].Position.X,
                        Y = inlets[i].Position.Y,
                        Z = inlets[i].Position.Z,
                        Name = inlets[i].Name
                    });
                }

                double snapTol = cellSize * 3;
                var walker = new TinWalker(surface, walkerInlets, snapTol);

                // 6 — Build watershed grid
                ed.WriteMessage("\n[2/4] Tracing flow across TIN surface...\n");
                var grid = new WatershedGrid(surface, walker, cellSize);

                var sw = Stopwatch.StartNew();
                double elapsed = grid.Build((done, total) =>
                {
                    ed.WriteMessage($"\r  {done:N0} / {total:N0} cells traced...");
                });

                ed.WriteMessage($"\n  Grid: {grid.Cols} x {grid.Rows} = {grid.TotalCells:N0} cells\n");
                ed.WriteMessage($"  Traced: {grid.TracedCells:N0} cells to inlets\n");
                if (grid.UnresolvedCells > 0)
                    ed.WriteMessage($"  Unresolved: {grid.UnresolvedCells:N0} cells (assigned to nearest inlet)\n");
                ed.WriteMessage($"  Time: {elapsed:F1} seconds\n");

                // 7 — Build catchment polygons
                ed.WriteMessage("\n[3/4] Building catchment polygons...\n");
                var boundaries = grid.GetCatchmentBoundaries();
                ed.WriteMessage($"  {boundaries.Count} catchment polygons created\n");

                // 8 — Create Civil 3D catchment objects with outlet structures
                ed.WriteMessage("\n[4/4] Creating Civil 3D Catchment objects...\n");

                var creator = new CatchmentCreator(doc);
                var polygonInfos = new List<CatchmentPolygonInfo>();
                foreach (var kvp in boundaries)
                {
                    int inletIdx = kvp.Key;
                    if (inletIdx < 0 || inletIdx >= inlets.Count || kvp.Value.Count < 3)
                        continue;

                    var inlet = inlets[inletIdx];
                    ed.WriteMessage($"  {inlet.Name}: {kvp.Value.Count} vertices, " +
                                    $"{ComputeArea(kvp.Value):N0} sq {GetUnits(db)}\n");

                    polygonInfos.Add(new CatchmentPolygonInfo
                    {
                        BoundaryPoints = kvp.Value,
                        StructureName = inlet.Name,
                        StructureObjectId = inlet.ObjectId,
                        StructureX = inlet.Position.X,
                        StructureY = inlet.Position.Y,
                        Area = ComputeArea(kvp.Value),
                    });
                }

                var result = creator.CreateCatchmentsFromPolylines(polygonInfos, surfaceId);

                ed.WriteMessage($"\n╔══════════════════════════════════════════════╗");
                ed.WriteMessage($"\n║   COMPLETE: {result.CatchmentsCreated} catchments created            ║");
                ed.WriteMessage($"\n║   Time: {elapsed:F1}s  Grid: {cellSize} {GetUnits(db)}          ║");
                ed.WriteMessage($"\n╚══════════════════════════════════════════════╝\n");

                foreach (var err in result.Errors)
                    ed.WriteMessage($"  Warning: {err}\n");

                // Optionally save GeoJSON for debugging
                string tempDir = Path.Combine(Path.GetTempPath(), "CatchmentTin");
                Directory.CreateDirectory(tempDir);
                var inletNames = new Dictionary<int, string>();
                for (int i = 0; i < inlets.Count; i++)
                    inletNames[i] = inlets[i].Name;
                string geoJson = grid.ExportGeoJson(inletNames);
                string geoJsonPath = Path.Combine(tempDir, "catchments_tin.geojson");
                File.WriteAllText(geoJsonPath, geoJson);
                ed.WriteMessage($"\n  GeoJSON saved: {geoJsonPath}\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nError: {ex.Message}\n");
                ed.WriteMessage($"\n{ex.StackTrace}\n");
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
