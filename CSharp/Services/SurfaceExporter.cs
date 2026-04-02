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
        /// The raw Autodesk CS-MAP code from the drawing (e.g., "TX83-CF").
        /// Set during GetDrawingCoordinateSystem() for inclusion in JSON metadata.
        /// </summary>
        private string _autodeskCsCode;

        /// <summary>
        /// Reads the drawing's coordinate system as a WKT string for the .prj file.
        /// Also stores the raw Autodesk CS-MAP code in _autodeskCsCode for JSON metadata.
        /// Returns null if no coordinate system is assigned.
        /// </summary>
        private string GetDrawingCoordinateSystem()
        {
            try
            {
                // Civil 3D stores the coordinate system in the drawing settings
                var civilDoc = Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument;
                var settings = civilDoc.Settings;

                // DrawingSettings.TransformationSettings.CoordinateSystemCode
                string csCode = null;
                try
                {
                    var drawingSettings = settings.DrawingSettings;
                    var unitZone = drawingSettings.UnitZoneSettings;
                    csCode = unitZone.CoordinateSystemCode;
                }
                catch { }

                // Also try the CGEOCS system variable as a reliable fallback
                if (string.IsNullOrEmpty(csCode))
                {
                    try
                    {
                        csCode = Autodesk.AutoCAD.ApplicationServices.Application
                            .GetSystemVariable("CGEOCS") as string;
                    }
                    catch { }
                }

                // Store the raw Autodesk code for JSON metadata
                _autodeskCsCode = csCode;

                if (string.IsNullOrEmpty(csCode))
                {
                    ReportProgress("No coordinate system assigned to drawing - .prj file skipped");
                    ReportProgress("Tip: Assign a coordinate system in Drawing Settings > Units and Zone");
                    return null;
                }

                ReportProgress($"Drawing coordinate system: {csCode}");

                // Use AutoCAD's coordinate system catalog to get the WKT
                try
                {
                    var csDb = Autodesk.AutoCAD.DatabaseServices.HostApplicationServices.Current
                        .GetType().GetMethod("GetCoordinateSystemWkt",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                    if (csDb != null)
                    {
                        string wkt = csDb.Invoke(
                            Autodesk.AutoCAD.DatabaseServices.HostApplicationServices.Current,
                            new object[] { csCode }) as string;
                        if (!string.IsNullOrEmpty(wkt))
                            return wkt;
                    }
                }
                catch { }

                // Fallback: store just the code so Python can resolve it
                return $"LOCAL_CS[\"{csCode}\"]";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Calculates an optimal cell size based on TIN triangle density and surface extent.
        /// Balances accuracy (small cells) against performance (large rasters).
        /// Target: keep total cells under ~25M while preserving detail.
        /// </summary>
        private double CalculateOptimalCellSize(TinSurface surface)
        {
            var props = surface.GetGeneralProperties();
            var tinProps = surface.GetTinProperties();
            double extentX = props.MaximumCoordinateX - props.MinimumCoordinateX;
            double extentY = props.MaximumCoordinateY - props.MinimumCoordinateY;

            int numTriangles = (int)tinProps.NumberOfTriangles;
            int numPoints = (int)props.NumberOfPoints;

            // Estimate mean triangle edge length from point count and extent
            double surfaceArea = extentX * extentY;
            double meanTriArea = surfaceArea / Math.Max(numTriangles, 1);
            double meanEdgeLength = Math.Sqrt(meanTriArea * 2.0); // approx edge from triangle area

            // Cell size should be roughly half the mean edge length to capture detail
            double cellFromDensity = meanEdgeLength * 0.5;

            // Cap to keep raster manageable (max ~25M cells)
            const long maxCells = 25_000_000;
            double cellFromExtent = Math.Sqrt((extentX * extentY) / maxCells);

            // Use the larger of the two (coarser) to stay within performance limits
            double cellSize = Math.Max(cellFromDensity, cellFromExtent);

            // Clamp to reasonable bounds (0.25 ft to 5 ft for imperial, or equivalent)
            cellSize = Math.Max(0.25, Math.Min(cellSize, 5.0));

            // Round to a clean value
            cellSize = Math.Round(cellSize * 4.0) / 4.0; // snap to 0.25 increments
            if (cellSize < 0.25) cellSize = 0.25;

            ReportProgress($"Adaptive cell size: {cellSize:F2} (surface has {numPoints:N0} points, {numTriangles:N0} triangles)");

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
                // ULXMAP/ULYMAP are cell-center coordinates for the upper-left pixel.
                // Since sampling starts at (originX + 0.5*cellSize), the header must reflect that.
                sw.WriteLine($"ULXMAP         {(originX + cellSize * 0.5):F10}");
                sw.WriteLine($"ULYMAP         {(originY - cellSize * 0.5):F10}");
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
            
            // Write .prj file from the drawing's coordinate system
            string prjPath = Path.ChangeExtension(path, ".prj");
            string coordinateSystem = GetDrawingCoordinateSystem();
            if (!string.IsNullOrEmpty(coordinateSystem))
            {
                File.WriteAllText(prjPath, coordinateSystem);
                _doc.Editor.WriteMessage($"\n  Written: {prjPath}");
            }

            // Write JSON metadata for the Python script
            string jsonPath = Path.ChangeExtension(path, ".json");
            // Detect drawing units for Python-side processing
            var drawingUnits = DrawingUnits.Detect(_doc);

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
                header_file = headerPath,
                coordinate_system = coordinateSystem ?? "",
                prj_file = !string.IsNullOrEmpty(coordinateSystem) ? prjPath : "",
                autodesk_cs_code = _autodeskCsCode ?? "",
                drawing_units = drawingUnits.UnitLabel,
                is_metric = drawingUnits.IsMetric
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

