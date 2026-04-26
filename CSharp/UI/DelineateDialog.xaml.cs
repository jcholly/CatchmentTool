using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CatchmentTool.Services;
using CatchmentTool2;
using CatchmentTool2.Network;
using CatchmentTool2.Pipeline;
using CatchmentTool2.Surface;

namespace CatchmentTool.UI
{
    /// <summary>
    /// Pour-point selection dialog. Picks a TIN, a pipe network, and which
    /// structures to delineate as catchment outlets. Runs the tuned v2 pipeline
    /// (cell_size=13.2 ft, depression=Fill, pipe-burn 1.5 ft, edge off-site
    /// exclusion, topology-aware smoothing). Parameters are baked in — no UI.
    /// </summary>
    public partial class DelineateDialog : Window
    {
        private readonly Document _doc;
        private readonly Database _db;
        private readonly Editor _ed;
        private readonly DrawingUnits _units;
        private List<SurfaceItem> _surfaces;
        private List<NetworkItem> _networks;
        private ObservableCollection<StructureRow> _structures;

        public DelineateDialog(Document doc)
        {
            InitializeComponent();
            _doc = doc;
            _db = doc.Database;
            _ed = doc.Editor;
            _units = DrawingUnits.Detect(doc);
            _structures = new ObservableCollection<StructureRow>();
            lstStructures.ItemsSource = _structures;
            cmbNetwork.SelectionChanged += (s, e) => LoadStructures();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Log("Catchment Delineation — Tuned v2 Pipeline", Brushes.Cyan);
            Log("Pour-point selection only. Algorithm parameters are baked in from tuning.", Brushes.Gray);
            Log("");
            LoadSurfaces();
            LoadNetworks();
            LoadStructures();
        }

