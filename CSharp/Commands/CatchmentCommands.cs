using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CatchmentTool.Services;
using CatchmentTool.UI;

// Ensure all dialog types are referenced
using ExportDialog = CatchmentTool.UI.ExportDialog;
using ImportDialog = CatchmentTool.UI.ImportDialog;
using AutoCatchmentDialog = CatchmentTool.UI.AutoCatchmentDialog;

// Aliases to resolve ambiguous references
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CatchmentTool.Commands.CatchmentCommands))]

namespace CatchmentTool.Commands
{
    /// <summary>
    /// Civil 3D commands for catchment delineation workflow.
    /// </summary>
    public class CatchmentCommands
    {
        /// <summary>
        /// Export surface and inlet structures for catchment delineation.
        /// This is the main workflow entry point.
        /// </summary>
        [CommandMethod("EXPORTCATCHMENTDATA")]
        public void ExportCatchmentData()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            var db = doc.Database;
            
            try
            {
                ed.WriteMessage("\n=== CATCHMENT DELINEATION TOOL ===\n");
                ed.WriteMessage("This tool exports surface and inlet data for WhiteboxTools processing.\n");
                
                // Get working directory
                string workingDir = GetWorkingDirectory(ed);
                if (string.IsNullOrEmpty(workingDir)) return;
                
                // Step 1: Select and export surface
                ed.WriteMessage("\n--- Step 1: Select Surface ---\n");
                ObjectId surfaceId = SelectSurface(ed, db);
                if (surfaceId.IsNull) return;
                
                var surfaceExporter = new SurfaceExporter(doc);
                string demPath = Path.Combine(workingDir, "surface_dem");
                var surfaceResult = surfaceExporter.Export(surfaceId, demPath);
                
                if (!surfaceResult.Success)
                {
                    ed.WriteMessage("\nERROR: Failed to export surface.");
                    return;
                }
                
                // Step 2: Select and export pipe network inlet structures
                ed.WriteMessage("\n\n--- Step 2: Select Pipe Network ---\n");
                ObjectId networkId = SelectPipeNetwork(ed, db);
                if (networkId.IsNull) return;
                
                var structureExtractor = new StructureExtractor(doc);
                string inletsPath = Path.Combine(workingDir, "inlet_structures");
                var structureResult = structureExtractor.Extract(networkId, inletsPath);
                
                if (!structureResult.Success || structureResult.Structures.Count == 0)
                {
                    ed.WriteMessage("\nWARNING: No inlet structures exported.");
                    ed.WriteMessage("\nContinue anyway? (Y/N) ");
                    
                    var confirmResult = ed.GetString("\nContinue? [Y/N]: ");
                    if (confirmResult.Status != PromptStatus.OK || 
                        !confirmResult.StringResult.Equals("Y", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
                
                // Write workflow configuration
                WriteWorkflowConfig(workingDir, surfaceResult, structureResult);
                
                // Summary
                ed.WriteMessage("\n\n=== EXPORT COMPLETE ===\n");
                ed.WriteMessage($"Working directory: {workingDir}\n");
                ed.WriteMessage($"Surface exported: {surfaceResult.SurfaceName}\n");
                ed.WriteMessage($"  - Cell size: {surfaceResult.CellSize:F4} units\n");
                ed.WriteMessage($"  - Dimensions: {surfaceResult.Columns} x {surfaceResult.Rows}\n");
                ed.WriteMessage($"Inlet structures: {structureResult.Structures.Count}\n");
                ed.WriteMessage("\nNext step: Run the Python script to delineate catchments:\n");
                ed.WriteMessage($"  python catchment_delineation.py --dem \"{surfaceResult.OutputPath}\" ");
                ed.WriteMessage($"--inlets \"{structureResult.OutputPath}.json\" ");
                ed.WriteMessage($"--output \"{Path.Combine(workingDir, "catchments.shp")}\"\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nERROR: {ex.Message}\n");
                ed.WriteMessage($"Stack trace: {ex.StackTrace}\n");
            }
        }
        
        /// <summary>
        /// Import catchment polygons and create Civil 3D Catchment objects.
        /// </summary>
        [CommandMethod("IMPORTCATCHMENTS")]
        public void ImportCatchments()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            var db = doc.Database;
            
            try
            {
                ed.WriteMessage("\n=== IMPORT CATCHMENTS ===\n");
                
                // Get catchment shapefile/GeoJSON path
                var fileResult = ed.GetString("\nEnter path to catchment polygons file: ");
                if (fileResult.Status != PromptStatus.OK) return;
                
                string catchmentFile = fileResult.StringResult.Trim('"');
                if (!File.Exists(catchmentFile))
                {
                    ed.WriteMessage($"\nERROR: File not found: {catchmentFile}");
                    return;
                }
                
                // Get pipe network to link catchments to
                ObjectId networkId = SelectPipeNetwork(ed, db);
                if (networkId.IsNull) return;
                
                // Get surface for catchment reference
                ObjectId surfaceId = SelectSurface(ed, db);
                if (surfaceId.IsNull) return;
                
                // Import catchments
                var creator = new CatchmentCreator(doc);
                var result = creator.ImportCatchments(catchmentFile, networkId, surfaceId);
                
                ed.WriteMessage($"\n\n=== IMPORT COMPLETE ===\n");
                ed.WriteMessage($"Catchments created: {result.CatchmentsCreated}\n");
                ed.WriteMessage($"Catchments linked to structures: {result.CatchmentsLinked}\n");
                
                if (result.Errors.Count > 0)
                {
                    ed.WriteMessage($"\nWarnings/Errors:\n");
                    foreach (var error in result.Errors)
                    {
                        ed.WriteMessage($"  - {error}\n");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nERROR: {ex.Message}\n");
            }
        }
        
        /// <summary>
        /// Export dialog - exports surface and inlet data.
        /// </summary>
        [CommandMethod("CATCHMENTEXPORT")]
        public void CatchmentExport()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            
            try
            {
                var dialog = new ExportDialog(doc);
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(dialog);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nError opening export dialog: {ex.Message}\n");
            }
        }
        
        /// <summary>
        /// Import dialog - imports catchment polygons.
        /// </summary>
        [CommandMethod("CATCHMENTIMPORT")]
        public void CatchmentImport()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            
            try
            {
                var dialog = new ImportDialog(doc);
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(dialog);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nError opening import dialog: {ex.Message}\n");
            }
        }
        
        /// <summary>
        /// Alias - same as CATCHMENTEXPORT.
        /// </summary>
        [CommandMethod("CATCHMENTTOOL")]
        public void CatchmentTool()
        {
            CatchmentExport();
        }
        
        /// <summary>
        /// Alias for backward compatibility.
        /// </summary>
        [CommandMethod("DELINEATECATCHMENTS")]
        public void DelineateCatchments()
        {
            CatchmentExport();
        }
        
        /// <summary>
        /// Open the automated catchment delineation dialog.
        /// </summary>
        [CommandMethod("CATCHMENTAUTO")]
        public void CatchmentAutoDialog()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            
            try
            {
                var dialog = new AutoCatchmentDialog(doc);
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModalWindow(dialog);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nError opening dialog: {ex.Message}\n");
            }
        }
        
