using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public partial class CatchmentDialog : Window
    {
        private Document _doc;
        private Database _db;
        private List<SurfaceInfo> _surfaces;
        private List<NetworkInfo> _networks;
        
        public CatchmentDialog(Document doc)
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
            // Use WPF-style folder picker via a save file dialog trick
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
        
        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (cmbSurface.SelectedItem == null)
            {
                System.Windows.MessageBox.Show("Please select a surface.", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            if (cmbNetwork.SelectedItem == null)
            {
                System.Windows.MessageBox.Show("Please select a pipe network.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            var surfaceId = ((SurfaceInfo)cmbSurface.SelectedItem).Id;
            var networkId = ((NetworkInfo)cmbNetwork.SelectedItem).Id;
            string workingDir = txtWorkingDir.Text;
            string pythonPath = txtPythonPath.Text;
            
            btnRun.IsEnabled = false;
            txtLog.Clear();
            
            try
            {
                RunFullWorkflow(surfaceId, networkId, workingDir, pythonPath);
            }
            catch (System.Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                System.Windows.MessageBox.Show($"Error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnRun.IsEnabled = true;
            }
        }
        
        private void RunFullWorkflow(ObjectId surfaceId, ObjectId networkId, string workingDir, string pythonPath)
        {
            // Create working directory
            if (!Directory.Exists(workingDir))
            {
                Directory.CreateDirectory(workingDir);
                Log($"Created directory: {workingDir}");
            }
            
            // Step 1: Export surface
            UpdateStatus(20, "Step 1/4: Exporting surface...");
            
            var surfaceExporter = new SurfaceExporter(_doc);
            string demPath = Path.Combine(workingDir, "surface_dem");
            var surfaceResult = surfaceExporter.Export(surfaceId, demPath);
            
            if (!surfaceResult.Success)
            {
                throw new System.Exception("Failed to export surface");
            }
            Log($"Exported surface: {surfaceResult.SurfaceName}");
            Log($"  Dimensions: {surfaceResult.Columns} x {surfaceResult.Rows}");
            
            // Step 2: Export inlet structures
            UpdateStatus(35, "Step 2/4: Exporting inlet structures...");
            
            var structureExtractor = new StructureExtractor(_doc);
            string inletsPath = Path.Combine(workingDir, "inlet_structures");
            var structureResult = structureExtractor.Extract(networkId, inletsPath);
            
            Log($"Exported {structureResult.Structures.Count} inlet structures");
            
            // Step 3: Run Python processing
            UpdateStatus(50, "Step 3/4: Running catchment delineation...");
            Log("Converting files and running WhiteboxTools...");
            
            // Run conversion script
            string convertScript = CreateConversionScript(workingDir, surfaceResult);
            string convertOutput = RunPythonScript(pythonPath, convertScript);
            Log(convertOutput);
            
            // Run delineation
            string scriptPath = GetDelineationScriptPath();
            string demGeoPath = Path.Combine(workingDir, "surface_dem_geo.tif");
            string inletsGeoPath = Path.Combine(workingDir, "inlet_structures_geo.shp");
            string outputPath = Path.Combine(workingDir, "catchments.shp");
            string tempDir = Path.Combine(workingDir, "temp");
            
            Log($"Script: {scriptPath}");
            Log($"Script exists: {File.Exists(scriptPath)}");
            Log($"DEM exists: {File.Exists(demGeoPath)}");
            Log($"Inlets exist: {File.Exists(inletsGeoPath)}");
            Log("Starting WhiteboxTools...");
            
            string args = $"\"{scriptPath}\" --dem \"{demGeoPath}\" --inlets \"{inletsGeoPath}\" " +
                         $"--output \"{outputPath}\" --working-dir \"{tempDir}\"";
            
            Log($"Command: {pythonPath} {args}");
            
            string delineateOutput = RunPythonCommandSimple(pythonPath, args);
            
            Log("Python finished");
            
            if (!string.IsNullOrEmpty(delineateOutput))
            {
                // Only log last few lines
                var lines = delineateOutput.Split('\n');
                int start = Math.Max(0, lines.Length - 10);
                for (int i = start; i < lines.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(lines[i]))
                        Log(lines[i].Trim());
                }
            }
            
            // Step 4: Import catchments
            UpdateStatus(80, "Step 4/4: Importing catchments...");
            
            string catchmentsJson = Path.Combine(workingDir, "catchments.json");
            if (!File.Exists(catchmentsJson))
            {
                throw new System.Exception("Catchments file not created. Check Python output.");
            }
            
            var creator = new CatchmentCreator(_doc);
            var importResult = creator.ImportCatchments(catchmentsJson, networkId, surfaceId);
            
            UpdateStatus(100, "Complete!");
            Log($"\n=== COMPLETE ===");
            Log($"Catchments created: {importResult.CatchmentsCreated}");
            Log($"Linked to structures: {importResult.CatchmentsLinked}");
            
            System.Windows.MessageBox.Show(
                $"Catchment delineation complete!\n\n" +
                $"Catchments created: {importResult.CatchmentsCreated}\n" +
                $"Linked to structures: {importResult.CatchmentsLinked}",
                "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        
        private void UpdateStatus(int progress, string message)
        {
            progressBar.Value = progress;
            txtStatus.Text = message;
            Log(message);
            
            // Force UI update using WPF dispatcher
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        }
        
        private void Log(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            txtLog.AppendText(message + Environment.NewLine);
            txtLog.ScrollToEnd();
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        }
        
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
        
        private string CreateConversionScript(string workingDir, SurfaceExportResult surfaceResult)
        {
            // Escape backslashes for Python
            string escapedDir = workingDir.Replace("\\", "\\\\");
            
            return $@"
import rasterio
from rasterio.crs import CRS
from rasterio.transform import from_bounds
import geopandas as gpd
import numpy as np
import json
from pathlib import Path

base_dir = Path(r'{workingDir}')
bil_path = base_dir / 'surface_dem.bil'
json_meta_path = base_dir / 'surface_dem.json'
inlet_json_path = base_dir / 'inlet_structures.json'

with open(json_meta_path) as f:
    meta = json.load(f)

with rasterio.open(bil_path) as src:
    data = src.read(1)

cell_size = meta['cell_size']
origin_x = meta['origin_x']
origin_y = meta['origin_y']
cols = meta['cols']
rows = meta['rows']

min_x = origin_x
max_y = origin_y
max_x = min_x + (cols * cell_size)
min_y = max_y - (rows * cell_size)

transform = from_bounds(min_x, min_y, max_x, max_y, cols, rows)
crs = CRS.from_epsg(32613)

tif_path = base_dir / 'surface_dem_geo.tif'
profile = dict(driver='GTiff', dtype='float32', width=cols, height=rows, count=1, crs=crs, transform=transform, nodata=-9999.0, compress='lzw')
with rasterio.open(tif_path, 'w', **profile) as dst:
    dst.write(data.astype(np.float32), 1)
print('DEM converted')

gdf = gpd.read_file(inlet_json_path)
gdf = gdf.set_crs(crs, allow_override=True)
shp_path = base_dir / 'inlet_structures_geo.shp'
gdf.to_file(shp_path)
print('Inlets converted')
";
        }
        
        private string GetDelineationScriptPath()
        {
            string[] possiblePaths = new[]
            {
                @"C:\Users\hollinj\OneDrive - Autodesk\Documents\CursorAI\Civil3D\CatchmentTool\Python\catchment_delineation.py",
                Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), 
                            "..", "Python", "catchment_delineation.py")
            };
            
            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }
            
            return possiblePaths[0];
        }
        
        private string RunPythonScript(string pythonPath, string script)
        {
            try
            {
                // Write script to temp file
                string tempScript = Path.GetTempFileName();
                tempScript = Path.ChangeExtension(tempScript, ".py");
                File.WriteAllText(tempScript, script);
                
                var psi = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = $"\"{tempScript}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using (var process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(60000);
                    
                    File.Delete(tempScript);
                    
                    if (!string.IsNullOrEmpty(error) && !error.Contains("Warning"))
                        return output + "\nErrors: " + error;
                    return output;
                }
            }
            catch (System.Exception ex)
            {
                return $"Python error: {ex.Message}";
            }
        }
        
        private string RunPythonCommand(string pythonPath, string arguments)
        {
            return RunPythonCommandSimple(pythonPath, arguments);
        }
        
        private string RunPythonCommandSimple(string pythonPath, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using (var process = Process.Start(psi))
                {
                    // Simple approach - just wait with timeout
                    bool finished = process.WaitForExit(180000); // 3 minute timeout
                    
                    if (!finished)
                    {
                        try { process.Kill(); } catch { }
                        return "Process timed out after 3 minutes";
                    }
                    
                    string output = process.StandardOutput.ReadToEnd();
                    string errors = process.StandardError.ReadToEnd();
                    
                    if (!string.IsNullOrEmpty(errors))
                    {
                        // Filter out common warnings
                        var errorLines = errors.Split('\n')
                            .Where(l => !l.Contains("Warning") && !l.Contains("Deprecation") && !string.IsNullOrWhiteSpace(l))
                            .ToArray();
                        if (errorLines.Length > 0)
                        {
                            output += "\nErrors:\n" + string.Join("\n", errorLines);
                        }
                    }
                    
                    return output;
                }
            }
            catch (System.Exception ex)
            {
                return $"Python error: {ex.Message}";
            }
        }
    }
    
    public class SurfaceInfo
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
    }
    
    public class NetworkInfo
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
    }
}