        private void LoadSurfaces()
        {
            _surfaces = new List<SurfaceItem>();
            var civilDoc = CivilApplication.ActiveDocument;
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civilDoc.GetSurfaceIds())
                {
                    var s = tr.GetObject(id, OpenMode.ForRead) as TinSurface;
                    if (s != null) _surfaces.Add(new SurfaceItem { Id = id, Name = s.Name });
                }
                tr.Commit();
            }
            cmbSurface.ItemsSource = _surfaces;
            cmbSurface.DisplayMemberPath = "Name";
            if (_surfaces.Count > 0) cmbSurface.SelectedIndex = 0;
            Log($"Found {_surfaces.Count} TIN surface(s).", Brushes.LightGray);
        }

        private void LoadNetworks()
        {
            _networks = new List<NetworkItem>();
            var civilDoc = CivilApplication.ActiveDocument;
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civilDoc.GetPipeNetworkIds())
                {
                    var n = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Network;
                    if (n != null) _networks.Add(new NetworkItem { Id = id, Name = n.Name });
                }
                tr.Commit();
            }
            cmbNetwork.ItemsSource = _networks;
            cmbNetwork.DisplayMemberPath = "Name";
            if (_networks.Count > 0) cmbNetwork.SelectedIndex = 0;
            Log($"Found {_networks.Count} pipe network(s).", Brushes.LightGray);
        }

        private void LoadStructures()
        {
            _structures.Clear();
            if (cmbNetwork.SelectedItem is not NetworkItem net) { UpdateCount(); return; }
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var network = (Autodesk.Civil.DatabaseServices.Network)tr.GetObject(net.Id, OpenMode.ForRead);
                var hasDownstream = new HashSet<string>();
                foreach (ObjectId pid in network.GetPipeIds())
                {
                    var p = (Autodesk.Civil.DatabaseServices.Pipe)tr.GetObject(pid, OpenMode.ForRead);
                    if (p.StartStructureId != ObjectId.Null)
                    {
                        var ss = (Autodesk.Civil.DatabaseServices.Structure)tr.GetObject(p.StartStructureId, OpenMode.ForRead);
                        hasDownstream.Add(ss.Handle.ToString());
                    }
                }
                foreach (ObjectId sid in network.GetStructureIds())
                {
                    var s = (Autodesk.Civil.DatabaseServices.Structure)tr.GetObject(sid, OpenMode.ForRead);
                    string handle = s.Handle.ToString();
                    bool isPond = !hasDownstream.Contains(handle);
                    _structures.Add(new StructureRow
                    {
                        Id = sid,
                        Handle = handle,
                        Name = s.Name,
                        Kind = isPond ? "Pond" : "Inlet",
                        Position = s.Position,
                        Rim = s.RimElevation,
                        IsSelected = true, // select-all default
                    });
                }
                tr.Commit();
            }
            UpdateCount();
            Log($"Loaded {_structures.Count} structure(s) ({_structures.Count(x => x.Kind == "Pond")} ponds, {_structures.Count(x => x.Kind == "Inlet")} inlets).", Brushes.LightGray);
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var s in _structures) s.IsSelected = true;
            UpdateCount();
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var s in _structures) s.IsSelected = false;
            UpdateCount();
        }

        private void BtnSelectPonds_Click(object sender, RoutedEventArgs e)
        {
            foreach (var s in _structures) s.IsSelected = (s.Kind == "Pond");
            UpdateCount();
        }

        private void UpdateCount()
        {
            int n = _structures.Count(x => x.IsSelected);
            txtStructureCount.Text = $"{n} structure(s) selected";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (cmbSurface.SelectedItem is not SurfaceItem surf) { Log("Pick a surface.", Brushes.Yellow); return; }
            if (cmbNetwork.SelectedItem is not NetworkItem net) { Log("Pick a pipe network.", Brushes.Yellow); return; }
            var selected = _structures.Where(s => s.IsSelected).ToList();
            if (selected.Count == 0) { Log("No pour-point structures selected.", Brushes.Yellow); return; }

            try
            {
                btnRun.IsEnabled = false;
                txtStatus.Text = "Running…";
                progress.Visibility = Visibility.Visible;
                progress.IsIndeterminate = true;
                Run(surf.Id, net.Id, selected);
            }
            catch (System.Exception ex)
            {
                Log($"ERROR: {ex.Message}", Brushes.Red);
            }
            finally
            {
                progress.Visibility = Visibility.Collapsed;
                txtStatus.Text = "Done.";
                btnRun.IsEnabled = true;
            }
        }

        private void Run(ObjectId surfaceId, ObjectId networkId, List<StructureRow> selected)
        {
            // Read the TIN + pipe network into Core types and run the pipeline.
            Tin tin;
            List<CatchmentTool2.Network.Structure> structures;
            CatchmentTool2.Network.PipeNetwork cnet;
            var handleToObjectId = new Dictionary<string, ObjectId>();
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var surf = (TinSurface)tr.GetObject(surfaceId, OpenMode.ForRead);
                tin = ReadTin(surf);
                (structures, cnet, handleToObjectId) = ReadNetwork(tr, networkId, selected);
                tr.Commit();
            }

            Log($"TIN: {tin.Vertices.Count:N0} vertices, {tin.Triangles.Count:N0} triangles", Brushes.LightGray);
            Log($"Network: {structures.Count} structures, {cnet.Pipes.Count} pipes", Brushes.LightGray);
            Log($"Selected pour-points: {structures.Count(s => s.UserSelected)}", Brushes.LightGray);

            var p = TunedDefaults.V2;
            var input = new PipelineInput(tin, structures, cnet, null);
            var result = CatchmentPipeline.Run(input, p);
            Log($"Delineation complete in {result.RuntimeSeconds:F1}s — {result.Catchments.Count} catchment(s)", Brushes.LightGreen);
            Log($"  Topo-assigned: {result.TopoAssignedCells:N0} cells", Brushes.LightGray);
            Log($"  Voronoi fallback: {result.FallbackAssignedCells:N0} cells", Brushes.LightGray);
            Log($"  Off-site / unassigned: {result.UnassignedCells:N0} cells", Brushes.LightGray);

            // Emit Civil 3D Catchment objects
            int created = CatchmentEmitter.Emit(_doc, surfaceId, networkId, structures, result, handleToObjectId, Log);
            Log($"Created {created} Civil 3D Catchment object(s).", Brushes.LightGreen);
        }

        // ---------- Helpers ----------

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

        private static (List<CatchmentTool2.Network.Structure>, CatchmentTool2.Network.PipeNetwork, Dictionary<string, ObjectId>)
            ReadNetwork(Transaction tr, ObjectId networkId, List<StructureRow> selectedRows)
        {
            var structures = new List<CatchmentTool2.Network.Structure>();
            var pipes = new List<CatchmentTool2.Network.Pipe>();
            var handleToId = new Dictionary<string, ObjectId>();
            var selectedHandles = selectedRows.Select(r => r.Handle).ToHashSet();

            var net = (Autodesk.Civil.DatabaseServices.Network)tr.GetObject(networkId, OpenMode.ForRead);
            foreach (ObjectId sid in net.GetStructureIds())
            {
                var s = (Autodesk.Civil.DatabaseServices.Structure)tr.GetObject(sid, OpenMode.ForRead);
                string handle = s.Handle.ToString();
                handleToId[handle] = sid;
                structures.Add(new CatchmentTool2.Network.Structure(
                    Id: handle,
                    Location: new Vec2(s.Position.X, s.Position.Y),
                    RimElevation: s.RimElevation,
                    Kind: CatchmentTool2.Network.StructureKind.Inlet,
                    UserSelected: selectedHandles.Contains(handle)));
            }
            foreach (ObjectId pid in net.GetPipeIds())
            {
                var p = (Autodesk.Civil.DatabaseServices.Pipe)tr.GetObject(pid, OpenMode.ForRead);
                string sid = p.StartStructureId == ObjectId.Null ? "" :
                    ((Autodesk.Civil.DatabaseServices.Structure)tr.GetObject(p.StartStructureId, OpenMode.ForRead)).Handle.ToString();
                string eid = p.EndStructureId == ObjectId.Null ? "" :
                    ((Autodesk.Civil.DatabaseServices.Structure)tr.GetObject(p.EndStructureId, OpenMode.ForRead)).Handle.ToString();
                if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(eid)) continue;
                pipes.Add(new CatchmentTool2.Network.Pipe(p.Handle.ToString(), sid, eid, p.StartPoint.Z, p.EndPoint.Z));
            }
            // Auto-classify Pond/Inlet by topology (no downstream pipe = Pond).
            var hasDown = pipes.Select(x => x.StartStructureId).ToHashSet();
            var classified = structures.Select(s => s with
            {
                Kind = hasDown.Contains(s.Id)
                    ? CatchmentTool2.Network.StructureKind.Inlet
                    : CatchmentTool2.Network.StructureKind.Pond,
            }).ToList();
            return (classified, new CatchmentTool2.Network.PipeNetwork(classified, pipes), handleToId);
        }

        private void Log(string msg, Brush color = null)
        {
            txtLog.Dispatcher.Invoke(() =>
            {
                var p = new Paragraph(new Run(msg) { Foreground = color ?? Brushes.White }) { Margin = new Thickness(0) };
                txtLog.Document.Blocks.Add(p);
                txtLog.ScrollToEnd();
            });
        }

        // ---------- Item types ----------
        private class SurfaceItem { public ObjectId Id; public string Name; public override string ToString() => Name; }
        private class NetworkItem { public ObjectId Id; public string Name; public override string ToString() => Name; }
    }

    public class StructureRow : INotifyPropertyChanged
    {
        public ObjectId Id { get; set; }
        public string Handle { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public Autodesk.AutoCAD.Geometry.Point3d Position { get; set; }
        public double Rim { get; set; }
        public string RimDisplay => double.IsNaN(Rim) ? "—" : Rim.ToString("0.00");

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
}