        /// <summary>
        /// One-step automated catchment delineation.
        /// Exports data, runs Python script, and imports catchments - all in one command.
        /// </summary>
        [CommandMethod("AUTOCATCHMENTS")]
        public void AutoCatchments()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            var db = doc.Database;
            
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                ed.WriteMessage("\n");
                ed.WriteMessage("╔══════════════════════════════════════════════════════════════╗\n");
                ed.WriteMessage("║       AUTOMATED CATCHMENT DELINEATION TOOL                   ║\n");
                ed.WriteMessage("║       One-step surface analysis and watershed creation       ║\n");
                ed.WriteMessage("╚══════════════════════════════════════════════════════════════╝\n\n");
                
                // ═══════════════════════════════════════════════════════════════
                // STEP 1: Select inputs
                // ═══════════════════════════════════════════════════════════════
                ed.WriteMessage("┌─────────────────────────────────────────────────────────────┐\n");
                ed.WriteMessage("│ STEP 1/5: SELECT INPUTS                                     │\n");
                ed.WriteMessage("└─────────────────────────────────────────────────────────────┘\n");
                
                // Select surface
                ed.WriteMessage("\n  [1.1] Selecting surface...\n");
                ObjectId surfaceId = SelectSurface(ed, db);
                if (surfaceId.IsNull)
                {
                    ed.WriteMessage("  ✗ No surface selected. Aborting.\n");
                    return;
                }
                
