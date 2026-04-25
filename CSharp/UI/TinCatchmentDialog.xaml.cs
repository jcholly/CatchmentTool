using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using CatchmentTool.Services;

namespace CatchmentTool.UI
{
    public class StructureItem : INotifyPropertyChanged
    {
        public ObjectId ObjectId { get; set; }
        public string Name { get; set; }
        public string PartFamilyName { get; set; }
        public string NetworkName { get; set; }
        public string DisplayName { get; set; }

        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string p = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public partial class TinCatchmentDialog : Window
    {
        private Document _doc;
        private Database _db;
        private Editor _ed;
        private bool _isRunning = false;
        private List<SurfaceInfo> _surfaces;
        private List<ObjectId> _networkIds;
        private ObservableCollection<StructureItem> _structures;
        private DrawingUnits _units;

        public TinCatchmentDialog(Document doc)
        {
            InitializeComponent();
            _doc = doc;
            _db = doc.Database;
            _ed = doc.Editor;
            _structures = new ObservableCollection<StructureItem>();
            _networkIds = new List<ObjectId>();

            _units = DrawingUnits.Detect(doc);

            lstStructures.ItemsSource = _structures;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Log("╔═══════════════════════════════════════╗", Brushes.Cyan);
            Log("║  TIN-Based Catchment Delineation    ║", Brushes.Cyan);
            Log("╚═══════════════════════════════════════╝", Brushes.Cyan);
            Log("");

            LoadTinSurfaces();
            LoadAllStructures();
        }

        private void LoadTinSurfaces()
        {
            _surfaces = new List<SurfaceInfo>();
            var civilDoc = CivilApplication.ActiveDocument;
            var surfaceIds = civilDoc.GetSurfaceIds();

            using (var tr = _db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in surfaceIds)
                {
                    var surface = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                    if (surface is TinSurface tinSurf)
                    {
                        _surfaces.Add(new SurfaceInfo
                        {
                            Id = id,
                            Name = surface.Name,
                            DisplayName = surface.Name
                        });
                    }
                }
                tr.Commit();
            }

            cmbSurface.ItemsSource = _surfaces;
            cmbSurface.DisplayMemberPath = "DisplayName";
            if (_surfaces.Count > 0) cmbSurface.SelectedIndex = 0;
        }

