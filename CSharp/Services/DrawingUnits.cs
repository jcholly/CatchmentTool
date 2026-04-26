using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace CatchmentTool.Services
{
    /// <summary>
    /// Detects the drawing's linear unit system and provides
    /// unit-aware labels and default values for the UI.
    /// </summary>
    public class DrawingUnits
    {
        /// <summary>True when the drawing uses metric (meters/centimeters).</summary>
        public bool IsMetric { get; private set; }

        /// <summary>Short unit abbreviation for display ("ft" or "m").</summary>
        public string UnitLabel { get; private set; }

        /// <summary>Short area unit label ("sq ft" or "sq m").</summary>
        public string AreaLabel { get; private set; }

        // User-facing UI defaults
        public double DefaultCellSize { get; private set; }
        public double DefaultSnapDistance { get; private set; }
        public double DefaultMinArea { get; private set; }

        public double DefaultBurnDepth { get; private set; }

        // Internal tolerances for the TIN catchment pipeline. Not user-
        // configurable; values are unit-correct equivalents (~3:1 ft:m).
        public double LightFillCap { get; private set; }
        public double InletSkipRadius { get; private set; }
        public double OrphanRescueRadius { get; private set; }
        public double FragmentMergeArea { get; private set; }

        /// <summary>
        /// Detect units from the active document.
        /// </summary>
        public static DrawingUnits Detect(Document doc)
        {
            var units = new DrawingUnits();

            try
            {
                var db = doc.Database;
                // INSUNITS system variable: 1 = Inches, 2 = Feet, 4 = Millimeters, 5 = Centimeters, 6 = Meters
                int insunits = Convert.ToInt32(Application.GetSystemVariable("INSUNITS"));

                units.IsMetric = insunits == 4 || insunits == 5 || insunits == 6;
            }
            catch
            {
                // Default to imperial if detection fails
                units.IsMetric = false;
            }

            if (units.IsMetric)
            {
                units.UnitLabel = "m";
                units.AreaLabel = "sq m";
                units.DefaultCellSize = 0.3;           // ~1 ft
                units.DefaultSnapDistance = 0;           // 0 = no snap (best for designed sites)
                units.DefaultMinArea = 10.0;            // ~100 sq ft

                units.DefaultBurnDepth = 1.0;           // ~3 ft

                units.LightFillCap        = 0.15;       // ~0.5 ft
                units.InletSkipRadius     = 3.0;        // ~10 ft
                units.OrphanRescueRadius  = 45.0;       // ~150 ft
                units.FragmentMergeArea   = 45.0;       // ~500 sq ft
            }
            else
            {
                units.UnitLabel = "ft";
                units.AreaLabel = "sq ft";
                units.DefaultCellSize = 1.0;
                units.DefaultSnapDistance = 0;
                units.DefaultMinArea = 100.0;

                units.DefaultBurnDepth = 3.0;

                units.LightFillCap        = 0.5;
                units.InletSkipRadius     = 10.0;
                units.OrphanRescueRadius  = 150.0;
                units.FragmentMergeArea   = 500.0;
            }

            return units;
        }

        /// <summary>
        /// Returns cell-size combo items appropriate for the detected unit system.
        /// Each item is (display text, numeric value as string).
        /// </summary>
        public (string Display, string Tag, bool IsDefault)[] GetCellSizeOptions()
        {
            if (IsMetric)
            {
                return new[]
                {
                    ("0.15 (Highest detail, slow)", "0.15", false),
                    ("0.3  (High detail)",          "0.3",  true),
                    ("0.6  (Medium)",               "0.6",  false),
                    ("1.5  (Fast)",                 "1.5",  false),
                };
            }
            else
            {
                return new[]
                {
                    ("0.5 (Highest detail, slow)", "0.5", false),
                    ("1.0 (High detail)",          "1.0", true),
                    ("2.0 (Medium)",               "2.0", false),
                    ("5.0 (Fast)",                 "5.0", false),
                };
            }
        }
    }
}
