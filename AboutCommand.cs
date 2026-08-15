using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPApplication
{
    [Transaction(TransactionMode.ReadOnly)]
    public class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            new AboutWindow().ShowDialog();
            return Result.Succeeded;
        }
    }
}
