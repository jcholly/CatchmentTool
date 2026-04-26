using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using CatchmentTool.UI;

[assembly: CommandClass(typeof(CatchmentTool.Commands.DelineateCommand))]

namespace CatchmentTool.Commands
{
    public class DelineateCommand
    {
        /// <summary>
        /// Opens the catchment-delineation dialog. Pick TIN + pipe network +
        /// pour-point structures (checkboxes), click Run. Algorithm parameters
        /// are baked in from tuning — no settings panel.
        /// Outputs native Civil 3D Catchment objects, one per checked structure.
        /// </summary>
        [CommandMethod("DELINEATE")]
        public void Delineate()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            try
            {
                var dialog = new DelineateDialog(doc);
                Application.ShowModalWindow(dialog);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage($"\nError opening DELINEATE dialog: {ex.Message}\n");
            }
        }
    }
}
