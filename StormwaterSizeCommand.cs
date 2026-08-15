using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPApplication
{
    [Transaction(TransactionMode.Manual)]
    public class StormwaterTraversalCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;

            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show("WCT-PlumbFlow",
                    "Please open a Revit project (.rvt) before running Stormwater Network Traversal.");
                return Result.Cancelled;
            }

            new StormwaterSizeWindow(doc, commandData.Application.ActiveUIDocument).ShowDialog();
            return Result.Succeeded;
        }
    }
}
