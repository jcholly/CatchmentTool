using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CatchmentTool.Services;

namespace CatchmentTool.UI
{
    /// <summary>
    /// Settings for catchment delineation analysis.
    /// </summary>
    public class DelineationSettings
    {
        public double CellSize { get; set; } = 1.0;
        public double SnapDistance { get; set; } = 10.0;
        public int BreachDistance { get; set; } = 10;
        public string DepressionMethod { get; set; } = "breach";
        public double MinCatchmentArea { get; set; } = 100.0;
        public bool BurnPipes { get; set; } = true;
        public double BurnDepth { get; set; } = 3.0;
    }

    public partial class AutoCatchmentDialog : Window
    {
        private Document _doc;
        private Database _db;
        private List<SurfaceInfo> _surfaces;
        private List<NetworkInfo> _networks;
        private List<StructureTypeInfo> _structureTypes;
        private bool _isRunning = false;
        private PythonEnvironment _pythonEnv;
        private DrawingUnits _units;

        public AutoCatchmentDialog(Document doc)
        {
            InitializeComponent();
            _doc = doc;
            _db = doc.Database;

            // Detect drawing units and apply to UI
            _units = DrawingUnits.Detect(doc);
            ApplyUnitLabels();

            LoadSurfaces();
            LoadNetworks();

            // Discover Python in the background
            ValidatePythonEnvironment();
        }

        /// <summary>
        /// Update all UI labels and defaults based on detected drawing units.
        /// </summary>
        private void ApplyUnitLabels()
        {
            // Update cell size combo items
            cmbCellSize.Items.Clear();
            foreach (var opt in _units.GetCellSizeOptions())
            {
                var item = new ComboBoxItem { Content = opt.Display, Tag = opt.Tag };
                if (opt.IsDefault) item.IsSelected = true;
                cmbCellSize.Items.Add(item);
            }

            // Update text labels via Tag-based lookup or direct name
            // The XAML labels reference units — update them dynamically
            lblCellSize.Text = $"DEM Cell Size ({_units.UnitLabel}):";
            lblSnapDistance.Text = $"Snap Distance ({_units.UnitLabel}):";
            lblMinArea.Text = $"Min Catchment Area ({_units.AreaLabel}):";

            lblBurnDepth.Text = $"Burn Depth ({_units.UnitLabel}):";

            // Set defaults
            txtSnapDistance.Text = _units.DefaultSnapDistance.ToString();
            txtMinArea.Text = _units.DefaultMinArea.ToString();

            txtBurnDepth.Text = _units.DefaultBurnDepth.ToString();
        }

