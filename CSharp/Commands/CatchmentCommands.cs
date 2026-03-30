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
            // Detect drawing units and coordinate system for Python-side processing
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var drawingUnits = DrawingUnits.Detect(doc);

            string csCode = "";
            try
            {
                csCode = Autodesk.AutoCAD.ApplicationServices.Application
                    .GetSystemVariable("CGEOCS") as string ?? "";
            }
            catch { }

            var config = new
            {
                created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                working_directory = workingDir,
                drawing_units = drawingUnits.UnitLabel,
                is_metric = drawingUnits.IsMetric,
                autodesk_cs_code = csCode,
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

        #region TR-55 Time of Concentration

        /// <summary>
        /// Calculate TR-55 Time of Concentration for existing catchments.
        /// Uses WhiteboxTools for flow path extraction and TR-55 segmented method.
        /// Writes Tc back to each Catchment object's TimeOfConcentration property.
        /// </summary>
        [CommandMethod("CALCULATETC")]
        public void CalculateTimeOfConcentration()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            var db = doc.Database;

            var stopwatch = Stopwatch.StartNew();

            try
            {
                ed.WriteMessage("\n");
                ed.WriteMessage("╔══════════════════════════════════════════════════════════════╗\n");
                ed.WriteMessage("║       TR-55 TIME OF CONCENTRATION CALCULATOR                ║\n");
                ed.WriteMessage("║       Computes Tc for existing catchments via DEM analysis   ║\n");
                ed.WriteMessage("╚══════════════════════════════════════════════════════════════╝\n\n");

                // ═══════════════════════════════════════════════════════════════
                // STEP 1: Select inputs
                // ═══════════════════════════════════════════════════════════════
                ed.WriteMessage("┌─────────────────────────────────────────────────────────────┐\n");
                ed.WriteMessage("│ STEP 1/4: SELECT INPUTS                                     │\n");
                ed.WriteMessage("└─────────────────────────────────────────────────────────────┘\n");

                // Select surface
                ed.WriteMessage("\n  [1.1] Selecting surface for DEM export...\n");
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
                ed.WriteMessage($"  ✓ Surface: {surfaceName}\n");

                // Find catchments in the drawing
                ed.WriteMessage("\n  [1.2] Scanning for catchment objects...\n");
                var catchmentIds = FindCatchmentIds(db);
                if (catchmentIds.Count == 0)
                {
                    ed.WriteMessage("  ✗ No catchment objects found in this drawing.\n");
                    ed.WriteMessage("    Run AUTOCATCHMENTS first to create catchments.\n");
                    return;
                }
                ed.WriteMessage($"  ✓ Found {catchmentIds.Count} catchments\n");

                // Get P2 rainfall via map dialog (NOAA Atlas 14)
                ed.WriteMessage("\n  [1.3] Opening rainfall map...\n");
                double p2Rainfall = 3.5; // fallback default
                try
                {
                    var mapDialog = new RainfallMapDialog();
                    var dialogResult = Autodesk.AutoCAD.ApplicationServices.Application
                        .ShowModalWindow(mapDialog);

                    if (dialogResult == true && mapDialog.P2RainfallInches > 0)
                    {
                        p2Rainfall = mapDialog.P2RainfallInches;
                        ed.WriteMessage($"  ✓ P₂ rainfall: {p2Rainfall:F2} inches (NOAA Atlas 14)\n");

                        if (!double.IsNaN(mapDialog.SelectedLatitude))
                        {
                            ed.WriteMessage($"    Location: {mapDialog.SelectedLatitude:F4}, {mapDialog.SelectedLongitude:F4}\n");
                        }
                    }
                    else
                    {
                        ed.WriteMessage($"  ✓ P₂ rainfall: {p2Rainfall:F2} inches (default)\n");
                    }
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"  ! Map dialog unavailable ({ex.Message}), using default P₂.\n");

                    // Fall back to manual prompt
                    var p2Opts = new PromptDoubleOptions("\n  Enter 2-year, 24-hour rainfall depth (inches) [3.5]: ");
                    p2Opts.DefaultValue = 3.5;
                    p2Opts.AllowNone = true;
                    var p2PromptResult = ed.GetDouble(p2Opts);
                    if (p2PromptResult.Status == PromptStatus.OK)
                        p2Rainfall = p2PromptResult.Value;
                    ed.WriteMessage($"  ✓ P₂ rainfall: {p2Rainfall:F2} inches\n");
                }

                // Prompt for Manning's n (sheet flow)
                var nSheetOpts = new PromptDoubleOptions("\n  Enter Manning's n for sheet flow [0.15]: ");
                nSheetOpts.DefaultValue = 0.15;
                nSheetOpts.AllowNone = true;
                var nSheetResult = ed.GetDouble(nSheetOpts);
                double nSheet = nSheetResult.Status == PromptStatus.OK ? nSheetResult.Value : 0.15;
                ed.WriteMessage($"  ✓ Manning's n (sheet): {nSheet:F3}\n");

                // ═══════════════════════════════════════════════════════════════
                // STEP 2: Export DEM and catchment boundaries
                // ═══════════════════════════════════════════════════════════════
                ed.WriteMessage("\n┌─────────────────────────────────────────────────────────────┐\n");
                ed.WriteMessage("│ STEP 2/4: EXPORT SURFACE AND CATCHMENT BOUNDARIES           │\n");
                ed.WriteMessage("└─────────────────────────────────────────────────────────────┘\n");

                string tempDir = Path.Combine(Path.GetTempPath(),
                    "CatchmentTool_Tc_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tempDir);
                ed.WriteMessage($"\n  [2.1] Working directory: {tempDir}\n");

                // Export surface to DEM
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

                // Export catchment boundaries to GeoJSON
                ed.WriteMessage("\n  [2.3] Exporting catchment boundaries...\n");
                string catchmentsPath = Path.Combine(tempDir, "catchments_for_tc.json");
                ExportCatchmentBoundaries(catchmentIds, db, catchmentsPath);
                ed.WriteMessage($"  ✓ Exported {catchmentIds.Count} catchment boundaries\n");

                // ═══════════════════════════════════════════════════════════════
                // STEP 3: Run Python Tc calculation
                // ═══════════════════════════════════════════════════════════════
                ed.WriteMessage("\n┌─────────────────────────────────────────────────────────────┐\n");
                ed.WriteMessage("│ STEP 3/4: COMPUTE TR-55 Tc (WhiteboxTools + Python)          │\n");
                ed.WriteMessage("└─────────────────────────────────────────────────────────────┘\n");

                ed.WriteMessage("\n  [3.1] Starting TR-55 Tc calculation...\n");
                ed.WriteMessage("        - DEM conditioning (breach depressions)\n");
                ed.WriteMessage("        - D8 flow direction + accumulation\n");
                ed.WriteMessage("        - Longest flow path extraction per catchment\n");
                ed.WriteMessage("        - Flow path segmentation + TR-55 equations\n\n");

                // Detect drawing units
                var drawingUnits = DrawingUnits.Detect(doc);

                string tcOutputPath = Path.Combine(tempDir, "tc_results.json");
                string tcPythonResult = RunPythonTcCalculation(
                    surfaceResult.OutputPath, catchmentsPath, tcOutputPath,
                    tempDir, p2Rainfall, nSheet, drawingUnits.UnitLabel, ed);

                if (!File.Exists(tcOutputPath))
                {
                    ed.WriteMessage($"  ✗ Tc calculation failed. Python output:\n{tcPythonResult}\n");
                    return;
                }
                ed.WriteMessage($"  ✓ Tc calculation complete\n");

                // ═══════════════════════════════════════════════════════════════
                // STEP 4: Write Tc values to catchment objects
                // ═══════════════════════════════════════════════════════════════
                ed.WriteMessage("\n┌─────────────────────────────────────────────────────────────┐\n");
                ed.WriteMessage("│ STEP 4/4: UPDATE CATCHMENT Tc VALUES                        │\n");
                ed.WriteMessage("└─────────────────────────────────────────────────────────────┘\n");

                string tcJson = File.ReadAllText(tcOutputPath);
                var tcResults = Newtonsoft.Json.JsonConvert.DeserializeObject<
                    List<Dictionary<string, object>>>(tcJson);

                int updated = 0;
                int failed = 0;

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (var tcResult in tcResults)
                    {
                        string catchmentId = tcResult.ContainsKey("catchmentId")
                            ? tcResult["catchmentId"].ToString() : "";
                        double tcMinutes = tcResult.ContainsKey("tcMinutes")
                            ? Convert.ToDouble(tcResult["tcMinutes"]) : -1;

                        if (tcMinutes < 0)
                        {
                            string err = tcResult.ContainsKey("error") ? tcResult["error"].ToString() : "unknown";
                            ed.WriteMessage($"\n  ✗ {catchmentId}: {err}");
                            failed++;
                            continue;
                        }

                        // Find the matching catchment object
                        ObjectId matchedId = FindCatchmentByName(catchmentIds, catchmentId, db, tr);
                        if (matchedId.IsNull)
                        {
                            ed.WriteMessage($"\n  ? {catchmentId}: Tc = {tcMinutes:F1} min (no matching catchment object)");
                            failed++;
                            continue;
                        }

                        // Write Tc to the catchment
                        if (SetCatchmentTc(matchedId, tcMinutes, tr, ed))
                        {
                            ed.WriteMessage($"\n  ✓ {catchmentId}: Tc = {tcMinutes:F1} min");
                            updated++;
                        }
                        else
                        {
                            ed.WriteMessage($"\n  ✗ {catchmentId}: Tc = {tcMinutes:F1} min (failed to write)");
                            failed++;
                        }
                    }

                    tr.Commit();
                }

                // ═══════════════════════════════════════════════════════════════
                // COMPLETE
                // ═══════════════════════════════════════════════════════════════
                stopwatch.Stop();

                ed.WriteMessage("\n\n╔══════════════════════════════════════════════════════════════╗\n");
                ed.WriteMessage("║                 TC CALCULATION COMPLETE!                     ║\n");
                ed.WriteMessage("╚══════════════════════════════════════════════════════════════╝\n\n");
                ed.WriteMessage($"  Summary:\n");
                ed.WriteMessage($"    • Surface analyzed: {surfaceName}\n");
                ed.WriteMessage($"    • Catchments processed: {tcResults.Count}\n");
                ed.WriteMessage($"    • Tc values updated: {updated}\n");
                if (failed > 0)
                    ed.WriteMessage($"    • Failed: {failed}\n");
                ed.WriteMessage($"    • P₂ rainfall used: {p2Rainfall:F2} inches\n");
                ed.WriteMessage($"    • Processing time: {stopwatch.Elapsed.TotalSeconds:F1} seconds\n");
                ed.WriteMessage($"\n  Detailed results: {tcOutputPath}\n");
                ed.WriteMessage($"  Working directory: {tempDir}\n\n");
            }
            catch (System.Exception ex)
            {
                stopwatch.Stop();
                ed.WriteMessage($"\n  ✗ ERROR: {ex.Message}\n");
                ed.WriteMessage($"  Stack trace: {ex.StackTrace}\n");
            }
        }

        /// <summary>
        /// Find all Catchment object IDs in the drawing.
        /// </summary>
        private List<ObjectId> FindCatchmentIds(Database db)
        {
            var ids = new List<ObjectId>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                var ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;

                foreach (ObjectId objId in ms)
                {
                    try
                    {
                        var catchment = tr.GetObject(objId, OpenMode.ForRead) as Catchment;
                        if (catchment != null)
                            ids.Add(objId);
                    }
                    catch { }
                }

                tr.Commit();
            }

            return ids;
        }

        /// <summary>
        /// Export catchment boundaries to a GeoJSON file for the Python Tc script.
        /// Each feature includes the catchment name as its ID.
        /// </summary>
        private void ExportCatchmentBoundaries(List<ObjectId> catchmentIds, Database db, string outputPath)
        {
            var features = new List<object>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var objId in catchmentIds)
                {
                    try
                    {
                        var catchment = tr.GetObject(objId, OpenMode.ForRead) as Catchment;
                        if (catchment == null) continue;

                        // Extract boundary vertices using reflection (version-safe)
                        var coords = ExtractCatchmentBoundaryCoords(catchment, tr);
                        if (coords == null || coords.Count < 3) continue;

                        // Close the ring if needed
                        var first = coords[0];
                        var last = coords[coords.Count - 1];
                        if (Math.Abs(first[0] - last[0]) > 0.001 || Math.Abs(first[1] - last[1]) > 0.001)
                            coords.Add(new double[] { first[0], first[1] });

                        var feature = new
                        {
                            type = "Feature",
                            properties = new
                            {
                                catchment_id = catchment.Name ?? $"catchment_{objId.Handle}",
                                name = catchment.Name ?? "",
                            },
                            geometry = new
                            {
                                type = "Polygon",
                                coordinates = new[] { coords }
                            }
                        };

                        features.Add(feature);
                    }
                    catch { }
                }

                tr.Commit();
            }

            var geojson = new
            {
                type = "FeatureCollection",
                features = features
            };

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(geojson, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(outputPath, json);
        }

        /// <summary>
        /// Extract catchment boundary coordinates using reflection (version-safe).
        /// Tries multiple API paths: BoundaryPolylineId, Vertices, GeometricExtents.
        /// </summary>
        private List<double[]> ExtractCatchmentBoundaryCoords(Catchment catchment, Transaction tr)
        {
            var catchmentType = catchment.GetType();

            // Try 1: BoundaryPolylineId -> read the polyline
            try
            {
                var boundaryProp = catchmentType.GetProperty("BoundaryPolylineId");
                if (boundaryProp != null)
                {
                    var polyId = (ObjectId)boundaryProp.GetValue(catchment);
                    if (!polyId.IsNull)
                    {
                        var polyline = tr.GetObject(polyId, OpenMode.ForRead) as Polyline;
                        if (polyline != null)
                        {
                            var coords = new List<double[]>();
                            for (int i = 0; i < polyline.NumberOfVertices; i++)
                            {
                                var pt = polyline.GetPoint2dAt(i);
                                coords.Add(new double[] { pt.X, pt.Y });
                            }
                            if (coords.Count >= 3) return coords;
                        }
                    }
                }
            }
            catch { }

            // Try 2: Vertices property
            try
            {
                var verticesProp = catchmentType.GetProperty("Vertices");
                if (verticesProp != null)
                {
                    var vertices = verticesProp.GetValue(catchment);
                    if (vertices is Point2dCollection pts)
                    {
                        var coords = new List<double[]>();
                        foreach (Point2d pt in pts)
                            coords.Add(new double[] { pt.X, pt.Y });
                        if (coords.Count >= 3) return coords;
                    }
                    else if (vertices is Point3dCollection pts3d)
                    {
                        var coords = new List<double[]>();
                        foreach (Point3d pt in pts3d)
                            coords.Add(new double[] { pt.X, pt.Y });
                        if (coords.Count >= 3) return coords;
                    }
                }
            }
            catch { }

            // Try 3: Treat as a curve
            try
            {
                if (catchment is Curve curve)
                {
                    var coords = new List<double[]>();
                    double startParam = curve.StartParam;
                    double endParam = curve.EndParam;
                    int steps = Math.Max(20, (int)(endParam - startParam));

                    for (int i = 0; i <= steps; i++)
                    {
                        double param = startParam + (endParam - startParam) * i / steps;
                        Point3d pt = curve.GetPointAtParameter(param);
                        coords.Add(new double[] { pt.X, pt.Y });
                    }
                    if (coords.Count >= 3) return coords;
                }
            }
            catch { }

            // Try 4: GeometricExtents (bounding box fallback)
            try
            {
                var extents = catchment.GeometricExtents;
                return new List<double[]>
                {
                    new double[] { extents.MinPoint.X, extents.MinPoint.Y },
                    new double[] { extents.MaxPoint.X, extents.MinPoint.Y },
                    new double[] { extents.MaxPoint.X, extents.MaxPoint.Y },
                    new double[] { extents.MinPoint.X, extents.MaxPoint.Y },
                    new double[] { extents.MinPoint.X, extents.MinPoint.Y },
                };
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Run the Python TR-55 Tc calculation script.
        /// </summary>
        private string RunPythonTcCalculation(string demPath, string catchmentsPath,
                                              string outputPath, string workingDir,
                                              double p2Rainfall, double nSheet,
                                              string drawingUnits, Editor ed)
        {
            var pyEnv = new PythonEnvironment();
            pyEnv.StatusCallback = (msg) => ed.WriteMessage($"        {msg}\n");

            if (!pyEnv.Discover())
            {
                if (pyEnv.PythonPath == null)
                {
                    ed.WriteMessage("  ✗ Python 3 not found.\n");
                    ed.WriteMessage("    Install Python 3.10+ from https://www.python.org/downloads/\n");
                }
                else
                {
                    ed.WriteMessage("  ✗ Python scripts not found. Reinstall the CatchmentTool bundle.\n");
                }
                return "Python environment not available";
            }

            ed.WriteMessage($"  [3.1] {pyEnv.PythonVersion}\n");

            // Check packages
            var missing = pyEnv.CheckMissingPackages();
            if (missing.Count > 0)
            {
                ed.WriteMessage($"  [3.2] Installing: {string.Join(", ", missing)}...\n");
                if (!pyEnv.InstallPackages(missing))
                {
                    return "Package installation failed";
                }
            }

            // Find the run_tc.py script (same directory as catchment_delineation.py)
            string scriptDir = Path.GetDirectoryName(pyEnv.ScriptPath) ?? "";
            string tcScript = Path.Combine(scriptDir, "run_tc.py");

            if (!File.Exists(tcScript))
            {
                ed.WriteMessage($"  ✗ run_tc.py not found at: {tcScript}\n");
                return "run_tc.py not found";
            }

            // Build arguments
            string arguments = $"--dem \"{demPath}\" " +
                              $"--catchments \"{catchmentsPath}\" " +
                              $"--output \"{outputPath}\" " +
                              $"--working-dir \"{workingDir}\" " +
                              $"--drawing-units {drawingUnits} " +
                              $"--p2-rainfall {p2Rainfall:F2} " +
                              $"--mannings-n-sheet {nSheet:F3}";

            // Build ProcessStartInfo manually for run_tc.py
            string fullArgs = pyEnv.PythonPath == "py"
                ? $"-3 \"{tcScript}\" {arguments}"
                : $"\"{tcScript}\" {arguments}";

            var startInfo = new ProcessStartInfo
            {
                FileName = pyEnv.PythonPath,
                Arguments = fullArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = scriptDir
            };

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
                            if (e.Data.Contains("Step") || e.Data.Contains("COMPLETE") ||
                                e.Data.Contains("Tc =") || e.Data.Contains("ERROR") ||
                                e.Data.Contains("catchment"))
                            {
                                ed.WriteMessage($"        {e.Data}\n");
                            }
                        }
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                            error.AppendLine(e.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    bool completed = process.WaitForExit(600000); // 10 min timeout for Tc

                    if (!completed)
                    {
                        process.Kill();
                        return "Python script timed out after 10 minutes";
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
        /// Find a catchment ObjectId by name match.
        /// </summary>
        private ObjectId FindCatchmentByName(List<ObjectId> catchmentIds, string name,
                                             Database db, Transaction tr)
        {
            foreach (var objId in catchmentIds)
            {
                try
                {
                    var catchment = tr.GetObject(objId, OpenMode.ForRead) as Catchment;
                    if (catchment != null &&
                        string.Equals(catchment.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return objId;
                    }
                }
                catch { }
            }

            // Try partial match (catchment name might be "CA-xxx" while result uses "xxx")
            foreach (var objId in catchmentIds)
            {
                try
                {
                    var catchment = tr.GetObject(objId, OpenMode.ForRead) as Catchment;
                    if (catchment != null)
                    {
                        string cName = catchment.Name ?? "";
                        // Check if catchment name ends with the result name
                        if (cName.EndsWith(name, StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(cName, StringComparison.OrdinalIgnoreCase))
                        {
                            return objId;
                        }
                    }
                }
                catch { }
            }

            return ObjectId.Null;
        }

        /// <summary>
        /// Set the TimeOfConcentration property on a Catchment object.
        /// Uses reflection for version-safe access.
        /// </summary>
        private bool SetCatchmentTc(ObjectId catchmentId, double tcMinutes,
                                    Transaction tr, Editor ed)
        {
            try
            {
                var catchment = tr.GetObject(catchmentId, OpenMode.ForWrite) as Catchment;
                if (catchment == null) return false;

                var catchmentType = catchment.GetType();

                // Try TimeOfConcentration property (minutes)
                var tcProp = catchmentType.GetProperty("TimeOfConcentration");
                if (tcProp != null && tcProp.CanWrite)
                {
                    tcProp.SetValue(catchment, tcMinutes);
                    return true;
                }

                // Try Tc property
                tcProp = catchmentType.GetProperty("Tc");
                if (tcProp != null && tcProp.CanWrite)
                {
                    tcProp.SetValue(catchment, tcMinutes);
                    return true;
                }

                // Try setting via method
                var setMethod = catchmentType.GetMethod("SetTimeOfConcentration");
                if (setMethod != null)
                {
                    setMethod.Invoke(catchment, new object[] { tcMinutes });
                    return true;
                }

                ed.WriteMessage($"\n    (TimeOfConcentration property not found via reflection)");
                return false;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n    Error setting Tc: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}

