using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Newtonsoft.Json.Linq;

// Aliases to resolve ambiguous references
using AcadEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace CatchmentTool.Services
{
    /// <summary>
    /// Creates Civil 3D Catchment objects from imported polygon data.
    /// </summary>
    public class CatchmentCreator
    {
        private readonly Document _doc;
        private readonly Database _db;
        private readonly Editor _ed;
        
        public CatchmentCreator(Document doc)
        {
            _doc = doc;
            _db = doc.Database;
            _ed = doc.Editor;
        }
        
        /// <summary>
        /// Import catchment polygons and create Civil 3D Catchment objects.
        /// </summary>
        public CatchmentImportResult ImportCatchments(string catchmentFile, ObjectId networkId, ObjectId surfaceId)
        {
            var result = new CatchmentImportResult();
            
            _ed.WriteMessage("\n=== IMPORTING CATCHMENTS ===\n");
            
            // Read catchment polygons
            var catchmentPolygons = ReadCatchmentPolygons(catchmentFile);
            _ed.WriteMessage($"Found {catchmentPolygons.Count} catchment polygons\n");
            
            if (catchmentPolygons.Count == 0)
            {
                result.Errors.Add("No catchment polygons found in file");
                return result;
            }
            
            // Build structure lookups
            var structureLookup = BuildStructureLookup(networkId);
            var structureNameLookup = BuildStructureNameLookup(networkId);
            _ed.WriteMessage($"Found {structureLookup.Count} structures in pipe network\n\n");
            
            // Get or create site and catchment group
            ObjectId siteId = GetOrCreateSite("Catchment Site");
            if (siteId.IsNull)
            {
                _ed.WriteMessage("Note: Could not find/create site - will create polylines only\n");
            }
            
            ObjectId catchmentGroupId = GetOrCreateCatchmentGroup(siteId, "Delineated Catchments");
            if (catchmentGroupId.IsNull)
            {
                _ed.WriteMessage("Note: Could not find/create catchment group - will create polylines only\n\n");
            }
            else
            {
                _ed.WriteMessage("Using catchment group: 'Delineated Catchments'\n\n");
            }
            
            // Get catchment style
            ObjectId catchmentStyleId = GetCatchmentStyle();
            if (catchmentStyleId.IsNull)
            {
                _ed.WriteMessage("Note: Could not find catchment style - will create polylines only\n\n");
            }
            else
            {
                _ed.WriteMessage("Using catchment style found\n\n");
            }
            
            // Create catchments
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                ObjectId layerId = GetOrCreateLayer("C-STORM-CATCHMENTS", 3);
                
                int num = 1;
                foreach (var polygon in catchmentPolygons)
                {
                    try
                    {
                        // Create closed polyline for boundary
                        ObjectId polyId = CreatePolylineFromPoints(polygon.BoundaryPoints, layerId, tr);
                        if (polyId.IsNull)
                        {
                            result.Errors.Add($"Failed to create boundary for {polygon.StructureName}");
                            continue;
                        }
                        
                        // Find the linked structure
                        ObjectId structureId = ObjectId.Null;
                        
                        // Try by StructureId first
                        if (!string.IsNullOrEmpty(polygon.StructureId))
                        {
                            structureLookup.TryGetValue(polygon.StructureId, out structureId);
                        }
                        
                        // Try by StructureName
                        if (structureId.IsNull && !string.IsNullOrEmpty(polygon.StructureName))
                        {
                            structureLookup.TryGetValue(polygon.StructureName, out structureId);
                            if (structureId.IsNull)
                            {
                                structureNameLookup.TryGetValue(polygon.StructureName, out structureId);
                            }
                        }
                        
                        // Debug: log what we're trying to match
                        if (structureId.IsNull && num <= 3) // Only log first 3 failures
                        {
                            _ed.WriteMessage($"    Debug: Could not find structure for ID='{polygon.StructureId}', Name='{polygon.StructureName}'\n");
                        }
                        
                        // Try to create Catchment object
                        string catchmentName = $"CA-{polygon.StructureName}";
                        if (string.IsNullOrEmpty(polygon.StructureName))
                        {
                            catchmentName = $"CA-{num:D3}";
                        }
                        
                        ObjectId catchmentId = CreateCatchment(
                            catchmentGroupId, 
                            catchmentStyleId,
                            polyId, 
                            catchmentName,
                            surfaceId,
                            structureId,
                            polygon,
                            tr);
                        
                        if (!catchmentId.IsNull)
                        {
                            result.CatchmentsCreated++;
                            if (!structureId.IsNull)
                            {
                                result.CatchmentsLinked++;
                                _ed.WriteMessage($"  [{num:D2}] Created '{catchmentName}' -> {polygon.StructureName}\n");
                            }
                            else
                            {
                                _ed.WriteMessage($"  [{num:D2}] Created '{catchmentName}' (no structure link)\n");
                            }
                        }
                        else
                        {
                            // Fallback: keep the polyline with XData
                            if (!structureId.IsNull)
                            {
                                AddStructureLinkXData(polyId, structureId, polygon, tr);
                            }
                            result.CatchmentsCreated++;
                            _ed.WriteMessage($"  [{num:D2}] Created polyline for '{catchmentName}'\n");
                        }
                        
                        num++;
                    }
                    catch (System.Exception ex)
                    {
                        result.Errors.Add($"Error creating {polygon.StructureName}: {ex.Message}");
                    }
                }
                
                tr.Commit();
            }
            
            _ed.WriteMessage($"\n=== IMPORT COMPLETE ===\n");
            _ed.WriteMessage($"Catchments created: {result.CatchmentsCreated}\n");
            _ed.WriteMessage($"Linked to structures: {result.CatchmentsLinked}\n");
            
            if (result.Errors.Count > 0)
            {
                _ed.WriteMessage($"\nWarnings ({result.Errors.Count}):\n");
                foreach (var err in result.Errors.Take(5))
                {
                    _ed.WriteMessage($"  - {err}\n");
                }
                if (result.Errors.Count > 5)
                {
                    _ed.WriteMessage($"  ... and {result.Errors.Count - 5} more\n");
                }
            }
            
            result.Success = result.CatchmentsCreated > 0;
            return result;
        }
        
        /// <summary>
        /// Get or create a site for catchments.
        /// </summary>
        private ObjectId GetOrCreateSite(string siteName)
        {
            var civilDoc = CivilApplication.ActiveDocument;
            
            try
            {
                // Check for existing sites
                var siteIds = civilDoc.GetSiteIds();
                
                using (var tr = _db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in siteIds)
                    {
                        var site = tr.GetObject(id, OpenMode.ForRead) as Site;
                        if (site != null && site.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase))
                        {
                            tr.Commit();
                            return id;
                        }
                    }
                    
                    // Use first existing site if available
                    if (siteIds.Count > 0)
                    {
                        tr.Commit();
                        return siteIds[0];
                    }
                    
                    tr.Commit();
                }
                
                // Create new site
                return Site.Create(civilDoc, siteName);
            }
            catch (System.Exception ex)
            {
                _ed.WriteMessage($"  Note: Site handling: {ex.Message}\n");
                
                // Try to get any existing site
                try
                {
                    var siteIds = civilDoc.GetSiteIds();
                    if (siteIds.Count > 0)
                    {
                        return siteIds[0];
                    }
                }
                catch { }
                
                return ObjectId.Null;
            }
        }
        
        /// <summary>
        /// Get or create a catchment group using runtime discovery.
        /// </summary>
        private ObjectId GetOrCreateCatchmentGroup(ObjectId siteId, string groupName)
        {
            _ed.WriteMessage("\n--- CATCHMENT GROUP DISCOVERY ---\n");
            
            try
            {
                var civilDoc = CivilApplication.ActiveDocument;
                
                // Step 1: Try to find existing catchment groups
                _ed.WriteMessage("Looking for existing catchment groups...\n");
                
                // Method 1: Via Site
                if (!siteId.IsNull)
                {
                    using (var tr = _db.TransactionManager.StartTransaction())
                    {
                        var site = tr.GetObject(siteId, OpenMode.ForRead) as Site;
                        if (site != null)
                        {
                            // Try reflection to get catchment groups from site
                            var siteType = site.GetType();
                            var methods = siteType.GetMethods().Where(m => m.Name.Contains("Catchment")).ToList();
                            _ed.WriteMessage($"  Site methods containing 'Catchment': {methods.Count}\n");
                            foreach (var m in methods.Take(5))
                            {
                                _ed.WriteMessage($"    - {m.Name}\n");
                            }
                            
                            var props = siteType.GetProperties().Where(p => p.Name.Contains("Catchment")).ToList();
                            _ed.WriteMessage($"  Site properties containing 'Catchment': {props.Count}\n");
                            foreach (var p in props)
                            {
                                _ed.WriteMessage($"    - {p.Name} ({p.PropertyType.Name})\n");
                                try
                                {
                                    var val = p.GetValue(site);
                                    if (val is ObjectIdCollection col && col.Count > 0)
                                    {
                                        _ed.WriteMessage($"      Found {col.Count} items!\n");
                                        tr.Commit();
                                        return col[0];
                                    }
                                    else if (val is ObjectId id && !id.IsNull)
                                    {
                                        _ed.WriteMessage($"      Found ObjectId!\n");
                                        tr.Commit();
                                        return id;
                                    }
                                }
                                catch { }
                            }
                        }
                        tr.Commit();
                    }
                }
                
                // Method 2: Via CivilDocument
                var docType = civilDoc.GetType();
                var cgMethods = docType.GetMethods().Where(m => m.Name.Contains("CatchmentGroup")).ToList();
                _ed.WriteMessage($"  CivilDocument methods containing 'CatchmentGroup': {cgMethods.Count}\n");
                foreach (var m in cgMethods)
                {
                    _ed.WriteMessage($"    - {m.Name}\n");
                    try
                    {
                        if (m.GetParameters().Length == 0)
                        {
                            var result = m.Invoke(civilDoc, null);
                            if (result is ObjectIdCollection col && col.Count > 0)
                            {
                                _ed.WriteMessage($"      Found {col.Count} groups!\n");
                                return col[0];
                            }
                        }
                    }
                    catch { }
                }
                
                // Step 2: Try to create a catchment group
                _ed.WriteMessage("\nAttempting to create catchment group...\n");
                
                var createMethods = typeof(CatchmentGroup).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "Create").ToList();
                
                _ed.WriteMessage($"  CatchmentGroup.Create overloads: {createMethods.Count}\n");
                foreach (var method in createMethods)
                {
                    var parms = method.GetParameters();
                    _ed.WriteMessage($"    ({string.Join(", ", parms.Select(p => $"{p.ParameterType.Name} {p.Name}"))})\n");
                }
                
                // Try each Create signature
                foreach (var method in createMethods)
                {
                    var parms = method.GetParameters();
                    object[] args = null;
                    
                    try
                    {
                        if (parms.Length == 3)
                        {
                            // Create(ObjectId siteId, string name, ???)
                            if (parms[0].ParameterType == typeof(ObjectId) && !siteId.IsNull)
                            {
                                if (parms[2].ParameterType == typeof(bool))
                                    args = new object[] { siteId, groupName, true };
                                else
                                    args = new object[] { siteId, groupName, ObjectId.Null };
                            }
                        }
                        else if (parms.Length == 2)
                        {
                            if (parms[0].ParameterType == typeof(ObjectId) && !siteId.IsNull)
                                args = new object[] { siteId, groupName };
                            else if (parms[0].ParameterType == typeof(Database))
                                args = new object[] { _db, groupName };
                            else if (parms[0].ParameterType == typeof(string))
                                args = new object[] { groupName, siteId };
                        }
                        else if (parms.Length == 1)
                        {
                            if (parms[0].ParameterType == typeof(string))
                                args = new object[] { groupName };
                            else if (parms[0].ParameterType == typeof(ObjectId) && !siteId.IsNull)
                                args = new object[] { siteId };
                        }
                        
                        if (args != null)
                        {
                            var result = method.Invoke(null, args);
                            if (result is ObjectId id && !id.IsNull)
                            {
                                _ed.WriteMessage($"  SUCCESS: Created catchment group with {parms.Length} params!\n");
                                return id;
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        _ed.WriteMessage($"    Create({parms.Length}) failed: {ex.InnerException?.Message ?? ex.Message}\n");
                    }
                }
                
                _ed.WriteMessage("\n  Could not create catchment group - will use polylines\n\n");
                return ObjectId.Null;
            }
            catch (System.Exception ex)
            {
                _ed.WriteMessage($"  CatchmentGroup error: {ex.Message}\n");
                return ObjectId.Null;
            }
        }
        
        /// <summary>
        /// Get or create a catchment style.
        /// </summary>
        private ObjectId GetCatchmentStyle()
        {
            try
            {
                var civilDoc = CivilApplication.ActiveDocument;
                var docType = civilDoc.GetType();
                
                // Try to get style collection via Styles property
                var stylesProperty = docType.GetProperty("Styles");
                if (stylesProperty != null)
                {
                    var styles = stylesProperty.GetValue(civilDoc);
                    if (styles != null)
                    {
                        var stylesType = styles.GetType();
                        
                        // Look for CatchmentStyles property
                        var catchmentStylesProp = stylesType.GetProperty("CatchmentStyles");
                        if (catchmentStylesProp != null)
                        {
                            var catchmentStyles = catchmentStylesProp.GetValue(styles);
                            if (catchmentStyles != null)
                            {
                                var csType = catchmentStyles.GetType();
                                
                                var countProp = csType.GetProperty("Count");
                                if (countProp != null)
                                {
                                    int count = (int)countProp.GetValue(catchmentStyles);
                                    _ed.WriteMessage($"  Found {count} catchment styles\n");
                                    
                                    if (count > 0)
                                    {
                                        // Method 1: Try GetEnumerator first (most reliable)
                                        try
                                        {
                                            var getEnum = csType.GetMethod("GetEnumerator");
                                            if (getEnum != null)
                                            {
                                                var enumerator = getEnum.Invoke(catchmentStyles, null) as System.Collections.IEnumerator;
                                                if (enumerator != null && enumerator.MoveNext())
                                                {
                                                    var current = enumerator.Current;
                                                    _ed.WriteMessage($"    Enumerator returned: {current?.GetType().Name}\n");
                                                    
                                                    if (current is ObjectId id && !id.IsNull)
                                                    {
                                                        _ed.WriteMessage($"  Got style via enumerator (ObjectId)\n");
                                                        return id;
                                                    }
                                                    
                                                    // Try to get ObjectId property
                                                    if (current != null)
                                                    {
                                                        var currentType = current.GetType();
                                                        var idProp = currentType.GetProperty("ObjectId")
                                                                  ?? currentType.GetProperty("Id");
                                                        if (idProp != null)
                                                        {
                                                            var id2 = (ObjectId)idProp.GetValue(current);
                                                            if (!id2.IsNull)
                                                            {
                                                                _ed.WriteMessage($"  Got style via enumerator (.ObjectId)\n");
                                                                return id2;
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        catch (System.Exception ex)
                                        {
                                            _ed.WriteMessage($"    Enumerator failed: {ex.Message}\n");
                                        }
                                        
                                        // Method 2: Try get_Item with int parameter specifically
                                        try
                                        {
                                            var indexers = csType.GetMethods()
                                                .Where(m => m.Name == "get_Item")
                                                .ToList();
                                            
                                            _ed.WriteMessage($"    Found {indexers.Count} indexers\n");
                                            
                                            // Find the one that takes an int
                                            var intIndexer = indexers.FirstOrDefault(m => 
                                            {
                                                var p = m.GetParameters();
                                                return p.Length == 1 && p[0].ParameterType == typeof(int);
                                            });
                                            
                                            if (intIndexer != null)
                                            {
                                                var result = intIndexer.Invoke(catchmentStyles, new object[] { 0 });
                                                _ed.WriteMessage($"    Int indexer returned: {result?.GetType().Name}\n");
                                                
                                                if (result is ObjectId id && !id.IsNull)
                                                {
                                                    _ed.WriteMessage($"  Got style via int indexer\n");
                                                    return id;
                                                }
                                                
                                                if (result != null)
                                                {
                                                    var idProp = result.GetType().GetProperty("ObjectId")
                                                              ?? result.GetType().GetProperty("Id");
                                                    if (idProp != null)
                                                    {
                                                        var id2 = (ObjectId)idProp.GetValue(result);
                                                        if (!id2.IsNull)
                                                        {
                                                            _ed.WriteMessage($"  Got style via int indexer (.ObjectId)\n");
                                                            return id2;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        catch (System.Exception ex)
                                        {
                                            _ed.WriteMessage($"    Int indexer failed: {ex.Message}\n");
                                        }
                                        
                                        // Method 3: Try ElementAt extension or similar
                                        try
                                        {
                                            // Cast to IEnumerable and use LINQ
                                            if (catchmentStyles is System.Collections.IEnumerable enumerable)
                                            {
                                                foreach (var item in enumerable)
                                                {
                                                    _ed.WriteMessage($"    IEnumerable item: {item?.GetType().Name}\n");
                                                    
                                                    if (item is ObjectId id && !id.IsNull)
                                                    {
                                                        _ed.WriteMessage($"  Got style via IEnumerable\n");
                                                        return id;
                                                    }
                                                    
                                                    if (item != null)
                                                    {
                                                        var idProp = item.GetType().GetProperty("ObjectId")
                                                                  ?? item.GetType().GetProperty("Id");
                                                        if (idProp != null)
                                                        {
                                                            var id2 = (ObjectId)idProp.GetValue(item);
                                                            if (!id2.IsNull)
                                                            {
                                                                _ed.WriteMessage($"  Got style via IEnumerable (.ObjectId)\n");
                                                                return id2;
                                                            }
                                                        }
                                                    }
                                                    
                                                    break; // Just need first one
                                                }
                                            }
                                        }
                                        catch (System.Exception ex)
                                        {
                                            _ed.WriteMessage($"    IEnumerable failed: {ex.Message}\n");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                
                _ed.WriteMessage("  Could not find catchment style via Styles property\n");
            }
            catch (System.Exception ex)
            {
                _ed.WriteMessage($"  GetCatchmentStyle error: {ex.Message}\n");
            }
            
            return ObjectId.Null;
        }
        
        /// <summary>
        /// Create a Catchment object from a polyline boundary.
        /// Uses reflection to discover and try all available Create method signatures.
        /// </summary>
        private ObjectId CreateCatchment(ObjectId catchmentGroupId, ObjectId catchmentStyleId, 
                                         ObjectId polylineId, string name,
                                         ObjectId surfaceId, ObjectId structureId, 
                                         CatchmentPolygonInfo polygon, Transaction tr)
        {
            ObjectId catchmentId = ObjectId.Null;
            
            // Check prerequisites
            if (catchmentGroupId.IsNull || catchmentStyleId.IsNull)
            {
                if (!_warnedNoGroup)
                {
                    if (catchmentGroupId.IsNull)
                        _ed.WriteMessage("  WARNING: No catchment group - cannot create Catchment objects\n");
                    if (catchmentStyleId.IsNull)
                        _ed.WriteMessage("  WARNING: No catchment style - cannot create Catchment objects\n");
                    _warnedNoGroup = true;
                }
                return ObjectId.Null;
            }
            
            try
            {
                // Print method signatures once for debugging
                if (!_printedCatchmentMethods)
                {
                    _ed.WriteMessage("\n--- CATCHMENT API DISCOVERY ---\n");
                    
                    var createMethods = typeof(Catchment).GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m => m.Name == "Create").ToList();
                    
                    _ed.WriteMessage($"Catchment.Create overloads: {createMethods.Count}\n");
                    foreach (var method in createMethods)
                    {
                        var parms = method.GetParameters();
                        _ed.WriteMessage($"  ({string.Join(", ", parms.Select(p => $"{p.ParameterType.Name} {p.Name}"))})\n");
                    }
                    _ed.WriteMessage("\n");
                    _printedCatchmentMethods = true;
                }
                
                // Create Point3dCollection from polygon boundary points
                var boundaryPoints = new Point3dCollection();
                foreach (var pt in polygon.BoundaryPoints)
                {
                    boundaryPoints.Add(new Point3d(pt.X, pt.Y, 0));
                }
                
                // Get all Create methods
                var methods = typeof(Catchment).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "Create").ToList();
                
                // Try each method signature
                foreach (var method in methods)
                {
                    var parms = method.GetParameters();
                    
                    try
                    {
                        object[] args = null;
                        
                        // Match parameter patterns - now we know the signature:
                        // (String name, ObjectId styleId, ObjectId catchmentGroupId, ObjectId surfaceId, Point3dCollection boundary)
                        if (parms.Length == 5)
                        {
                            if (parms[0].ParameterType == typeof(string) &&
                                parms[4].ParameterType == typeof(Point3dCollection))
                            {
                                // Correct order: name, styleId, groupId, surfaceId, boundary
                                args = new object[] { name, catchmentStyleId, catchmentGroupId, surfaceId, boundaryPoints };
                            }
                        }
                        else if (parms.Length == 4)
                        {
                            // (String, ObjectId, ObjectId, Point3dCollection)
                            if (parms[0].ParameterType == typeof(string) &&
                                parms[3].ParameterType == typeof(Point3dCollection))
                            {
                                args = new object[] { name, catchmentStyleId, catchmentGroupId, boundaryPoints };
                            }
                        }
                        
                        if (args != null)
                        {
                            var result = method.Invoke(null, args);
                            if (result is ObjectId id && !id.IsNull)
                            {
                                catchmentId = id;
                                _ed.WriteMessage($"    SUCCESS with {parms.Length}-param signature!\n");
                                break;
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        // Log first failure only
                        if (!_loggedFirstFailure)
                        {
                            _ed.WriteMessage($"    Create failed ({parms.Length} params): {ex.InnerException?.Message ?? ex.Message}\n");
                            _loggedFirstFailure = true;
                        }
                    }
                }
                
                // If catchment was created, try to set properties and rename
                if (!catchmentId.IsNull)
                {
                    var catchment = tr.GetObject(catchmentId, OpenMode.ForWrite) as Catchment;
                    if (catchment != null)
                    {
                        try { catchment.Name = name; } catch { }
                        SetCatchmentProperties(catchment, surfaceId, structureId, tr);
                    }
                    
                    // Delete the temporary polyline since we have a real catchment
                    try
                    {
                        var poly = tr.GetObject(polylineId, OpenMode.ForWrite) as AcadEntity;
                        if (poly != null) poly.Erase();
                    }
                    catch { }
                }
            }
            catch (System.Exception ex)
            {
                _ed.WriteMessage($"    CreateCatchment exception: {ex.Message}\n");
            }
            
            return catchmentId;
        }
        
        private bool _printedCatchmentMethods = false;
        private bool _warnedNoGroup = false;
        private bool _loggedFirstFailure = false;
        
        /// <summary>
        /// Try to set catchment properties using reflection.
        /// </summary>
        private void SetCatchmentProperties(Catchment catchment, ObjectId surfaceId, ObjectId structureId, Transaction tr)
        {
            var catchmentType = catchment.GetType();
            
            // Log ALL available properties for debugging (once)
            if (!_loggedCatchmentProps)
            {
                _ed.WriteMessage("\n--- CATCHMENT PROPERTIES (ALL) ---\n");
                var allProps = catchmentType.GetProperties().OrderBy(p => p.Name).ToList();
                foreach (var p in allProps)
                {
                    _ed.WriteMessage($"  {p.Name} ({p.PropertyType.Name}) CanWrite={p.CanWrite}\n");
                }
                
                _ed.WriteMessage("\n--- CATCHMENT METHODS ---\n");
                var methods = catchmentType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName && m.DeclaringType == catchmentType)
                    .OrderBy(m => m.Name)
                    .ToList();
                foreach (var m in methods)
                {
                    var parms = m.GetParameters();
                    _ed.WriteMessage($"  {m.Name}({string.Join(", ", parms.Select(p => p.ParameterType.Name))})\n");
                }
                
                _loggedCatchmentProps = true;
            }
            
            // Try to set reference surface
            if (!surfaceId.IsNull)
            {
                try
                {
                    var prop = catchmentType.GetProperty("ReferenceSurfaceId") 
                            ?? catchmentType.GetProperty("SurfaceId");
                    if (prop != null && prop.CanWrite)
                    {
                        prop.SetValue(catchment, surfaceId);
                        _ed.WriteMessage($"    Set ReferenceSurfaceId\n");
                    }
                }
                catch (System.Exception ex)
                {
                    _ed.WriteMessage($"    Failed to set surface: {ex.Message}\n");
                }
            }
            
            // Try to set reference object (the outlet structure)
            if (!structureId.IsNull)
            {
                bool setRefObject = false;
                
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
                            _ed.WriteMessage($"    Set ReferencePipeNetworkId\n");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        _ed.WriteMessage($"    Failed to set network: {ex.Message}\n");
                    }
                }
                
                // Step 2: Set the structure reference
                try
                {
                    var structProp = catchmentType.GetProperty("ReferencePipeNetworkStructureId");
                    if (structProp != null && structProp.CanWrite)
                    {
                        structProp.SetValue(catchment, structureId);
                        _ed.WriteMessage($"    Set ReferencePipeNetworkStructureId\n");
                        setRefObject = true;
                    }
                }
                catch (System.Exception ex)
                {
                    _ed.WriteMessage($"    Failed to set structure (method 1): {ex.Message}\n");
                }
                
                // Step 3: Also try ReferenceDischargeObjectId
                if (!setRefObject)
                {
                    try
                    {
                        var dischargeProp = catchmentType.GetProperty("ReferenceDischargeObjectId");
                        if (dischargeProp != null && dischargeProp.CanWrite)
                        {
                            dischargeProp.SetValue(catchment, structureId);
                            _ed.WriteMessage($"    Set ReferenceDischargeObjectId\n");
                            setRefObject = true;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        _ed.WriteMessage($"    Failed to set discharge object: {ex.Message}\n");
                    }
                }
                
                // Set discharge point at structure location (structure already retrieved above)
                if (structure != null)
                {
                    // Try to set discharge point at structure location
                    string[] dischargePointProps = new[] {
                        "DischargePoint",
                        "DischargePointLocation",
                        "OutletPoint",
                        "DischargeLocation"
                    };
                    
                    foreach (var propName in dischargePointProps)
                    {
                        try
                        {
                            var prop = catchmentType.GetProperty(propName);
                            if (prop != null && prop.CanWrite)
                            {
                                if (prop.PropertyType == typeof(Point2d))
                                {
                                    var point = new Point2d(structure.Position.X, structure.Position.Y);
                                    prop.SetValue(catchment, point);
                                    _ed.WriteMessage($"    Set {propName} to structure location\n");
                                    break;
                                }
                                else if (prop.PropertyType == typeof(Point3d))
                                {
                                    prop.SetValue(catchment, structure.Position);
                                    _ed.WriteMessage($"    Set {propName} (3D) to structure location\n");
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                    
                    // Try method-based discharge point setting
                    try
                    {
                        var setMethod = catchmentType.GetMethod("SetDischargePoint");
                        if (setMethod != null)
                        {
                            var parms = setMethod.GetParameters();
                            if (parms.Length == 1)
                            {
                                if (parms[0].ParameterType == typeof(Point2d))
                                {
                                    var point = new Point2d(structure.Position.X, structure.Position.Y);
                                    setMethod.Invoke(catchment, new object[] { point });
                                    _ed.WriteMessage($"    Called SetDischargePoint(Point2d)\n");
                                }
                                else if (parms[0].ParameterType == typeof(Point3d))
                                {
                                    setMethod.Invoke(catchment, new object[] { structure.Position });
                                    _ed.WriteMessage($"    Called SetDischargePoint(Point3d)\n");
                                }
                            }
                        }
                    }
                    catch { }
                }
                
                if (!setRefObject)
                {
                    _ed.WriteMessage($"    WARNING: Could not set reference object property\n");
                }
            }
        }
        
        private bool _loggedCatchmentProps = false;
        
        #region Helper Methods
        
        private Dictionary<string, ObjectId> BuildStructureLookup(ObjectId networkId)
        {
            var lookup = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var network = tr.GetObject(networkId, OpenMode.ForRead) as Network;
                if (network == null) { tr.Commit(); return lookup; }
                
                foreach (ObjectId structId in network.GetStructureIds())
                {
                    var structure = tr.GetObject(structId, OpenMode.ForRead) as Structure;
                    if (structure != null)
                    {
                        // Add by Name (primary key we export)
                        if (!string.IsNullOrEmpty(structure.Name))
                        {
                            lookup[structure.Name] = structId;
                        }
                        // Also add by Handle for backwards compatibility
                        lookup[structure.Handle.ToString()] = structId;
                        // Also add by ObjectId string
                        lookup[structId.ToString()] = structId;
                    }
                }
                tr.Commit();
            }
            
            _ed.WriteMessage($"  Structure lookup keys: {lookup.Count}\n");
            
            return lookup;
        }
        
        private Dictionary<string, ObjectId> BuildStructureNameLookup(ObjectId networkId)
        {
            var lookup = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var network = tr.GetObject(networkId, OpenMode.ForRead) as Network;
                if (network == null) { tr.Commit(); return lookup; }
                
                foreach (ObjectId structId in network.GetStructureIds())
                {
                    var structure = tr.GetObject(structId, OpenMode.ForRead) as Structure;
                    if (structure != null && !string.IsNullOrEmpty(structure.Name))
                    {
                        lookup[structure.Name] = structId;
                    }
                }
                tr.Commit();
            }
            return lookup;
        }
        
        private List<CatchmentPolygonInfo> ReadCatchmentPolygons(string filePath)
        {
            var catchments = new List<CatchmentPolygonInfo>();
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            
            if (ext == ".json" || ext == ".geojson")
            {
                catchments = ReadGeoJson(filePath);
            }
            else if (ext == ".shp")
            {
                string jsonPath = Path.ChangeExtension(filePath, ".json");
                if (File.Exists(jsonPath))
                {
                    catchments = ReadGeoJson(jsonPath);
                }
            }
            
            return catchments;
        }
        
        private List<CatchmentPolygonInfo> ReadGeoJson(string filePath)
        {
            var catchments = new List<CatchmentPolygonInfo>();
            string json = File.ReadAllText(filePath);
            var geoJson = JObject.Parse(json);
            var features = geoJson["features"] as JArray;
            
            if (features == null) return catchments;
            
            foreach (var feature in features)
            {
                var geometry = feature["geometry"];
                var properties = feature["properties"];
                if (geometry == null || properties == null) continue;
                
                string geomType = geometry["type"]?.ToString();
                if (geomType != "Polygon" && geomType != "MultiPolygon") continue;
                
                // Try multiple property name variations (shapefile truncates to 10 chars)
                var catchment = new CatchmentPolygonInfo
                {
                    StructureId = (properties["structure_id"]?.ToString() ?? 
                                   properties["structure_"]?.ToString() ?? 
                                   properties["struct_id"]?.ToString() ?? ""),
                    StructureName = (properties["struct_name"]?.ToString() ?? 
                                     properties["struct_nam"]?.ToString() ?? 
                                     properties["name"]?.ToString() ?? ""),
                    Area = properties["area"]?.Value<double>() ?? 0,
                    StructureX = properties["struct_x"]?.Value<double>() ?? 0,
                    StructureY = properties["struct_y"]?.Value<double>() ?? 0
                };
                
                if (geomType == "Polygon")
                {
                    var coordinates = geometry["coordinates"] as JArray;
                    if (coordinates != null && coordinates.Count > 0)
                    {
                        catchment.BoundaryPoints = ParseCoordinateRing(coordinates[0] as JArray);
                    }
                }
                else if (geomType == "MultiPolygon")
                {
                    var coordinates = geometry["coordinates"] as JArray;
                    JArray largestRing = null;
                    double largestArea = 0;
                    foreach (JArray polygon in coordinates)
                    {
                        if (polygon.Count > 0)
                        {
                            var ring = polygon[0] as JArray;
                            var pts = ParseCoordinateRing(ring);
                            double area = Math.Abs(CalculatePolygonArea(pts));
                            if (largestRing == null || area > largestArea)
                            {
                                largestRing = ring;
                                largestArea = area;
                            }
                        }
                    }
                    if (largestRing != null)
                        catchment.BoundaryPoints = ParseCoordinateRing(largestRing);
                }
                
                if (catchment.BoundaryPoints.Count >= 3)
                    catchments.Add(catchment);
            }
            
            return catchments;
        }
        
        /// <summary>
        /// Calculate the signed area of a polygon using the Shoelace formula.
        /// </summary>
        private double CalculatePolygonArea(List<Point2d> pts)
        {
            double area = 0;
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                area += pts[i].X * pts[j].Y;
                area -= pts[j].X * pts[i].Y;
            }
            return area / 2.0;
        }

        private List<Point2d> ParseCoordinateRing(JArray ring)
        {
            var points = new List<Point2d>();
            if (ring == null) return points;
            
            foreach (var coord in ring)
            {
                var arr = coord as JArray;
                if (arr != null && arr.Count >= 2)
                {
                    points.Add(new Point2d(arr[0].Value<double>(), arr[1].Value<double>()));
                }
            }
            return points;
        }
        
        private ObjectId CreatePolylineFromPoints(List<Point2d> points, ObjectId layerId, Transaction tr)
        {
            if (points.Count < 3) return ObjectId.Null;
            
            var pline = new Polyline();
            for (int i = 0; i < points.Count; i++)
            {
                pline.AddVertexAt(i, points[i], 0, 0, 0);
            }
            pline.Closed = true;
            pline.LayerId = layerId;
            
            var bt = tr.GetObject(_db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;
            
            ObjectId polyId = ms.AppendEntity(pline);
            tr.AddNewlyCreatedDBObject(pline, true);
            
            return polyId;
        }
        
        private void AddStructureLinkXData(ObjectId polyId, ObjectId structureId,
                                           CatchmentPolygonInfo catchment, Transaction tr)
        {
            const string appName = "CATCHMENT_TOOL";
            
            var regTable = tr.GetObject(_db.RegAppTableId, OpenMode.ForWrite) as RegAppTable;
            if (!regTable.Has(appName))
            {
                var regApp = new RegAppTableRecord { Name = appName };
                regTable.Add(regApp);
                tr.AddNewlyCreatedDBObject(regApp, true);
            }
            
            var xdata = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, appName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, $"StructureId:{catchment.StructureId}"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, $"StructureName:{catchment.StructureName}"),
                new TypedValue((int)DxfCode.ExtendedDataReal, catchment.Area),
                new TypedValue((int)DxfCode.ExtendedDataHandle, structureId.Handle)
            );
            
            var poly = tr.GetObject(polyId, OpenMode.ForWrite) as AcadEntity;
            poly.XData = xdata;
        }
        
        private ObjectId GetOrCreateLayer(string layerName, short colorIndex)
        {
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var lt = tr.GetObject(_db.LayerTableId, OpenMode.ForWrite) as LayerTable;
                
                if (lt.Has(layerName))
                {
                    tr.Commit();
                    return lt[layerName];
                }
                
                var layer = new LayerTableRecord
                {
                    Name = layerName,
                    Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex)
                };
                
                ObjectId layerId = lt.Add(layer);
                tr.AddNewlyCreatedDBObject(layer, true);
                tr.Commit();
                
                return layerId;
            }
        }
        
        #endregion

        /// <summary>
        /// Create Civil 3D Catchment objects from pre-built polygon data.
        /// Used by MAKECATCHMENTS — structure is already resolved via point-in-polygon.
        /// </summary>
        public CatchmentImportResult CreateCatchmentsFromPolylines(
            List<CatchmentPolygonInfo> polygons, ObjectId surfaceId)
        {
            var result = new CatchmentImportResult();

            ObjectId siteId = GetOrCreateSite("Catchment Site");
            ObjectId groupId = GetOrCreateCatchmentGroup(siteId, "Delineated Catchments");
            ObjectId styleId = GetCatchmentStyle();
            ObjectId layerId;

            using (var tr = _db.TransactionManager.StartTransaction())
            {
                layerId = GetOrCreateLayer("C-STORM-CATCHMENTS", 3);
                tr.Commit();
            }

            using (var tr = _db.TransactionManager.StartTransaction())
            {
                int num = 1;
                foreach (var polygon in polygons)
                {
                    try
                    {
                        string name = polygon.StructureName.Length > 0
                            ? $"Catchment - {polygon.StructureName}"
                            : $"Catchment {num}";

                        ObjectId structId = !polygon.StructureObjectId.IsNull
                            ? polygon.StructureObjectId
                            : ObjectId.Null;

                        ObjectId polyId = CreatePolylineFromPoints(polygon.BoundaryPoints, layerId, tr);
                        if (polyId.IsNull) { result.Errors.Add($"Could not create boundary for {name}"); num++; continue; }

                        ObjectId catchId = CreateCatchment(groupId, styleId, polyId, name, surfaceId, structId, polygon, tr);

                        if (!catchId.IsNull)
                        {
                            result.CatchmentsCreated++;
                            _ed.WriteMessage($"  [{num:D2}] Created '{name}'\n");
                        }
                        else
                        {
                            result.CatchmentsCreated++;
                            _ed.WriteMessage($"  [{num:D2}] Created polyline boundary for '{name}'\n");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Error on polygon {num}: {ex.Message}");
                    }
                    num++;
                }
                tr.Commit();
            }

            result.Success = result.CatchmentsCreated > 0;
            return result;
        }
    }

    public class CatchmentPolygonInfo
    {
        public string StructureId { get; set; }
        public string StructureName { get; set; }
        public double Area { get; set; }
        public double StructureX { get; set; }
        public double StructureY { get; set; }
        public List<Point2d> BoundaryPoints { get; set; } = new List<Point2d>();
        /// <summary>Directly resolved structure ObjectId (used by MAKECATCHMENTS; bypasses name lookup).</summary>
        public ObjectId StructureObjectId { get; set; } = ObjectId.Null;
    }
    
    public class CatchmentImportResult
    {
        public bool Success { get; set; }
        public int CatchmentsCreated { get; set; }
        public int CatchmentsLinked { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }
}