        /// <summary>
        /// Check that Python 3 and required packages are available.
        /// Shows a warning banner in the log area if something is missing.
        /// </summary>
        private void ValidatePythonEnvironment()
        {
            _pythonEnv = new PythonEnvironment();
            _pythonEnv.StatusCallback = (msg) => Log($"  {msg}");

            Log("Checking Python environment...");

            if (!_pythonEnv.Discover())
            {
                if (_pythonEnv.PythonPath == null)
                {
                    Log("  Python 3 not found on this machine.", Brushes.Red);
                    Log("  Install Python 3.10+ from https://www.python.org/downloads/", Brushes.Red);
                    Log("  Make sure 'Add Python to PATH' is checked during install.", Brushes.Red);
                }
                else
                {
                    Log("  Delineation script not found.", Brushes.Red);
                    Log("  Reinstall the CatchmentTool bundle.", Brushes.Red);
                }
                Log("");
                return;
            }

            Log($"  {_pythonEnv.PythonVersion}", Brushes.LightGreen);
            Log($"  Script: {_pythonEnv.ScriptPath}", Brushes.LightGreen);

            // Check packages
            var missing = _pythonEnv.CheckMissingPackages();
            if (missing.Count > 0)
            {
                Log($"  Missing packages: {string.Join(", ", missing)}", Brushes.Orange);
                Log("  These will be installed automatically when you run the tool.", Brushes.Orange);
            }
            else
            {
                Log("  All required packages installed.", Brushes.LightGreen);
            }
            Log("");
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

        private void CmbNetwork_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
                            string partFamily = structure.PartFamilyName ?? "Unknown";
                            if (structuresByType.ContainsKey(partFamily))
                                structuresByType[partFamily]++;
                            else
                                structuresByType[partFamily] = 1;
                        }
                    }
                }
                tr.Commit();
            }

            foreach (var kvp in structuresByType.OrderBy(x => x.Key))
            {
                _structureTypes.Add(new StructureTypeInfo
                {
                    PartFamilyName = kvp.Key,
                    Count = kvp.Value,
                    DisplayName = $"{kvp.Key} ({kvp.Value})",
                    IsSelected = true
                });
            }

            lstStructureTypes.ItemsSource = _structureTypes;
            UpdateStructureCount();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (_structureTypes != null)
            {
                foreach (var st in _structureTypes) st.IsSelected = true;
                lstStructureTypes.Items.Refresh();
                UpdateStructureCount();
            }
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            if (_structureTypes != null)
            {
                foreach (var st in _structureTypes) st.IsSelected = false;
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

        private double GetCellSize()
        {
            if (cmbCellSize.SelectedItem is ComboBoxItem item)
            {
                if (double.TryParse(item.Tag?.ToString(), out double val))
                    return val;
            }
            return _units.DefaultCellSize;
        }

        private string GetDepressionMethod()
        {
            if (cmbDepressionMethod.SelectedItem is ComboBoxItem item)
            {
                return item.Tag?.ToString() ?? "breach";
            }
            return "breach";
        }

        private double GetDoubleValue(System.Windows.Controls.TextBox tb, double defaultVal)
        {
            if (double.TryParse(tb.Text, out double val))
                return val;
            return defaultVal;
        }

        private int GetIntValue(System.Windows.Controls.TextBox tb, int defaultVal)
        {
            if (int.TryParse(tb.Text, out int val))
                return val;
            return defaultVal;
        }

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;

            // Pre-flight: make sure Python is available
            if (_pythonEnv?.PythonPath == null)
            {
                MessageBox.Show(
                    "Python 3 was not found on this machine.\n\n" +
                    "Install Python 3.10+ from https://www.python.org/downloads/\n" +
                    "Make sure 'Add Python to PATH' is checked during installation.",
                    "Python Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (_pythonEnv?.ScriptPath == null)
            {
                MessageBox.Show(
                    "The catchment_delineation.py script was not found.\n\n" +
                    "Reinstall the CatchmentTool plugin bundle.",
                    "Script Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

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

            var selectedTypes = _structureTypes?.Where(s => s.IsSelected).Select(s => s.PartFamilyName).ToList()
                               ?? new List<string>();

            if (selectedTypes.Count == 0)
            {
                MessageBox.Show("Please select at least one structure type.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isRunning = true;
            btnRun.IsEnabled = false;
            btnClose.Content = "Cancel";
            txtLog.Clear();

            var surfaceId = ((SurfaceInfo)cmbSurface.SelectedItem).Id;
            var networkId = ((NetworkInfo)cmbNetwork.SelectedItem).Id;
            var surfaceName = ((SurfaceInfo)cmbSurface.SelectedItem).Name;
            var networkName = ((NetworkInfo)cmbNetwork.SelectedItem).Name;

            // Get advanced settings
            var settings = new DelineationSettings
            {
                CellSize = GetCellSize(),
                SnapDistance = GetDoubleValue(txtSnapDistance, _units.DefaultSnapDistance),
                BreachDistance = GetIntValue(txtBreachDistance, 10),
                DepressionMethod = GetDepressionMethod(),
                MinCatchmentArea = GetDoubleValue(txtMinArea, _units.DefaultMinArea),

                BurnPipes = chkBurnPipes.IsChecked == true,
                BurnDepth = GetDoubleValue(txtBurnDepth, _units.DefaultBurnDepth)
            };

            try
            {
                await RunDelineationAsync(surfaceId, networkId, surfaceName, networkName, selectedTypes, settings);
            }
            catch (System.Exception ex)
            {
                Log($"\n✗ ERROR: {ex.Message}", Brushes.Red);
            }
            finally
            {
                _isRunning = false;
                btnRun.IsEnabled = true;
                btnClose.Content = "Close";
            }
        }

        private async Task RunDelineationAsync(ObjectId surfaceId, ObjectId networkId,
                                                string surfaceName, string networkName,
                                                List<string> selectedTypes, DelineationSettings settings)
        {
            string ul = _units.UnitLabel;
            string al = _units.AreaLabel;
            var stopwatch = Stopwatch.StartNew();

            Log("╔══════════════════════════════════════════════════════════════╗", Brushes.Cyan);
            Log("║       AUTOMATED CATCHMENT DELINEATION                        ║", Brushes.Cyan);
            Log("╚══════════════════════════════════════════════════════════════╝", Brushes.Cyan);
            Log("");

            // ═══════════════════════════════════════════════════════════════
            // STEP 0: Ensure Python packages
            // ═══════════════════════════════════════════════════════════════
            SetProgress(0, "Checking Python packages...");
            var missing = _pythonEnv.CheckMissingPackages();
            if (missing.Count > 0)
            {
                Log($"  Installing missing packages: {string.Join(", ", missing)}...");
                Log("  (This may take a minute on first run)");
                DoEvents();

                bool ok = await Task.Run(() => _pythonEnv.InstallPackages(missing));
                if (!ok)
                {
                    Log("  ✗ Failed to install required Python packages.", Brushes.Red);
                    Log("  Try running: pip install " + string.Join(" ", missing), Brushes.Red);
                    return;
                }
                Log("  ✓ Packages installed", Brushes.LightGreen);
            }

            // ═══════════════════════════════════════════════════════════════
            // STEP 1: Setup
            // ═══════════════════════════════════════════════════════════════
            SetProgress(5, "Setting up...");
            Log("┌─────────────────────────────────────────────────────────────┐", Brushes.Yellow);
            Log("│ STEP 1/5: SETUP                                             │", Brushes.Yellow);
            Log("└─────────────────────────────────────────────────────────────┘", Brushes.Yellow);

            string tempDir = Path.Combine(Path.GetTempPath(), "CatchmentTool_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);

            Log($"  Surface: {surfaceName}");
            Log($"  Network: {networkName}");
            Log($"  Structure types: {selectedTypes.Count}");
            Log($"  Drawing units: {(_units.IsMetric ? "Metric" : "Imperial")} ({ul})");
            Log($"  Working directory: {tempDir}");
            Log("");
            Log("  Analysis Settings:");
            Log($"    Cell size: {settings.CellSize} {ul}");
            Log($"    Snap distance: {settings.SnapDistance} {ul}");
            Log($"    Breach distance: {settings.BreachDistance} cells");
            Log($"    Depression method: {settings.DepressionMethod}");
            Log($"    Min catchment area: {settings.MinCatchmentArea} {al}");

            Log($"    Burn pipes: {(settings.BurnPipes ? $"Yes (depth: {settings.BurnDepth} {ul})" : "No")}");
            Log("");

            await Task.Delay(100); // Allow UI to update
            DoEvents();

            // ═══════════════════════════════════════════════════════════════
            // STEP 2: Export DEM
            // ═══════════════════════════════════════════════════════════════
            SetProgress(10, "Exporting surface...");
            Log("┌─────────────────────────────────────────────────────────────┐", Brushes.Yellow);
            Log("│ STEP 2/5: EXPORT SURFACE TO DEM                             │", Brushes.Yellow);
            Log("└─────────────────────────────────────────────────────────────┘", Brushes.Yellow);

            var surfaceExporter = new SurfaceExporter(_doc);
            surfaceExporter.ProgressCallback = (msg) =>
            {
                Log($"  {msg}");
                DoEvents();
            };
            surfaceExporter.CellSize = settings.CellSize;

            string demPath = Path.Combine(tempDir, "surface_dem");
            var surfaceResult = surfaceExporter.Export(surfaceId, demPath);

            if (!surfaceResult.Success)
            {
                Log("  ✗ Failed to export surface", Brushes.Red);
                return;
            }

            Log($"  ✓ DEM exported: {surfaceResult.Columns} x {surfaceResult.Rows} cells", Brushes.LightGreen);
            Log("");

            // ═══════════════════════════════════════════════════════════════
            // STEP 3: Export structures
            // ═══════════════════════════════════════════════════════════════
            SetProgress(30, "Exporting structures...");
            Log("┌─────────────────────────────────────────────────────────────┐", Brushes.Yellow);
            Log("│ STEP 3/5: EXPORT INLET STRUCTURES                           │", Brushes.Yellow);
            Log("└─────────────────────────────────────────────────────────────┘", Brushes.Yellow);

            var structureExtractor = new StructureExtractor(_doc);
            string inletsPath = Path.Combine(tempDir, "inlet_structures");
            var structureResult = structureExtractor.ExtractByTypes(networkId, inletsPath, selectedTypes);

            if (structureResult.Structures.Count == 0)
            {
                Log("  ✗ No structures exported", Brushes.Red);
                return;
            }

            Log($"  ✓ Exported {structureResult.Structures.Count} inlet structures", Brushes.LightGreen);

            // Export pipe lines for burning into DEM
            string pipesPath = "";
            if (settings.BurnPipes)
            {
                pipesPath = Path.Combine(tempDir, "pipe_lines.json");
                int pipeCount = structureExtractor.ExtractPipeLines(networkId, pipesPath);
                Log($"  ✓ Exported {pipeCount} pipe lines for DEM burning", Brushes.LightGreen);
            }
            Log("");

            // ═══════════════════════════════════════════════════════════════
            // STEP 4: Run Python
            // ═══════════════════════════════════════════════════════════════
            SetProgress(40, "Running catchment delineation...");
            Log("┌─────────────────────────────────────────────────────────────┐", Brushes.Yellow);
            Log("│ STEP 4/5: RUN CATCHMENT DELINEATION (WhiteboxTools)         │", Brushes.Yellow);
            Log("└─────────────────────────────────────────────────────────────┘", Brushes.Yellow);

            Log("  Running hydrological analysis...");
            if (settings.BurnPipes)
            {
                Log($"    Burning pipes into DEM (depth: {settings.BurnDepth} {ul})");
            }
            Log("    DEM conditioning (breach depressions)");
            Log("    D8 flow direction");
            Log("    Flow accumulation");
            Log("    Watershed delineation");
            Log("");

            string catchmentsPath = Path.Combine(tempDir, "catchments.shp");

            string pythonOutput = "";
            string pythonError = "";

            await Task.Run(() =>
            {
                RunPythonDelineation(
                    surfaceResult.OutputPath,
                    inletsPath + ".json",
                    catchmentsPath,
                    tempDir,
                    settings,
                    pipesPath,
                    out pythonOutput,
                    out pythonError);
            });

            SetProgress(70, "Checking results...");

            // Log Python output
            if (!string.IsNullOrWhiteSpace(pythonOutput))
            {
                Log("  Python output:");
                foreach (var line in pythonOutput.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        Log($"    {line.Trim()}");
                }
            }

            // Log Python errors
            if (!string.IsNullOrWhiteSpace(pythonError))
            {
                Log("  Python errors:", Brushes.Orange);
                foreach (var line in pythonError.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        Log($"    {line.Trim()}", Brushes.Orange);
                }
            }

            string catchmentsJson = Path.ChangeExtension(catchmentsPath, ".json");
            if (!File.Exists(catchmentsJson))
            {
                Log($"  ✗ Catchment delineation failed - output file not found", Brushes.Red);
                Log($"    Expected: {catchmentsJson}", Brushes.Red);
                Log($"    Working dir: {tempDir}", Brushes.Red);
                return;
            }

            Log($"  ✓ Catchment polygons created", Brushes.LightGreen);
            Log("");

            // ═══════════════════════════════════════════════════════════════
            // STEP 5: Import catchments
            // ═══════════════════════════════════════════════════════════════
            SetProgress(80, "Importing catchments...");
            Log("┌─────────────────────────────────────────────────────────────┐", Brushes.Yellow);
            Log("│ STEP 5/5: IMPORT CATCHMENTS INTO CIVIL 3D                   │", Brushes.Yellow);
            Log("└─────────────────────────────────────────────────────────────┘", Brushes.Yellow);

            var catchmentCreator = new CatchmentCreator(_doc);
            var importResult = catchmentCreator.ImportCatchments(catchmentsJson, networkId, surfaceId);

            Log($"  ✓ Catchments created: {importResult.CatchmentsCreated}", Brushes.LightGreen);
            Log($"  ✓ Linked to structures: {importResult.CatchmentsLinked}", Brushes.LightGreen);
            Log("");

            // ═══════════════════════════════════════════════════════════════
            // Complete
            // ═══════════════════════════════════════════════════════════════
            SetProgress(100, "Complete!");
            stopwatch.Stop();

            Log("╔══════════════════════════════════════════════════════════════╗", Brushes.LightGreen);
            Log("║                    DELINEATION COMPLETE!                     ║", Brushes.LightGreen);
            Log("╚══════════════════════════════════════════════════════════════╝", Brushes.LightGreen);
            Log("");
            Log($"  Summary:");
            Log($"    Catchments created: {importResult.CatchmentsCreated}");
            Log($"    Processing time: {stopwatch.Elapsed.TotalSeconds:F1} seconds");
            Log($"    Working directory: {tempDir}");

            MessageBox.Show(
                $"Catchment delineation complete!\n\n" +
                $"Catchments created: {importResult.CatchmentsCreated}\n" +
                $"Linked to structures: {importResult.CatchmentsLinked}\n" +
                $"Processing time: {stopwatch.Elapsed.TotalSeconds:F1} seconds",
                "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Run the Python delineation script using the discovered PythonEnvironment.
        /// </summary>
        private void RunPythonDelineation(string demPath, string inletsPath, string outputPath,
                                          string workingDir, DelineationSettings settings,
                                          string pipesPath,
                                          out string stdOut, out string stdErr)
        {
            stdOut = "";
            stdErr = "";

            // Build arguments
            string arguments = $"--dem \"{demPath}\" " +
                              $"--inlets \"{inletsPath}\" " +
                              $"--output \"{outputPath}\" " +
                              $"--working-dir \"{workingDir}\" " +
                              $"--snap-distance {settings.SnapDistance} " +
                              $"--breach-distance {settings.BreachDistance} " +
                              $"--depression-method {settings.DepressionMethod} " +
                              $"--min-area {settings.MinCatchmentArea} " +
                              $"--area-unit {(_units.IsMetric ? "sq_m" : "sq_ft")} " +
                              $"--drawing-units {(_units.IsMetric ? "m" : "ft")}";

            if (settings.BurnPipes && !string.IsNullOrEmpty(pipesPath))
            {
                arguments += $" --burn-pipes \"{pipesPath}\" --burn-depth {settings.BurnDepth}";
            }

            var startInfo = _pythonEnv.BuildProcessStartInfo(arguments);

            try
            {
                using (var process = new Process { StartInfo = startInfo })
                {
                    var outputBuilder = new StringBuilder();
                    var errorBuilder = new StringBuilder();

                    process.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                            outputBuilder.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                            errorBuilder.AppendLine(e.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    bool completed = process.WaitForExit(300000);

                    stdOut = outputBuilder.ToString();
                    stdErr = errorBuilder.ToString();

                    if (!completed)
                    {
                        process.Kill();
                        stdErr += "\nProcess timed out after 5 minutes";
                    }
                    else if (process.ExitCode != 0)
                    {
                        stdErr += $"\nProcess exited with code {process.ExitCode}";
                    }
                }
            }
            catch (System.Exception ex)
            {
                stdErr = $"Failed to run Python: {ex.Message}";
            }
        }

        private void SetProgress(int percent, string status)
        {
            Dispatcher.Invoke(() =>
            {
                progressBar.Value = percent;
                txtProgress.Text = status;
            });
        }

        private void Log(string message, Brush color = null)
        {
            Dispatcher.Invoke(() =>
            {
                txtLog.AppendText(message + Environment.NewLine);
                txtLog.ScrollToEnd();
            });
        }

        private void DoEvents()
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(delegate { }));
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
