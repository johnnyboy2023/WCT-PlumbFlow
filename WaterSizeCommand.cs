using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace RevitMCPApplication
{
    [Transaction(TransactionMode.Manual)]
    public class WaterSizeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc   = uidoc.Document;

            var window = new WaterSizeWindow(doc, uidoc);
            window.ShowDialog();

            // Numbering writes must happen here — inside Execute(), not inside the dialog,
            // because PickObject() invalidates the transaction context within the dialog.
            if (window.NumberingRequested && window.LastResult?.SubMeterCount > 0)
            {
                using var tx = new Transaction(doc, "Number Sub-Meters");
                tx.Start();
                int i = 1;
                foreach (var entry in window.LastResult.SubMeters)
                {
                    doc.GetElement(entry.MeterId)
                       ?.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)
                       ?.Set($"SubWM-{i:D2}");
                    i++;
                }
                tx.Commit();
            }

            return Result.Succeeded;
        }
    }

    // Restricts pipe selection to MEPCurve (pipes) only.
    internal class PipeSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)   => elem is MEPCurve;
        public bool AllowReference(Reference r, XYZ pt) => false;
    }
}
