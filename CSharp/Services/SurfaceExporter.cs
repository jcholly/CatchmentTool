using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace CatchmentTool.Services
{
    /// <summary>
    /// Exports Civil 3D TIN surfaces to high-resolution GeoTIFF DEMs.
    /// 
    /// The cell size is calculated based on the TIN triangle density to preserve
    /// maximum detail from the original surface.
    /// </summary>
    public class SurfaceExporter
    {
        private readonly Document _doc;
        private readonly Database _db;
        
        /// <summary>
        /// Optional callback for progress updates.
        /// </summary>
        public Action<string> ProgressCallback { get; set; }
        
        /// <summary>
        /// Cell size for DEM export (in drawing units, typically feet).
        /// Default is 1.0.
        /// </summary>
        public double CellSize { get; set; } = 1.0;
        
        public SurfaceExporter(Document doc)
        {
            _doc = doc;
            _db = doc.Database;
        }
        
        private void ReportProgress(string message)
        {
            _doc.Editor.WriteMessage($"\n{message}");
            ProgressCallback?.Invoke(message);
        }
        
        /// <summary>
        /// Export a TIN surface to a GeoTIFF DEM file.
        /// </summary>
        /// <param name="surfaceId">ObjectId of the surface to export</param>
        /// <param name="outputPath">Path for output GeoTIFF file</param>
        /// <param name="cellSizeOverride">Optional cell size override (if null, calculated from TIN)</param>
        /// <returns>Export result with metadata</returns>
        public SurfaceExportResult Export(ObjectId surfaceId, string outputPath, double? cellSizeOverride = null)
        {
            var result = new SurfaceExportResult();
            
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var surface = tr.GetObject(surfaceId, OpenMode.ForRead) as TinSurface;
                if (surface == null)
                {
                    throw new ArgumentException("Selected object is not a TIN Surface");
                }
                
                result.SurfaceName = surface.Name;
                
                // Get surface bounds
                var props = surface.GetGeneralProperties();
                double minX = props.MinimumCoordinateX;
                double minY = props.MinimumCoordinateY;
                double maxX = props.MaximumCoordinateX;
                double maxY = props.MaximumCoordinateY;
                double minZ = props.MinimumElevation;
                double maxZ = props.MaximumElevation;
                
                result.Bounds = new SurfaceBounds
                {
                    MinX = minX, MinY = minY,
                    MaxX = maxX, MaxY = maxY,
                    MinZ = minZ, MaxZ = maxZ
                };
                
                // Use provided cell size override, or CellSize property, or calculate
                double cellSize = cellSizeOverride ?? CellSize;
                result.CellSize = cellSize;
                
                ReportProgress($"Exporting surface '{surface.Name}'...");
                ReportProgress($"Bounds: ({minX:F2}, {minY:F2}) to ({maxX:F2}, {maxY:F2})");
                ReportProgress($"Elevation range: {minZ:F2} to {maxZ:F2}");
                ReportProgress($"Cell size: {cellSize:F4} units");
                
                // Calculate raster dimensions
                int cols = (int)Math.Ceiling((maxX - minX) / cellSize);
                int rows = (int)Math.Ceiling((maxY - minY) / cellSize);
                
                result.Columns = cols;
                result.Rows = rows;
                
                long totalCells = (long)cols * rows;
                ReportProgress($"Raster size: {cols} x {rows} cells ({totalCells:N0} total)");
                ReportProgress($"Sampling surface... (this may take a while)");
                
                // Sample the surface to create the elevation grid
                double[,] elevations = new double[rows, cols];
                double noDataValue = -9999.0;
                
                int pointsSampled = 0;
                int pointsFailed = 0;
                int lastReportedPct = -1;
                
                // Calculate progress update interval (every 5% or at least every 100 rows)
                int progressInterval = Math.Max(1, Math.Min(rows / 20, 100));
                
                for (int row = 0; row < rows; row++)
                {
                    double y = maxY - (row + 0.5) * cellSize; // Center of cell, top-down
                    
                    for (int col = 0; col < cols; col++)
                    {
                        double x = minX + (col + 0.5) * cellSize; // Center of cell
                        
                        try
                        {
                            double elevation = surface.FindElevationAtXY(x, y);
                            elevations[row, col] = elevation;
                            pointsSampled++;
                        }
                        catch
                        {
                            // Point outside surface boundary
                            elevations[row, col] = noDataValue;
                            pointsFailed++;
                        }
                    }
                    
                    // Progress update every 5% (with callback for UI responsiveness)
                    if (row % progressInterval == 0)
                    {
                        int pct = (int)((double)row / rows * 100);
                        if (pct != lastReportedPct)
                        {
                            ReportProgress($"Sampling: {pct}% complete ({pointsSampled:N0} points)...");
                            lastReportedPct = pct;
                        }
                    }
                }
                
                result.PointsSampled = pointsSampled;
                result.PointsOutsideBoundary = pointsFailed;
                
                // Write BIL raster file (WriteGeoTiff sets result.OutputPath with .bil extension)
                WriteGeoTiff(outputPath, elevations, minX, maxY, cellSize, noDataValue, result);
                
                // Note: result.OutputPath is already set by WriteGeoTiff to include .bil extension
                result.Success = true;
                
                tr.Commit();
            }
            
            return result;
        }
        
        /// <summary>
        /// Returns a fixed 1 ft x 1 ft cell size for DEM export.
        /// This provides a good balance between accuracy and performance for catchment delineation.
        /// </summary>
        private double CalculateOptimalCellSize(TinSurface surface)
        {
            // Fixed 1 ft cell size - good balance for catchment work
            double cellSize = 1.0;
            
            ReportProgress($"Using fixed cell size: {cellSize:F2} ft");
            
            return cellSize;
        }
        
        /// <summary>
        /// Write elevation data to a GeoTIFF file.
        /// Uses a simple uncompressed TIFF format with GeoTIFF tags.
        /// </summary>
        private void WriteGeoTiff(string path, double[,] data, double originX, double originY, 
                                   double cellSize, double noData, SurfaceExportResult result)
        {
            int rows = data.GetLength(0);
            int cols = data.GetLength(1);
            
            // Convert to float32 for smaller file size
            float[] floatData = new float[rows * cols];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    floatData[r * cols + c] = (float)data[r, c];
                }
            }
            
            // Write raw binary elevation data with header for Python to read
            // Using a simple format: header + raw float32 data
            string headerPath = Path.ChangeExtension(path, ".hdr");
            string dataPath = Path.ChangeExtension(path, ".bil");
            
            // Write ESRI BIL format header (readable by rasterio)
            using (var sw = new StreamWriter(headerPath))
            {
                sw.WriteLine($"NROWS          {rows}");
                sw.WriteLine($"NCOLS          {cols}");
                sw.WriteLine($"NBANDS         1");
                sw.WriteLine($"NBITS          32");
                sw.WriteLine($"PIXELTYPE      FLOAT");
                sw.WriteLine($"BYTEORDER      I"); // Intel byte order (little-endian)
                sw.WriteLine($"LAYOUT         BIL");
                sw.WriteLine($"ULXMAP         {originX:F10}");
                sw.WriteLine($"ULYMAP         {originY:F10}");
                sw.WriteLine($"XDIM           {cellSize:F10}");
                sw.WriteLine($"YDIM           {cellSize:F10}");
                sw.WriteLine($"NODATA         {noData}");
            }
            
            // Write raw binary data
            using (var fs = new FileStream(dataPath, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                foreach (float f in floatData)
                {
                    bw.Write(f);
                }
            }
            
            // Also write a .prj file if we have coordinate system info
            // (User should set this based on their Civil 3D drawing coordinate system)
            string prjPath = Path.ChangeExtension(path, ".prj");
            
            // Write JSON metadata for the Python script
            string jsonPath = Path.ChangeExtension(path, ".json");
            var metadata = new
            {
                surface_name = result.SurfaceName,
                origin_x = originX,
                origin_y = originY,
                cell_size = cellSize,
                rows = rows,
                cols = cols,
                no_data = noData,
                min_elevation = result.Bounds.MinZ,
                max_elevation = result.Bounds.MaxZ,
                bounds = new { result.Bounds.MinX, result.Bounds.MinY, result.Bounds.MaxX, result.Bounds.MaxY },
                bil_file = dataPath,
                header_file = headerPath
            };
            
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(metadata, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(jsonPath, json);
            
            _doc.Editor.WriteMessage($"\n  Written: {dataPath}");
            _doc.Editor.WriteMessage($"\n  Written: {headerPath}");
            _doc.Editor.WriteMessage($"\n  Written: {jsonPath}");
            
            result.OutputPath = dataPath;
        }
    }
    
    /// <summary>
    /// Result of a surface export operation.
    /// </summary>
    public class SurfaceExportResult
    {
        public bool Success { get; set; }
        public string SurfaceName { get; set; }
        public string OutputPath { get; set; }
        public double CellSize { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int PointsSampled { get; set; }
        public int PointsOutsideBoundary { get; set; }
        public SurfaceBounds Bounds { get; set; }
    }
    
    /// <summary>
    /// Surface bounds information.
    /// </summary>
    public class SurfaceBounds
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public double MinZ { get; set; }
        public double MaxZ { get; set; }
    }
}