                string surfaceName = "";
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var surface = tr.GetObject(surfaceId, OpenMode.ForRead) as CivilSurface;
                    surfaceName = surface?.Name ?? "Unknown";
                    tr.Commit();
                }
                ed.WriteMessage($"  ✓ Surface selected: {surfaceName}\n");
                
                // Select pipe network
                ed.WriteMessage("\n  [1.2] Selecting pipe network...\n");
                ObjectId networkId = SelectPipeNetwork(ed, db);
                if (networkId.IsNull)
                {
                    ed.WriteMessage("  ✗ No pipe network selected. Aborting.\n");
                    return;
                }
                
                string networkName = "";
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var network = tr.GetObject(networkId, OpenMode.ForRead) as Network;
                    networkName = network?.Name ?? "Unknown";
                    tr.Commit();
                }
                ed.WriteMessage($"  ✓ Pipe network selected: {networkName}\n");
                
                // Get structure types and let user select
                ed.WriteMessage("\n  [1.3] Loading structure types...\n");
                var structureTypes = GetStructureTypes(networkId, db);
                ed.WriteMessage($"  ✓ Found {structureTypes.Count} structure types:\n");
                
                int typeNum = 1;
                foreach (var st in structureTypes)
                {
                    ed.WriteMessage($"      {typeNum}. {st.Key} ({st.Value} structures)\n");
                    typeNum++;
                }
                
                // For now, include all structure types (user can configure later)
                var selectedTypes = structureTypes.Keys.ToList();
                int totalStructures = structureTypes.Values.Sum();
                ed.WriteMessage($"\n  ✓ Using all {totalStructures} structures as inlets\n");
                
                // ═══════════════════════════════════════════════════════════════
                // STEP 2: Create working directory and export DEM
                // ═══════════════════════════════════════════════════════════════
                ed.WriteMessage("\n┌─────────────────────────────────────────────────────────────┐\n");
                ed.WriteMessage("│ STEP 2/5: EXPORT SURFACE TO DEM                             │\n");
                ed.WriteMessage("└─────────────────────────────────────────────────────────────┘\n");
                
                // Create temp working directory
                string tempDir = Path.Combine(Path.GetTempPath(), "CatchmentTool_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tempDir);
                ed.WriteMessage($"\n  [2.1] Created working directory:\n        {tempDir}\n");
                
                // Export surface
                ed.WriteMessage("\n  [2.2] Exporting surface to DEM raster...\n");
                var surfaceExporter = new SurfaceExporter(doc);
                surfaceExporter.ProgressCallback = (msg) => ed.WriteMessage($"        {msg}\n");
                
                string demPath = Path.Combine(tempDir, "surface_dem");
                var surfaceResult = surfaceExporter.Export(surfaceId, demPath);
                
                if (!surfaceResult.Success)
                {
                    ed.WriteMessage("  ✗ Failed to export surface. Aborting.\n");
                    return;
                }
                ed.WriteMessage($"  ✓ DEM exported: {surfaceResult.Columns} x {surfaceResult.Rows} cells\n");
                ed.WriteMessage($"        Cell size: {surfaceResult.CellSize:F2} units\n");
                
                // ═══════════════════════════════════════════════════════════════
                // STEP 3: Export inlet structures
                // ═══════════════════════════════════════════════════════════════
                ed.WriteMessage("\n┌─────────────────────────────────────────────────────────────┐\n");
                ed.WriteMessage("│ STEP 3/5: EXPORT INLET STRUCTURES                           │\n");
                ed.WriteMessage("└─────────────────────────────────────────────────────────────┘\n");
                
                ed.WriteMessage("\n  [3.1] Extracting structure locations...\n");
                var structureExtractor = new StructureExtractor(doc);
                string inletsPath = Path.Combine(tempDir, "inlet_structures");
                var structureResult = structureExtractor.ExtractByTypes(networkId, inletsPath, selectedTypes);
                
                if (structureResult.Structures.Count == 0)
                {
                    ed.WriteMessage("  ✗ No structures exported. Check structure type selection.\n");
                    return;
                }
                ed.WriteMessage($"  ✓ Exported {structureResult.Structures.Count} inlet structures\n");
                
                // ═══════════════════════════════════════════════════════════════
                // STEP 4: Run Python catchment delineation
                // ═══════════════════════════════════════════════════════════════
                ed.WriteMessage("\n┌─────────────────────────────────────────────────────────────┐\n");
                ed.WriteMessage("│ STEP 4/5: RUN CATCHMENT DELINEATION (WhiteboxTools)         │\n");
                ed.WriteMessage("└─────────────────────────────────────────────────────────────┘\n");
                
                ed.WriteMessage("\n  [4.1] Starting Python catchment delineation script...\n");
                ed.WriteMessage("        This performs hydrological analysis using WhiteboxTools:\n");
                ed.WriteMessage("        - DEM conditioning (breach depressions)\n");
                ed.WriteMessage("        - D8 flow direction calculation\n");
                ed.WriteMessage("        - Flow accumulation\n");
                ed.WriteMessage("        - Pour point snapping\n");
                ed.WriteMessage("        - Watershed delineation\n");
                ed.WriteMessage("        - Raster to vector conversion\n\n");
                
                string catchmentsPath = Path.Combine(tempDir, "catchments.shp");
                string pythonResult = RunPythonDelineation(
                    surfaceResult.OutputPath,
                    inletsPath + ".json",
                    catchmentsPath,
                    tempDir,
                    ed);
                
                if (!File.Exists(catchmentsPath))
                {
                    ed.WriteMessage($"  ✗ Catchment delineation failed. Python output:\n{pythonResult}\n");
                    return;
                }
                
                // Check for GeoJSON output
                string catchmentsJson = Path.ChangeExtension(catchmentsPath, ".json");
                if (!File.Exists(catchmentsJson))
                {
                    ed.WriteMessage("  ✗ Catchment GeoJSON not found. Aborting.\n");
                    return;
                }
                ed.WriteMessage($"  ✓ Catchment polygons created\n");
                
                // ═══════════════════════════════════════════════════════════════
                // STEP 5: Import catchments into Civil 3D
                // ═══════════════════════════════════════════════════════════════
                ed.WriteMessage("\n┌─────────────────────────────────────────────────────────────┐\n");
                ed.WriteMessage("│ STEP 5/5: IMPORT CATCHMENTS INTO CIVIL 3D                   │\n");
                ed.WriteMessage("└─────────────────────────────────────────────────────────────┘\n");
                
                ed.WriteMessage("\n  [5.1] Creating Civil 3D catchment objects...\n");
                var catchmentCreator = new CatchmentCreator(doc);
                var importResult = catchmentCreator.ImportCatchments(catchmentsJson, networkId, surfaceId);
                
                ed.WriteMessage($"  ✓ Catchments created: {importResult.CatchmentsCreated}\n");
                ed.WriteMessage($"  ✓ Linked to structures: {importResult.CatchmentsLinked}\n");
                
                if (importResult.Errors.Count > 0)
                {
                    ed.WriteMessage($"\n  Warnings ({importResult.Errors.Count}):\n");
                    foreach (var err in importResult.Errors.Take(5))
                    {
                        ed.WriteMessage($"    - {err}\n");
                    }
                }
                
                // ═══════════════════════════════════════════════════════════════
                // COMPLETE
                // ═══════════════════════════════════════════════════════════════
                stopwatch.Stop();
                
                ed.WriteMessage("\n╔══════════════════════════════════════════════════════════════╗\n");
                ed.WriteMessage("║                    DELINEATION COMPLETE!                     ║\n");
                ed.WriteMessage("╚══════════════════════════════════════════════════════════════╝\n\n");
                ed.WriteMessage($"  Summary:\n");
                ed.WriteMessage($"    • Surface analyzed: {surfaceName}\n");
                ed.WriteMessage($"    • DEM resolution: {surfaceResult.CellSize:F2} ft cells\n");
                ed.WriteMessage($"    • Inlet structures: {structureResult.Structures.Count}\n");
                ed.WriteMessage($"    • Catchments created: {importResult.CatchmentsCreated}\n");
                ed.WriteMessage($"    • Processing time: {stopwatch.Elapsed.TotalSeconds:F1} seconds\n");
                ed.WriteMessage($"\n  Working directory (can be deleted):\n    {tempDir}\n\n");
            }
            catch (System.Exception ex)
            {
                stopwatch.Stop();
                ed.WriteMessage($"\n  ✗ ERROR: {ex.Message}\n");
                ed.WriteMessage($"  Stack trace: {ex.StackTrace}\n");
            }
        }
        
        /// <summary>
        /// Get unique structure types and counts from a pipe network.
        /// </summary>
        private Dictionary<string, int> GetStructureTypes(ObjectId networkId, Database db)
        {
            var types = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            
            using (var tr = db.TransactionManager.StartTransaction())
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
                            if (types.ContainsKey(partFamily))
                                types[partFamily]++;
                            else
                                types[partFamily] = 1;
                        }
                    }
                }
                tr.Commit();
            }
            
            return types;
        }
        
        /// <summary>
        /// Run the Python catchment delineation script using PythonEnvironment.
        /// </summary>
        private string RunPythonDelineation(string demPath, string inletsPath, string outputPath,
                                            string workingDir, Editor ed)
        {
            // Discover Python and script
            var pyEnv = new PythonEnvironment();
            pyEnv.StatusCallback = (msg) => ed.WriteMessage($"        {msg}\n");

            if (!pyEnv.Discover())
            {
                if (pyEnv.PythonPath == null)
                {
                    ed.WriteMessage("  ✗ Python 3 not found on this machine.\n");
                    ed.WriteMessage("    Install Python 3.10+ from https://www.python.org/downloads/\n");
                    ed.WriteMessage("    Make sure 'Add Python to PATH' is checked during installation.\n");
                }
                else
                {
                    ed.WriteMessage("  ✗ catchment_delineation.py script not found.\n");
                    ed.WriteMessage("    Reinstall the CatchmentTool plugin bundle.\n");
                }
                return "Python environment not available";
            }

            ed.WriteMessage($"  [4.2] {pyEnv.PythonVersion}\n");
            ed.WriteMessage($"  [4.2] Script: {pyEnv.ScriptPath}\n\n");

            // Check and install missing packages
            var missing = pyEnv.CheckMissingPackages();
            if (missing.Count > 0)
            {
                ed.WriteMessage($"  [4.3] Installing missing packages: {string.Join(", ", missing)}...\n");
                if (!pyEnv.InstallPackages(missing))
                {
                    ed.WriteMessage("  ✗ Failed to install required packages.\n");
                    ed.WriteMessage($"    Try: pip install {string.Join(" ", missing)}\n");
                    return "Package installation failed";
                }
            }

            // Build the command
            string arguments = $"--dem \"{demPath}\" " +
                              $"--inlets \"{inletsPath}\" " +
                              $"--output \"{outputPath}\" " +
                              $"--working-dir \"{workingDir}\"";

            ed.WriteMessage($"  [4.3] Running catchment delineation...\n");

            var startInfo = pyEnv.BuildProcessStartInfo(arguments);

            var output = new StringBuilder();
            var error = new StringBuilder();

            try
            {
                using (var process = new Process { StartInfo = startInfo })
                {
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            output.AppendLine(e.Data);
                            // Log key progress lines
                            if (e.Data.Contains("Step") || e.Data.Contains("COMPLETE") ||
                                e.Data.Contains("Created") || e.Data.Contains("ERROR"))
                            {
                                ed.WriteMessage($"        {e.Data}\n");
                            }
                        }
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            error.AppendLine(e.Data);
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // Wait for completion with timeout
                    bool completed = process.WaitForExit(300000); // 5 minute timeout

                    if (!completed)
                    {
                        process.Kill();
                        return "Python script timed out after 5 minutes";
                    }

                    if (process.ExitCode != 0)
                    {
                        return $"Python exited with code {process.ExitCode}: {error}";
                    }
                }
            }
            catch (System.Exception ex)
            {
                return $"Failed to run Python: {ex.Message}";
            }

            return output.ToString();
        }
        
        /// <summary>
        /// Link existing catchments to outlet structures based on naming convention.
        /// Expects catchments named "CA-xxx" to link to structure "xxx".
        /// </summary>
        [CommandMethod("LINKCATCHMENTS")]
        public void LinkCatchmentsToStructures()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            var db = doc.Database;
            
            try
            {
                ed.WriteMessage("\n=== LINK CATCHMENTS TO OUTLET STRUCTURES ===\n");
                ed.WriteMessage("This command links catchments to structures based on naming convention.\n");
                ed.WriteMessage("Catchment 'CA-xxx' will be linked to structure 'xxx'\n\n");
                
                // Get pipe network
                ObjectId networkId = SelectPipeNetwork(ed, db);
                if (networkId.IsNull) return;
                
                // Build structure name lookup
                var structureLookup = new System.Collections.Generic.Dictionary<string, ObjectId>(
                    StringComparer.OrdinalIgnoreCase);
                
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var network = tr.GetObject(networkId, OpenMode.ForRead) as Network;
                    if (network != null)
                    {
                        foreach (ObjectId structId in network.GetStructureIds())
                        {
                            var structure = tr.GetObject(structId, OpenMode.ForRead) as Structure;
                            if (structure != null && !string.IsNullOrEmpty(structure.Name))
                            {
                                structureLookup[structure.Name] = structId;
                            }
                        }
                    }
                    tr.Commit();
                }
                
                ed.WriteMessage($"Found {structureLookup.Count} structures in network\n\n");
                
                // Find all catchments in the drawing by iterating model space
                int linked = 0;
                int notFound = 0;
                int total = 0;
                
                // Log Catchment properties once
                bool loggedProps = false;
                
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    // Get all Catchment objects from model space
                    var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                    
                    foreach (ObjectId objId in ms)
                    {
                        // Try to get as Catchment
                        Catchment catchment = null;
                        try
                        {
                            catchment = tr.GetObject(objId, OpenMode.ForWrite) as Catchment;
                        }
                        catch { continue; }
                        
                        if (catchment == null) continue;
                        
                        total++;
                        
                        // Log properties once for debugging
                        if (!loggedProps)
                        {
                            LogCatchmentProperties(catchment, ed);
                            loggedProps = true;
                        }
                        
                        // Parse catchment name to get structure name
                        // Expected format: "CA-xxx" where xxx is the structure name
                        string catchmentName = catchment.Name;
                        string structureName = null;
                        
                        if (catchmentName.StartsWith("CA-", StringComparison.OrdinalIgnoreCase))
                        {
                            structureName = catchmentName.Substring(3);
                        }
                        else
                        {
                            // Try to find structure with same name
                            structureName = catchmentName;
                        }
                        
                        // Find matching structure
                        if (!string.IsNullOrEmpty(structureName) && 
                            structureLookup.TryGetValue(structureName, out ObjectId structureId))
                        {
                            // Try to set the reference object
                            if (SetCatchmentReferenceObject(catchment, structureId, tr, ed))
                            {
                                linked++;
                                ed.WriteMessage($"  Linked '{catchmentName}' -> '{structureName}'\n");
                            }
                            else
                            {
                                ed.WriteMessage($"  FAILED to link '{catchmentName}' -> '{structureName}'\n");
                            }
                        }
                        else
                        {
                            notFound++;
                            ed.WriteMessage($"  No structure found for '{catchmentName}' (looked for '{structureName}')\n");
                        }
                    }
                    
                    tr.Commit();
                }
                
                ed.WriteMessage($"\n=== SUMMARY ===\n");
                ed.WriteMessage($"Total catchments: {total}\n");
                ed.WriteMessage($"Successfully linked: {linked}\n");
                ed.WriteMessage($"Structure not found: {notFound}\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nERROR: {ex.Message}\n");
                ed.WriteMessage($"Stack trace: {ex.StackTrace}\n");
            }
        }
        
        /// <summary>
        /// Log all catchment properties for debugging.
        /// </summary>
        private void LogCatchmentProperties(Catchment catchment, Editor ed)
        {
            var catchmentType = catchment.GetType();
            
            ed.WriteMessage("\n--- CATCHMENT API PROPERTIES ---\n");
            var allProps = catchmentType.GetProperties()
                .OrderBy(p => p.Name)
                .ToList();
            foreach (var p in allProps)
            {
                ed.WriteMessage($"  {p.Name} ({p.PropertyType.Name}) CanWrite={p.CanWrite}\n");
            }
            ed.WriteMessage("\n");
        }
        
        /// <summary>
        /// Set the reference object (outlet structure) on a catchment.
        /// </summary>
        private bool SetCatchmentReferenceObject(Catchment catchment, ObjectId structureId, 
                                                  Transaction tr, Editor ed)
        {
            var catchmentType = catchment.GetType();
            bool success = false;
            
            // Get the structure to find its network
            Structure structure = null;
            ObjectId networkId = ObjectId.Null;
            try
            {
                structure = tr.GetObject(structureId, OpenMode.ForRead) as Structure;
                if (structure != null)
                {
                    networkId = structure.NetworkId;
                }
            }
            catch { }
            
            // Step 1: Set the pipe network reference first (required before setting structure)
            if (!networkId.IsNull)
            {
                try
                {
                    var networkProp = catchmentType.GetProperty("ReferencePipeNetworkId");
                    if (networkProp != null && networkProp.CanWrite)
                    {
                        networkProp.SetValue(catchment, networkId);
                        ed.WriteMessage($"    Set ReferencePipeNetworkId\n");
                    }
                }
                catch { }
            }
            
            // Step 2: Set the structure reference using ReferencePipeNetworkStructureId
            try
            {
                var structProp = catchmentType.GetProperty("ReferencePipeNetworkStructureId");
                if (structProp != null && structProp.CanWrite)
                {
                    structProp.SetValue(catchment, structureId);
                    ed.WriteMessage($"    Set ReferencePipeNetworkStructureId\n");
                    success = true;
                }
            }
            catch { }
            
            // Step 3: Also try ReferenceDischargeObjectId as alternative/backup
            if (!success)
            {
                try
                {
                    var dischargeProp = catchmentType.GetProperty("ReferenceDischargeObjectId");
                    if (dischargeProp != null && dischargeProp.CanWrite)
                    {
                        dischargeProp.SetValue(catchment, structureId);
                        ed.WriteMessage($"    Set ReferenceDischargeObjectId\n");
                        success = true;
                    }
                }
                catch { }
            }
            
            return success;
        }
        
        /// <summary>
        /// List all structures in a pipe network (for debugging).
        /// </summary>
        [CommandMethod("LISTPIPESTRUCTURES")]
        public void ListPipeStructures()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            var db = doc.Database;
            
            try
            {
                ObjectId networkId = SelectPipeNetwork(ed, db);
                if (networkId.IsNull) return;
                
                var extractor = new StructureExtractor(doc);
                var structures = extractor.GetAllStructures(networkId);
                
                ed.WriteMessage($"\n\n=== PIPE NETWORK STRUCTURES ===\n");
                ed.WriteMessage(string.Format("{0,-30} {1,-25} {2,-10} {3,-12} {4,-12}\n", "Name", "Part Family", "Is Inlet", "X", "Y"));
                ed.WriteMessage(new string('-', 100) + "\n");
                
                foreach (var s in structures)
                {
                    ed.WriteMessage($"{s.Name,-30} {s.PartFamilyName,-25} {s.IsInlet,-10} {s.X,-12:F2} {s.Y,-12:F2}\n");
                }
                
                int inletCount = 0;
                foreach (var s in structures) if (s.IsInlet) inletCount++;
                
                ed.WriteMessage($"\nTotal: {structures.Count} structures, {inletCount} identified as inlets\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nERROR: {ex.Message}\n");
            }
        }
        
        #region Helper Methods
        
        private string GetWorkingDirectory(Editor ed)
        {
            // Default to a Data subfolder in the drawing's location
            string drawingPath = Application.DocumentManager.MdiActiveDocument.Name;
            string defaultDir = "";
            
            if (!string.IsNullOrEmpty(drawingPath) && File.Exists(drawingPath))
            {
                defaultDir = Path.Combine(Path.GetDirectoryName(drawingPath), "CatchmentData");
            }
            else
            {
                defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 
                                          "CatchmentData");
            }
            
            var result = ed.GetString($"\nEnter working directory [{defaultDir}]: ");
            
            string workingDir = result.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(result.StringResult)
                ? result.StringResult.Trim('"')
                : defaultDir;
            
            // Create directory if it doesn't exist
            if (!Directory.Exists(workingDir))
            {
                Directory.CreateDirectory(workingDir);
                ed.WriteMessage($"\nCreated directory: {workingDir}");
            }
            
            return workingDir;
        }
        
        private ObjectId SelectSurface(Editor ed, Database db)
        {
            // Get all surfaces in the document
            var civilDoc = CivilApplication.ActiveDocument;
            var surfaceIds = civilDoc.GetSurfaceIds();
            
            if (surfaceIds.Count == 0)
            {
                ed.WriteMessage("\nNo surfaces found in this drawing.");
                return ObjectId.Null;
            }
            
            // List available surfaces
            ed.WriteMessage("\nAvailable surfaces:\n");
            
            using (var tr = db.TransactionManager.StartTransaction())
            {
                int i = 1;
                foreach (ObjectId id in surfaceIds)
                {
                    var surface = tr.GetObject(id, OpenMode.ForRead) as CivilSurface;
                    if (surface != null)
                    {
                        string surfaceType = surface is TinSurface ? "TIN" : 
                                            surface is GridSurface ? "Grid" : "Other";
                        ed.WriteMessage($"  {i}. {surface.Name} ({surfaceType})\n");
                    }
                    i++;
                }
                tr.Commit();
            }
            
            // Prompt for selection
            var opts = new PromptIntegerOptions("\nSelect surface number: ");
            opts.LowerLimit = 1;
            opts.UpperLimit = surfaceIds.Count;
            opts.AllowNone = false;
            
            var result = ed.GetInteger(opts);
            if (result.Status != PromptStatus.OK)
            {
                return ObjectId.Null;
            }
            
            return surfaceIds[result.Value - 1];
        }
        
        private ObjectId SelectPipeNetwork(Editor ed, Database db)
        {
            // Get all pipe networks
            var civilDoc = CivilApplication.ActiveDocument;
            var networkIds = civilDoc.GetPipeNetworkIds();
            
            if (networkIds.Count == 0)
            {
                ed.WriteMessage("\nNo pipe networks found in this drawing.");
                return ObjectId.Null;
            }
            
            // List available networks
            ed.WriteMessage("\nAvailable pipe networks:\n");
            
            using (var tr = db.TransactionManager.StartTransaction())
            {
                int i = 1;
                foreach (ObjectId id in networkIds)
                {
                    var network = tr.GetObject(id, OpenMode.ForRead) as Network;
                    if (network != null)
                    {
                        int structCount = network.GetStructureIds().Count;
                        int pipeCount = network.GetPipeIds().Count;
                        ed.WriteMessage($"  {i}. {network.Name} ({structCount} structures, {pipeCount} pipes)\n");
                    }
                    i++;
                }
                tr.Commit();
            }
            
            // Prompt for selection
            var opts = new PromptIntegerOptions("\nSelect pipe network number: ");
            opts.LowerLimit = 1;
            opts.UpperLimit = networkIds.Count;
            opts.AllowNone = false;
            
            var result = ed.GetInteger(opts);
            if (result.Status != PromptStatus.OK)
            {
                return ObjectId.Null;
            }
            
            return networkIds[result.Value - 1];
        }
        
        private void WriteWorkflowConfig(string workingDir, SurfaceExportResult surfaceResult, 
                                         StructureExtractionResult structureResult)
        {
            var config = new
            {
                created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                working_directory = workingDir,
                surface = new
                {
                    name = surfaceResult.SurfaceName,
                    dem_file = surfaceResult.OutputPath,
                    cell_size = surfaceResult.CellSize,
                    rows = surfaceResult.Rows,
                    columns = surfaceResult.Columns,
                    bounds = surfaceResult.Bounds
                },
                pipe_network = new
                {
                    name = structureResult.NetworkName,
                    inlets_file = structureResult.OutputPath + ".json",
                    inlet_count = structureResult.Structures.Count
                },
                output = new
                {
                    catchments_file = Path.Combine(workingDir, "catchments.shp")
                }
            };
            
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(config, Newtonsoft.Json.Formatting.Indented);
            string configPath = Path.Combine(workingDir, "workflow_config.json");
            File.WriteAllText(configPath, json);
        }
        
        #endregion
    }
}