        private void LoadAllStructures()
        {
            _structures.Clear();
            _networkIds.Clear();

            try
            {
                var civilDoc = CivilApplication.ActiveDocument;
                var networkIds = civilDoc.GetPipeNetworkIds();

                using (var tr = _db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId netId in networkIds)
                    {
                        var network = tr.GetObject(netId, OpenMode.ForRead)
                                      as Autodesk.Civil.DatabaseServices.Network;
                        if (network == null) continue;

                        _networkIds.Add(netId);
                        string netName = network.Name;

                        try
                        {
                            var structureIds = network.GetStructureIds();
                            foreach (ObjectId id in structureIds)
                            {
                                var structure = tr.GetObject(id, OpenMode.ForRead)
                                                as Autodesk.Civil.DatabaseServices.Structure;
                                if (structure == null) continue;

                                string partFamily;
                                try { partFamily = structure.PartFamilyName ?? "Unknown"; }
                                catch { partFamily = "Unknown"; }

                                _structures.Add(new StructureItem
                                {
                                    ObjectId = id,
                                    Name = structure.Name,
                                    PartFamilyName = partFamily,
                                    NetworkName = netName,
                                    DisplayName = $"{structure.Name} ({partFamily})"
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Warning: could not load structures from {netName}: {ex.Message}", Brushes.Yellow);
                        }
                    }
                    tr.Commit();
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading pipe networks: {ex.GetType().Name}: {ex.Message}", Brushes.Red);
            }

            UpdateStructureCount();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _structures)
                item.IsSelected = true;
            UpdateStructureCount();
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _structures)
                item.IsSelected = false;
            UpdateStructureCount();
        }

        private void UpdateStructureCount()
        {
            int count = _structures.Count(s => s.IsSelected);
            txtStructureCount.Text = $"{count} / {_structures.Count} selected";
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
                return;

            // Validate inputs
            if (!(cmbSurface.SelectedItem is SurfaceInfo surfInfo))
            {
                MessageBox.Show("Please select a TIN surface", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedStructures = _structures.Where(s => s.IsSelected).ToList();
            if (selectedStructures.Count == 0)
            {
                MessageBox.Show("Please select at least one inlet structure", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(txtCellSize.Text, out double cellSize) || cellSize <= 0)
            {
                MessageBox.Show("Cell size must be a positive number", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!double.TryParse(txtSnapTolerance.Text, out double snapTol) || snapTol <= 0)
            {
                MessageBox.Show("Snap tolerance must be a positive number", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Cell-size safety check — warn before running a many-minute job.
            try
            {
                using (var tr = _db.TransactionManager.StartTransaction())
                {
                    var tinSurf = tr.GetObject(surfInfo.Id, OpenMode.ForRead) as TinSurface;
                    if (tinSurf != null)
                    {
                        var p = tinSurf.GetGeneralProperties();
                        long approxCols = (long)((p.MaximumCoordinateX - p.MinimumCoordinateX) / cellSize) + 1;
                        long approxRows = (long)((p.MaximumCoordinateY - p.MinimumCoordinateY) / cellSize) + 1;
                        long approxCells = approxRows * approxCols;
                        if (approxCells > 5_000_000)
                        {
                            double suggested = Math.Ceiling(cellSize
                                * Math.Sqrt(approxCells / 1_000_000.0));
                            var confirm = MessageBox.Show(
                                $"The grid will be about {approxCells:N0} cells at cell size "
                                + $"{cellSize:F1} {_units.UnitLabel}.\n\n"
                                + $"This may take 30+ minutes. A cell size of "
                                + $"about {suggested:F0} would reduce it to roughly 1M cells.\n\n"
                                + "Continue anyway?",
                                "Large Grid Warning",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning);
                            if (confirm != MessageBoxResult.Yes)
                                return;
                        }
                    }
                    tr.Commit();
                }
            }
            catch
            {
                // If we can't check, just proceed — user knows best.
            }

            // Disable UI and start async work
            _isRunning = true;
            btnRun.IsEnabled = false;
            btnClose.Content = "Cancel";

            rtbLog.Document.Blocks.Clear();
            Log("╔═══════════════════════════════════════╗", Brushes.Yellow);
            Log("║  RUNNING DELINEATION                ║", Brushes.Yellow);
            Log("╚═══════════════════════════════════════╝", Brushes.Yellow);
            Log("");

            // Run in background thread
            Task.Run(() => RunDelineation(surfInfo.Id, _networkIds.ToList(), selectedStructures, cellSize, snapTol));
        }

        private void RunDelineation(ObjectId surfaceId, List<ObjectId> networkIds, List<StructureItem> selectedStructures, double cellSize, double snapTolerance)
        {
            try
            {
                TinSurface surface = null;

                using (var tr = _db.TransactionManager.StartTransaction())
                {
                    surface = tr.GetObject(surfaceId, OpenMode.ForRead) as TinSurface;
                    tr.Commit();
                }

                if (surface == null)
                {
                    Log("Error: Could not open surface", Brushes.Red);
                    return;
                }

                // Extract inlet structures (only selected ones)
                Log($"[1/5] Extracting {selectedStructures.Count} inlet structures...");
                var inlets = new List<StructureData>();
                using (var tr = _db.TransactionManager.StartTransaction())
                {
                    foreach (var structItem in selectedStructures)
                    {
                        var structure = tr.GetObject(structItem.ObjectId, OpenMode.ForRead) as Structure;
                        if (structure != null)
                        {
                            inlets.Add(new StructureData
                            {
                                ObjectId = structItem.ObjectId,
                                Name = structure.Name,
                                Position = structure.Location,
                            });
                        }
                    }
                    tr.Commit();
                }

                if (inlets.Count == 0)
                {
                    Log("No valid inlet structures found. Aborting.", Brushes.Red);
                    return;
                }

                Log($"  Found {inlets.Count} inlets", Brushes.LightGreen);
                Log("");

                // Build TinWalker inlets
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

                // Extract pipe segments from the network for burning into
                // the hierarchy grid. Pipes give upstream inlets a low-elevation
                // corridor so priority-flood respects the designed drainage
                // instead of letting the lowest collector dominate.
                var pipeSegments = new List<PipeBurner.PipeSegment>();
                try
                {
                    using (var tr = _db.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId netId in networkIds)
                        {
                            var net = tr.GetObject(netId, OpenMode.ForRead)
                                      as Autodesk.Civil.DatabaseServices.Network;
                            if (net == null) continue;
                            try
                            {
                                var pipeIds = net.GetPipeIds();
                                foreach (ObjectId pipeId in pipeIds)
                                {
                                    var pipe = tr.GetObject(pipeId, OpenMode.ForRead)
                                               as Autodesk.Civil.DatabaseServices.Pipe;
                                    if (pipe == null) continue;
                                    pipeSegments.Add(new PipeBurner.PipeSegment
                                    {
                                        StartX = pipe.StartPoint.X,
                                        StartY = pipe.StartPoint.Y,
                                        StartInvert = pipe.StartPoint.Z,
                                        EndX = pipe.EndPoint.X,
                                        EndY = pipe.EndPoint.Y,
                                        EndInvert = pipe.EndPoint.Z,
                                    });
                                }
                            }
                            catch { /* skip networks whose pipes can't be read */ }
                        }
                        tr.Commit();
                    }
                }
                catch (Exception ex)
                {
                    Log($"Warning: Could not extract pipes: {ex.Message}", Brushes.Yellow);
                    pipeSegments.Clear();
                }

                // Build spillover hierarchy first — priority-flood from inlets
                // so we can resolve stuck drops via rising-water routing
                // instead of the old nearest-inlet guess.
                Log($"[2/5] Building spillover hierarchy (priority-flood from inlets)...");
                Log($"  Burning {pipeSegments.Count} pipes into elevation grid");
                var hierarchy = new BasinHierarchy(
                    surface, walkerInlets, cellSize, pipeSegments);
                double hierarchyTime = hierarchy.Build();
                Log($"  Grid: {hierarchy.Cols} x {hierarchy.Rows} = {hierarchy.Rows * hierarchy.Cols:N0} cells");
                Log($"  Pipe-burned cells: {hierarchy.BurnedCells:N0}");
                Log($"  Light-conditioned (filled micro-sinks): "
                    + $"{hierarchy.RaisedCells:N0} cells");
                Log($"  Labelled: {hierarchy.LabelledCells:N0}  Orphan: {hierarchy.OrphanCells:N0}");
                Log($"  Time: {hierarchyTime:F1} seconds", Brushes.LightGreen);
                Log("");

                // Build watershed grid (now with spillover fallback)
                Log($"[3/5] Tracing flow across TIN surface...");
                Log($"  Cell size: {cellSize:F1} {_units.UnitLabel}, Snap tolerance: {snapTolerance:F1} {_units.UnitLabel}");
                var walker = new TinWalker(surface, walkerInlets, snapTolerance);
                var grid = new WatershedGrid(surface, walker, cellSize);

                var sw = Stopwatch.StartNew();
                int prevProgress = 0;
                double elapsed = grid.Build((done, total) =>
                {
                    int newProgress = (int)((done * 100) / total);
                    if (newProgress > prevProgress)
                    {
                        SetProgress(newProgress, $"{done:N0} / {total:N0} cells");
                        prevProgress = newProgress;
                    }
                }, hierarchy);

                int resolved = grid.DirectCells + grid.SpilloverCells;
                double pctDirect    = resolved == 0 ? 0 : 100.0 * grid.DirectCells / resolved;
                double pctSpillover = resolved == 0 ? 0 : 100.0 * grid.SpilloverCells / resolved;
                Log($"  Grid: {grid.Cols} x {grid.Rows} = {grid.TotalCells:N0} cells");
                Log($"  Resolved to inlets: {resolved:N0}");
                Log($"    Direct (walker arrived)   : {grid.DirectCells:N0}  ({pctDirect:F1}%)", Brushes.LightGreen);
                Log($"    Spillover (rising water)  : {grid.SpilloverCells:N0}  ({pctSpillover:F1}%)",
                    grid.SpilloverCells > resolved * 0.3 ? Brushes.Orange : Brushes.LightGreen);
                if (grid.OffSurfaceCells > 0)
                    Log($"    Off-surface              : {grid.OffSurfaceCells:N0}", Brushes.Orange);
                if (grid.OrphanCells > 0)
                    Log($"    Orphan (no inlet reach)  : {grid.OrphanCells:N0}", Brushes.Red);
                Log($"  Time: {elapsed:F1} seconds", Brushes.LightGreen);
                Log("");

                // Post-processing: resolve orphan cells + merge tiny fragments.
                // Ensures every inside-TIN cell ends up in a catchment
                // (no holes in the map) and removes stair-step noise from
                // cells-at-hull-edges getting cut off during flood.
                int nRescued = grid.ResolveOrphansToNearestInlet(walkerInlets);
                if (nRescued > 0)
                    Log($"  Orphan cells reassigned to nearest inlet: {nRescued:N0}",
                        Brushes.LightGreen);
                const double MIN_CATCHMENT_SQFT = 500.0;
                int minCells = Math.Max(1,
                    (int)(MIN_CATCHMENT_SQFT / (cellSize * cellSize)));
                int nMerged = grid.MergeSmallComponents(minCells);
                if (nMerged > 0)
                    Log($"  Small fragments merged into neighbors: {nMerged:N0} "
                        + $"(< {MIN_CATCHMENT_SQFT:F0} sqft)");
                // Second orphan pass — tiny isolated blobs left behind.
                int nRescued2 = grid.ResolveOrphansToNearestInlet(walkerInlets);
                if (nRescued2 > 0)
                    Log($"  Orphan cells reassigned (pass 2): {nRescued2:N0}");
                Log("");

                // Build per-component catchment polygons. One inlet may own
                // several polygons when its territory is split by buildings
                // (TIN holes) — each fragment becomes its own catchment.
                Log($"[4/5] Building catchment polygons...");
                var polygons = grid.GetCatchmentPolygons(minCells: 1);
                Log($"  {polygons.Count} catchment polygons created "
                    + $"(across {polygons.Select(p => p.InletId).Distinct().Count()} inlets)",
                    Brushes.LightGreen);
                Log("");

                // Create Civil 3D catchment objects
                Log($"[5/5] Creating Civil 3D Catchment objects...");
                SetProgress(90, "Creating catchments...");

                var creator = new CatchmentCreator(_doc);
                var polygonInfos = new List<CatchmentPolygonInfo>();

                foreach (var poly in polygons)
                {
                    int inletIdx = poly.InletId;
                    if (inletIdx < 0 || inletIdx >= inlets.Count
                        || poly.Ring == null || poly.Ring.Count < 3)
                        continue;

                    var inlet = inlets[inletIdx];
                    double area = ComputeArea(poly.Ring);
                    Log($"  {inlet.Name}: {poly.Ring.Count} vertices, "
                        + $"{area:N0} sq {_units.UnitLabel}");

                    polygonInfos.Add(new CatchmentPolygonInfo
                    {
                        BoundaryPoints = poly.Ring,
                        StructureName = inlet.Name,
                        StructureObjectId = inlet.ObjectId,
                        StructureX = inlet.Position.X,
                        StructureY = inlet.Position.Y,
                        Area = area,
                    });
                }

                var result = creator.CreateCatchmentsFromPolylines(polygonInfos, surfaceId);

                Log("");
                Log("╔═══════════════════════════════════════╗", Brushes.LightGreen);
                Log($"║  {result.CatchmentsCreated} catchments created successfully    ║", Brushes.LightGreen);
                Log("╚═══════════════════════════════════════╝", Brushes.LightGreen);
                Log("");

                if (result.Errors.Count > 0)
                {
                    Log("Warnings:", Brushes.Orange);
                    foreach (var err in result.Errors)
                        Log($"  - {err}", Brushes.Orange);
                    Log("");
                }

                SetProgress(100, "Complete");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}", Brushes.Red);
                Log(ex.StackTrace, Brushes.Red);
                SetProgress(0, "Error");
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    _isRunning = false;
                    btnRun.IsEnabled = true;
                    btnClose.Content = "Close";
                });
            }
        }

        private void Log(string message, Brush color = null)
        {
            Dispatcher.Invoke(() =>
            {
                var para = new Paragraph(new Run(message))
                {
                    Foreground = color ?? new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC)),
                    Margin = new Thickness(0),
                    LineHeight = 1
                };
                rtbLog.Document.Blocks.Add(para);
                rtbLog.ScrollToEnd();
            });
        }

        private void SetProgress(int percent, string status)
        {
            Dispatcher.Invoke(() =>
            {
                progressBar.Value = Math.Max(0, Math.Min(100, percent));
                txtStatus.Text = status;
            });
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                // TODO: Implement cancellation if needed
                _isRunning = false;
            }
            Close();
        }

        private double ComputeArea(List<Point2d> points)
        {
            if (points.Count < 3) return 0;

            double area = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var p1 = points[i];
                var p2 = points[(i + 1) % points.Count];
                area += p1.X * p2.Y - p2.X * p1.Y;
            }
            return Math.Abs(area) / 2.0;
        }

        private class StructureData
        {
            public ObjectId ObjectId { get; set; }
            public string Name { get; set; }
            public Point3d Position { get; set; }
        }

        private class SurfaceInfo
        {
            public ObjectId Id { get; set; }
            public string Name { get; set; }
            public string DisplayName { get; set; }
        }

        private class NetworkInfo
        {
            public ObjectId Id { get; set; }
            public string Name { get; set; }
            public string DisplayName { get; set; }
        }
    }
}
