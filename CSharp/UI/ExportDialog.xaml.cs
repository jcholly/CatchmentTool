using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CatchmentTool.Services;

namespace CatchmentTool.UI
{
    public partial class ExportDialog : Window
    {
        private Document _doc;
        private Database _db;
        private List<SurfaceInfo> _surfaces;
        private List<NetworkInfo> _networks;
        private List<StructureTypeInfo> _structureTypes;
        
        public string ExportedWorkingDir { get; private set; }
        
        public ExportDialog(Document doc)
        {
            InitializeComponent();
            _doc = doc;
            _db = doc.Database;
            
            LoadSurfaces();
            LoadNetworks();
            SetDefaultWorkingDir();
        }
        
        private void LoadSurfaces()
        {
            _surfaces = new List<SurfaceInfo>();
            var civilDoc = CivilApplication.ActiveDocument;
            var surfaceIds = civilDoc.GetSurfaceIds();
            
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in surfaceIds)
                {
                    var surface = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                    if (surface != null)
                    {
                        string type = surface is TinSurface ? "TIN" : "Other";
                        _surfaces.Add(new SurfaceInfo 
                        { 
                            Id = id, 
                            Name = surface.Name,
                            DisplayName = $"{surface.Name} ({type})"
                        });
                    }
                }
                tr.Commit();
            }
            
