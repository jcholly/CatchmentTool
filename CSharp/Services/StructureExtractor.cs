using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace CatchmentTool.Services
{
    /// <summary>
    /// Extracts inlet structures from Civil 3D pipe networks for use as pour points.
    /// 
    /// Filters structures to only include inlets (catch basins, area drains, etc.)
    /// based on structure family/part name patterns.
    /// </summary>
    public class StructureExtractor
    {
        private readonly Document _doc;
        private readonly Database _db;
        
        // Common inlet structure name patterns (case-insensitive)
        private static readonly string[] InletPatterns = new[]
        {
            "inlet",
            "catch basin",
            "catchbasin",
            "area drain",
            "areadrain",
            "curb inlet",
            "curbinlet",
            "grate",
            "drop inlet",
            "dropinlet",
            "storm inlet",
            "storminlet",
            "cb",  // common abbreviation
            "di",  // drop inlet abbreviation
        };
        
        public StructureExtractor(Document doc)
        {
            _doc = doc;
            _db = doc.Database;
        }
        
        /// <summary>
        /// Extract inlet structures from a pipe network.
        /// </summary>
        /// <param name="networkId">ObjectId of the pipe network</param>
        /// <param name="outputPath">Path for output shapefile</param>
        /// <param name="customFilter">Optional custom filter function</param>
        /// <returns>Extraction result with metadata</returns>
        public StructureExtractionResult Extract(ObjectId networkId, string outputPath, 
                                                  Func<Structure, bool> customFilter = null)
        {
            var result = new StructureExtractionResult();
            var structures = new List<InletStructureInfo>();
            
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var network = tr.GetObject(networkId, OpenMode.ForRead) as Network;
                if (network == null)
                {
                    throw new ArgumentException("Selected object is not a Pipe Network");
                }
                
                result.NetworkName = network.Name;
                _doc.Editor.WriteMessage($"\nExtracting inlet structures from '{network.Name}'...");
                
                // Get all structures in the network
                var structureIds = network.GetStructureIds();
                int totalStructures = structureIds.Count;
                int inletCount = 0;
                
                _doc.Editor.WriteMessage($"\n  Total structures: {totalStructures}");
                
                foreach (ObjectId structId in structureIds)
                {
                    var structure = tr.GetObject(structId, OpenMode.ForRead) as Structure;
                    if (structure == null) continue;
                    
                    // Check if this is an inlet structure
                    bool isInlet = customFilter?.Invoke(structure) ?? IsInletStructure(structure);
                    
                    if (isInlet)
                    {
                        var info = new InletStructureInfo
                        {
                            ObjectId = structId,
                            StructureId = structure.Handle.ToString(),
                            Name = structure.Name,
                            PartFamilyName = GetPartFamilyName(structure),
                            X = structure.Position.X,
                            Y = structure.Position.Y,
                            RimElevation = structure.RimElevation,
                            SumpElevation = structure.SumpElevation,
                            Description = structure.Description ?? ""
                        };
                        
                        structures.Add(info);
                        inletCount++;
                    }
                }
                
                _doc.Editor.WriteMessage($"\n  Inlet structures found: {inletCount}");
                
                if (structures.Count == 0)
                {
                    _doc.Editor.WriteMessage("\n  WARNING: No inlet structures found!");
                    _doc.Editor.WriteMessage("\n  Check that your pipe network contains structures");
                    _doc.Editor.WriteMessage("\n  with names containing: inlet, catch basin, area drain, etc.");
                }
                
                tr.Commit();
            }
            
            // Write to shapefile format (using simple DBF + SHP approach)
            WriteShapefile(outputPath, structures);
            
            result.Structures = structures;
            result.OutputPath = outputPath;
            result.Success = true;
            
            return result;
        }
        
        /// <summary>
        /// Determine if a structure is an inlet based on its name/family.
        /// </summary>
        private bool IsInletStructure(Structure structure)
        {
            // Check structure name
            string name = structure.Name?.ToLowerInvariant() ?? "";
            string partFamily = GetPartFamilyName(structure)?.ToLowerInvariant() ?? "";
            string description = structure.Description?.ToLowerInvariant() ?? "";
            
            string combined = $"{name} {partFamily} {description}";
            
            foreach (var pattern in InletPatterns)
            {
                if (combined.Contains(pattern))
                {
                    return true;
                }
            }
            
            // Additional check: structures with no pipes connected at the start
            // (terminal structures that could be inlets)
            // This is a heuristic - may need refinement
            
            return false;
        }
        
        /// <summary>
        /// Get the part family name for a structure.
        /// </summary>
        private string GetPartFamilyName(Structure structure)
        {
            try
            {
                // Try direct property first (most reliable)
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
                
                // Fallback: try to get from structure's part size name
                if (!string.IsNullOrEmpty(structure.PartSizeName))
                {
                    return structure.PartSizeName;
                }
            }
            catch { }
            
            return "Unknown";
        }
        
        /// <summary>
        /// Write structures to a shapefile for WhiteboxTools.
        /// Creates a simple point shapefile with attribute table.
        /// </summary>
        private void WriteShapefile(string basePath, List<InletStructureInfo> structures)
        {
            string shpPath = Path.ChangeExtension(basePath, ".shp");
            string shxPath = Path.ChangeExtension(basePath, ".shx");
            string dbfPath = Path.ChangeExtension(basePath, ".dbf");
            string jsonPath = Path.ChangeExtension(basePath, ".json");
            
            // Write as GeoJSON (easier for Python to read, converts to SHP easily)
            WriteGeoJson(jsonPath, structures);
            
            // Write simple CSV as backup
            string csvPath = Path.ChangeExtension(basePath, ".csv");
            WriteCsv(csvPath, structures);
            
            _doc.Editor.WriteMessage($"\n  Written: {jsonPath}");
            _doc.Editor.WriteMessage($"\n  Written: {csvPath}");
        }
        
        /// <summary>
        /// Write structures to GeoJSON format.
        /// </summary>
        private void WriteGeoJson(string path, List<InletStructureInfo> structures)
        {
            var features = structures.Select(s => new
            {
                type = "Feature",
                geometry = new
                {
                    type = "Point",
                    coordinates = new[] { s.X, s.Y }
                },
                properties = new
                {
                    // Use Name as primary ID for matching (Handle as backup)
                    structure_id = s.Name,  // Primary: use structure Name
                    structure_handle = s.StructureId,  // Backup: original Handle
                    name = s.Name,
                    part_family = s.PartFamilyName,
                    rim_elev = s.RimElevation,
                    sump_elev = s.SumpElevation,
                    description = s.Description
                }
            }).ToList();
            
            var geojson = new
            {
                type = "FeatureCollection",
                features = features
            };
            
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(geojson, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(path, json);
        }
        
        /// <summary>
        /// Write structures to CSV format as backup.
        /// </summary>
        private void WriteCsv(string path, List<InletStructureInfo> structures)
        {
            using (var sw = new StreamWriter(path))
            {
                sw.WriteLine("structure_id,name,part_family,x,y,rim_elev,sump_elev,description");
                
                foreach (var s in structures)
                {
                    sw.WriteLine($"\"{s.StructureId}\",\"{s.Name}\",\"{s.PartFamilyName}\",{s.X:F6},{s.Y:F6},{s.RimElevation:F4},{s.SumpElevation:F4},\"{s.Description}\"");
                }
            }
        }
        
        /// <summary>
        /// Extract structures filtered by specific part family types.
        /// </summary>
        /// <param name="networkId">ObjectId of the pipe network</param>
        /// <param name="outputPath">Path for output shapefile</param>
        /// <param name="selectedTypes">List of part family names to include</param>
        /// <returns>Extraction result with metadata</returns>
        public StructureExtractionResult ExtractByTypes(ObjectId networkId, string outputPath, 
                                                         List<string> selectedTypes)
        {
            var result = new StructureExtractionResult();
            var structures = new List<InletStructureInfo>();
            
            // Create a case-insensitive set of selected types
            var typeSet = new HashSet<string>(selectedTypes, StringComparer.OrdinalIgnoreCase);
            
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var network = tr.GetObject(networkId, OpenMode.ForRead) as Network;
                if (network == null)
                {
                    throw new ArgumentException("Selected object is not a Pipe Network");
                }
                
                result.NetworkName = network.Name;
                _doc.Editor.WriteMessage($"\nExtracting structures from '{network.Name}'...");
                _doc.Editor.WriteMessage($"\n  Filtering by {selectedTypes.Count} structure types");
                
                // Get all structures in the network
                var structureIds = network.GetStructureIds();
                int totalStructures = structureIds.Count;
                int matchedCount = 0;
                
                _doc.Editor.WriteMessage($"\n  Total structures: {totalStructures}");
                
                foreach (ObjectId structId in structureIds)
                {
                    var structure = tr.GetObject(structId, OpenMode.ForRead) as Structure;
                    if (structure == null) continue;
                    
                    string partFamily = GetPartFamilyName(structure);
                    
                    // Check if this structure's type is in the selected list
                    if (typeSet.Contains(partFamily))
                    {
                        var info = new InletStructureInfo
                        {
                            ObjectId = structId,
                            StructureId = structure.Handle.ToString(),
                            Name = structure.Name,
                            PartFamilyName = partFamily,
                            X = structure.Position.X,
                            Y = structure.Position.Y,
                            RimElevation = structure.RimElevation,
                            SumpElevation = structure.SumpElevation,
                            Description = structure.Description ?? "",
                            IsInlet = true
                        };
                        
                        structures.Add(info);
                        matchedCount++;
                    }
                }
                
                _doc.Editor.WriteMessage($"\n  Structures matching selected types: {matchedCount}");
                
                tr.Commit();
            }
            
            // Write to shapefile format
            WriteShapefile(outputPath, structures);
            
            result.Structures = structures;
            result.OutputPath = outputPath;
            result.Success = true;
            
            return result;
        }
        
        /// <summary>
        /// Get all available structures for user selection (not just inlets).
        /// </summary>
        public List<InletStructureInfo> GetAllStructures(ObjectId networkId)
        {
            var structures = new List<InletStructureInfo>();
            
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var network = tr.GetObject(networkId, OpenMode.ForRead) as Network;
                if (network == null) return structures;
                
                foreach (ObjectId structId in network.GetStructureIds())
                {
                    var structure = tr.GetObject(structId, OpenMode.ForRead) as Structure;
                    if (structure == null) continue;
                    
                    structures.Add(new InletStructureInfo
                    {
                        ObjectId = structId,
                        StructureId = structure.Handle.ToString(),
                        Name = structure.Name,
                        PartFamilyName = GetPartFamilyName(structure),
                        X = structure.Position.X,
                        Y = structure.Position.Y,
                        RimElevation = structure.RimElevation,
                        SumpElevation = structure.SumpElevation,
                        IsInlet = IsInletStructure(structure)
                    });
                }
                
                tr.Commit();
            }
            
            return structures;
        }
        
        /// <summary>
        /// Extract pipe lines from a network for burning into DEM.
        /// </summary>
        /// <param name="networkId">ObjectId of the pipe network</param>
        /// <param name="outputPath">Path for output GeoJSON file</param>
        /// <returns>Number of pipes extracted</returns>
        public int ExtractPipeLines(ObjectId networkId, string outputPath)
        {
            var pipes = new List<PipeLineInfo>();
            
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var network = tr.GetObject(networkId, OpenMode.ForRead) as Network;
                if (network == null)
                {
                    _doc.Editor.WriteMessage("\n  Error: Could not open pipe network");
                    return 0;
                }
                
                foreach (ObjectId pipeId in network.GetPipeIds())
                {
                    var pipe = tr.GetObject(pipeId, OpenMode.ForRead) as Pipe;
                    if (pipe == null) continue;
                    
                    // Get pipe endpoints
                    var startPt = pipe.StartPoint;
                    var endPt = pipe.EndPoint;
                    
                    // Get pipe invert elevations
                    double startInvert = pipe.StartPoint.Z;
                    double endInvert = pipe.EndPoint.Z;
                    
                    pipes.Add(new PipeLineInfo
                    {
                        PipeId = pipe.Name ?? pipeId.ToString(),
                        StartX = startPt.X,
                        StartY = startPt.Y,
                        StartZ = startInvert,
                        EndX = endPt.X,
                        EndY = endPt.Y,
                        EndZ = endInvert,
                        Diameter = pipe.InnerDiameterOrWidth
                    });
                }
                
                tr.Commit();
            }
            
            // Write to GeoJSON
            WritePipeGeoJson(outputPath, pipes);
            
            _doc.Editor.WriteMessage($"\n  Extracted {pipes.Count} pipe lines");
            _doc.Editor.WriteMessage($"\n  Written: {outputPath}");
            
            return pipes.Count;
        }
        
        /// <summary>
        /// Write pipe lines to GeoJSON format.
        /// </summary>
        private void WritePipeGeoJson(string path, List<PipeLineInfo> pipes)
        {
            var features = pipes.Select(p => new
            {
                type = "Feature",
                geometry = new
                {
                    type = "LineString",
                    coordinates = new[]
                    {
                        new[] { p.StartX, p.StartY },
                        new[] { p.EndX, p.EndY }
                    }
                },
                properties = new
                {
                    pipe_id = p.PipeId,
                    start_invert = p.StartZ,
                    end_invert = p.EndZ,
                    diameter = p.Diameter
                }
            }).ToList();
            
            var geojson = new
            {
                type = "FeatureCollection",
                features = features
            };
            
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(geojson, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(path, json);
        }
    }
    
    /// <summary>
    /// Information about a pipe line.
    /// </summary>
    public class PipeLineInfo
    {
        public string PipeId { get; set; }
        public double StartX { get; set; }
        public double StartY { get; set; }
        public double StartZ { get; set; }
        public double EndX { get; set; }
        public double EndY { get; set; }
        public double EndZ { get; set; }
        public double Diameter { get; set; }
    }
    
    /// <summary>
    /// Information about an inlet structure.
    /// </summary>
    public class InletStructureInfo
    {
        public ObjectId ObjectId { get; set; }
        public string StructureId { get; set; }
        public string Name { get; set; }
        public string PartFamilyName { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double RimElevation { get; set; }
        public double SumpElevation { get; set; }
        public string Description { get; set; }
        public bool IsInlet { get; set; }
    }
    
    /// <summary>
    /// Result of structure extraction.
    /// </summary>
    public class StructureExtractionResult
    {
        public bool Success { get; set; }
        public string NetworkName { get; set; }
        public string OutputPath { get; set; }
        public List<InletStructureInfo> Structures { get; set; } = new List<InletStructureInfo>();
    }
}