            cmbSurface.ItemsSource = _surfaces;
            cmbSurface.DisplayMemberPath = "DisplayName";
            if (_surfaces.Count > 0) cmbSurface.SelectedIndex = 0;
        }
        
        private void LoadNetworks()
        {
            _networks = new List<NetworkInfo>();
            var civilDoc = CivilApplication.ActiveDocument;
            var networkIds = civilDoc.GetPipeNetworkIds();
            
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in networkIds)
                {
                    var network = tr.GetObject(id, OpenMode.ForRead) as Network;
                    if (network != null)
                    {
                        int structs = network.GetStructureIds().Count;
                        _networks.Add(new NetworkInfo
                        {
                            Id = id,
                            Name = network.Name,
                            DisplayName = $"{network.Name} ({structs} structures)"
                        });
                    }
                }
                tr.Commit();
            }
            
            cmbNetwork.ItemsSource = _networks;
            cmbNetwork.DisplayMemberPath = "DisplayName";
            if (_networks.Count > 0) cmbNetwork.SelectedIndex = 0;
        }
        
        private void CmbNetwork_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            LoadStructureTypes();
        }
        
        private void LoadStructureTypes()
        {
            _structureTypes = new List<StructureTypeInfo>();
            
            if (cmbNetwork.SelectedItem == null)
            {
                lstStructureTypes.ItemsSource = null;
                UpdateStructureCount();
                return;
            }
            
            var networkId = ((NetworkInfo)cmbNetwork.SelectedItem).Id;
            var structuresByType = new Dictionary<string, int>();
            
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var network = tr.GetObject(networkId, OpenMode.ForRead) as Network;
                if (network != null)
                {
                    foreach (ObjectId structId in network.GetStructureIds())
                    {
                        var structure = tr.GetObject(structId, OpenMode.ForRead) as Structure;
                        if (structure != null)
                        {
                            // Use the same logic as StructureExtractor.GetPartFamilyName
                            string partFamily = GetPartFamilyName(structure);
                            if (structuresByType.ContainsKey(partFamily))
                                structuresByType[partFamily]++;
                            else
                                structuresByType[partFamily] = 1;
                        }
                    }
                }
                tr.Commit();
            }
            
            // Create structure type items sorted by name
            foreach (var kvp in structuresByType.OrderBy(x => x.Key))
            {
                _structureTypes.Add(new StructureTypeInfo
                {
                    PartFamilyName = kvp.Key,
                    Count = kvp.Value,
                    DisplayName = $"{kvp.Key} ({kvp.Value})",
                    IsSelected = true  // Default to selected
                });
            }
            
            lstStructureTypes.ItemsSource = _structureTypes;
            UpdateStructureCount();
        }
        
        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (_structureTypes != null)
            {
                foreach (var st in _structureTypes)
                    st.IsSelected = true;
                lstStructureTypes.Items.Refresh();
                UpdateStructureCount();
            }
        }
        
        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            if (_structureTypes != null)
            {
                foreach (var st in _structureTypes)
                    st.IsSelected = false;
                lstStructureTypes.Items.Refresh();
                UpdateStructureCount();
            }
        }
        
        private void StructureType_CheckChanged(object sender, RoutedEventArgs e)
        {
            UpdateStructureCount();
        }
        
        private void UpdateStructureCount()
        {
            if (_structureTypes == null || _structureTypes.Count == 0)
            {
                txtStructureCount.Text = "No structures found";
                return;
            }
            
            int selectedTypes = _structureTypes.Count(s => s.IsSelected);
            int selectedStructures = _structureTypes.Where(s => s.IsSelected).Sum(s => s.Count);
            int totalStructures = _structureTypes.Sum(s => s.Count);
            
            txtStructureCount.Text = $"{selectedStructures} of {totalStructures} structures selected ({selectedTypes} types)";
        }
        
        private void SetDefaultWorkingDir()
        {
            string drawingPath = _doc.Name;
            if (!string.IsNullOrEmpty(drawingPath) && File.Exists(drawingPath))
            {
                txtWorkingDir.Text = Path.Combine(Path.GetDirectoryName(drawingPath), "CatchmentData");
            }
            else
            {
                txtWorkingDir.Text = @"C:\Temp\CatchmentData";
            }
        }
        
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Select Working Directory",
                FileName = "Select Folder",
                Filter = "Folder|*.",
                InitialDirectory = Directory.Exists(txtWorkingDir.Text) ? txtWorkingDir.Text : @"C:\"
            };
            
            if (dialog.ShowDialog() == true)
            {
                txtWorkingDir.Text = Path.GetDirectoryName(dialog.FileName);
            }
        }
        
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (cmbSurface.SelectedItem == null)
            {
                MessageBox.Show("Please select a surface.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            if (cmbNetwork.SelectedItem == null)
            {
                MessageBox.Show("Please select a pipe network.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            // Check that at least one structure type is selected
            var selectedTypes = _structureTypes?.Where(s => s.IsSelected).Select(s => s.PartFamilyName).ToList() 
                               ?? new List<string>();
            
            if (selectedTypes.Count == 0)
            {
                MessageBox.Show("Please select at least one structure type to include as inlets.", 
                               "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            var surfaceId = ((SurfaceInfo)cmbSurface.SelectedItem).Id;
            var networkId = ((NetworkInfo)cmbNetwork.SelectedItem).Id;
            string workingDir = txtWorkingDir.Text;
            
            btnExport.IsEnabled = false;
            txtLog.Clear();
            
            try
            {
                RunExport(surfaceId, networkId, workingDir, selectedTypes);
            }
            catch (System.Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnExport.IsEnabled = true;
            }
        }
        
        private void RunExport(ObjectId surfaceId, ObjectId networkId, string workingDir, List<string> selectedStructureTypes)
        {
            // Create working directory
            if (!Directory.Exists(workingDir))
            {
                Directory.CreateDirectory(workingDir);
                Log($"Created directory: {workingDir}");
            }
            
            // Force UI update
            DoEvents();
            
            // Export surface with progress callback
            Log("Exporting surface (this may take a while for large surfaces)...");
            DoEvents();
            
            var surfaceExporter = new SurfaceExporter(_doc);
            surfaceExporter.ProgressCallback = (message) =>
            {
                Log($"  {message}");
                DoEvents();
            };
            
            string demPath = Path.Combine(workingDir, "surface_dem");
            var surfaceResult = surfaceExporter.Export(surfaceId, demPath);
            
            if (!surfaceResult.Success)
            {
                throw new System.Exception("Failed to export surface");
            }
            Log($"  Surface: {surfaceResult.SurfaceName}");
            Log($"  Dimensions: {surfaceResult.Columns} x {surfaceResult.Rows}");
            Log($"  Cell size: {surfaceResult.CellSize:F4}");
            DoEvents();
            
            // Export inlet structures (filtered by selected types)
            Log($"Exporting structures ({selectedStructureTypes.Count} types selected)...");
            foreach (var t in selectedStructureTypes.Take(5))
                Log($"  - {t}");
            if (selectedStructureTypes.Count > 5)
                Log($"  - ... and {selectedStructureTypes.Count - 5} more");
            DoEvents();
            
            var structureExtractor = new StructureExtractor(_doc);
            string inletsPath = Path.Combine(workingDir, "inlet_structures");
            var structureResult = structureExtractor.ExtractByTypes(networkId, inletsPath, selectedStructureTypes);
            
            Log($"  Exported {structureResult.Structures.Count} structures as inlets");
            
            ExportedWorkingDir = workingDir;
            
            Log("");
            Log("=== EXPORT COMPLETE ===");
            Log($"Working directory: {workingDir}");
            Log("");
            Log("Next: Ask Cursor to run the Python script with this path.");
            
            MessageBox.Show(
                $"Export complete!\n\n" +
                $"Working directory:\n{workingDir}\n\n" +
                $"Surface: {surfaceResult.SurfaceName}\n" +
                $"Inlet structures: {structureResult.Structures.Count}\n\n" +
                "Now ask Cursor to run the Python catchment delineation script.",
                "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        
        /// <summary>
        /// Process pending UI messages to keep the window responsive.
        /// </summary>
        private void DoEvents()
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(delegate { }));
        }
        
        private void Log(string message)
        {
            txtLog.AppendText(message + Environment.NewLine);
            txtLog.ScrollToEnd();
        }
        
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
        
        /// <summary>
        /// Get the part family name for a structure (same logic as StructureExtractor).
        /// </summary>
        private string GetPartFamilyName(Structure structure)
        {
            try
            {
                // Try direct property first
                if (!string.IsNullOrEmpty(structure.PartFamilyName))
                {
                    return structure.PartFamilyName;
                }
                
                // Try to get part family name from PartData
                var partData = structure.PartData;
                if (partData != null)
                {
                    try
                    {
                        var familyProp = partData.GetType().GetProperty("PartFamilyName") 
                                      ?? partData.GetType().GetProperty("FamilyName")
                                      ?? partData.GetType().GetProperty("PartFamily");
                        if (familyProp != null)
                        {
                            var value = familyProp.GetValue(partData);
                            if (value != null) return value.ToString();
                        }
                        
                        var descProp = partData.GetType().GetProperty("PartDescription");
                        if (descProp != null)
                        {
                            var value = descProp.GetValue(partData);
                            if (value != null) return value.ToString();
                        }
                    }
                    catch { }
                }
                
                // Fallback to part size name
                if (!string.IsNullOrEmpty(structure.PartSizeName))
                {
                    return structure.PartSizeName;
                }
            }
            catch { }
            
            return "Unknown";
        }
    }
    
    /// <summary>
    /// Info about a structure type (part family) with selection state.
    /// </summary>
    public class StructureTypeInfo : INotifyPropertyChanged
    {
        private bool _isSelected;
        
        public string PartFamilyName { get; set; }
        public int Count { get; set; }
        public string DisplayName { get; set; }
        
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }
        
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
