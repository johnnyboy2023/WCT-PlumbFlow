using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace RevitMCPApplication
{
    public partial class StormwaterSizeWindow : Window
    {
        // Per-view static state — each plan view keeps its own marker set so that
        // annotating the Roof plan and Ground Floor plan are independent operations.
        private static readonly Dictionary<ElementId, List<ElementId>> _markersByView = new();
        private static readonly Dictionary<ElementId, List<ElementId>> _brokenByView  = new();

        private readonly Document   _doc;
        private readonly UIDocument _uidoc;

        public StormwaterSizeWindow(Document doc, UIDocument uidoc)
        {
            InitializeComponent();
            _doc   = doc;
            _uidoc = uidoc;

            var viewId = _uidoc.ActiveView.Id;

            // Validate stored IDs for the current view — user may have Ctrl+Z'd since last run.
            if (_markersByView.TryGetValue(viewId, out var ids) && ids.Any()
                && !ids.Any(id => _doc.GetElement(id) != null))
            {
                _markersByView.Remove(viewId);
                _brokenByView.Remove(viewId);
            }

            // If static state has no record for this view (e.g. after a Revit restart),
            // scan the view for markers that were placed in a previous session.
            if (!_markersByView.ContainsKey(viewId))
            {
                var (found, broken) = ScanViewForExistingMarkers(_doc, _uidoc.ActiveView);
                if (found.Any())
                {
                    _markersByView[viewId] = found;
                    _brokenByView[viewId]  = broken;
                }
            }

            ClearMarkersBtn.IsEnabled = _markersByView.ContainsKey(viewId)
                                     && _markersByView[viewId].Any();
        }

        // Rebuilds marker state from the model — used when opening a saved file that already
        // has WCT sizing annotations placed by a previous session.
        private static (List<ElementId> markerIds, List<ElementId> brokenIds) ScanViewForExistingMarkers(
            Document doc, View view)
        {
            var markerIds = new List<ElementId>();
            var brokenIds = new List<ElementId>();

            var wctTypeId = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault(t => t.Name == "WCT-SizingMarker")?.Id;

            foreach (TextNote tn in new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(TextNote)))
            {
                if ((wctTypeId != null && tn.GetTypeId() == wctTypeId) || tn.Text == "✕")
                    markerIds.Add(tn.Id);
            }

            // Restore broken-fitting IDs by looking for the specific orange override.
            if (markerIds.Any())
            {
                foreach (FamilyInstance fi in new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(FamilyInstance)))
                {
                    var c = view.GetElementOverrides(fi.Id).ProjectionLineColor;
                    if (c.IsValid && c.Red == 255 && c.Green == 128 && c.Blue == 0)
                        brokenIds.Add(fi.Id);
                }
            }

            return (markerIds, brokenIds);
        }

        private void SelectPipe_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            try
            {
                var view   = _uidoc.ActiveView;
                var viewId = view.Id;

                // Clear existing markers for this view before re-running.
                if (_markersByView.TryGetValue(viewId, out var existing) && existing.Any())
                {
                    PipeNetworkTraverser.ClearSizingMarkers(_doc, existing, view,
                        _brokenByView.GetValueOrDefault(viewId));
                    _markersByView.Remove(viewId);
                    _brokenByView.Remove(viewId);
                }

                var picked = _uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new PipeSelectionFilter(),
                    "Click any pipe in the stormwater network");

                var startId = picked.ElementId;

                PipeNetworkTraverser.AssignSequentialMarks(_doc);

                var (transitions, _, brokenFittings) = PipeNetworkTraverser.FindSizeTransitions(_doc, startId);
                var placed = PipeNetworkTraverser.PlaceSizingMarkers(_doc, view, transitions, brokenFittings);

                _markersByView[viewId] = placed;
                _brokenByView[viewId]  = brokenFittings;

                if (brokenFittings.Any())
                {
                    MessageBox.Show(
                        $"{brokenFittings.Count} junction{(brokenFittings.Count == 1 ? "" : "s")} detected with no incoming flow.\n\n" +
                        $"The affected fitting{(brokenFittings.Count == 1 ? " has" : "s have")} been marked with a red ✕ in the model.\n\n" +
                        "At each marked fitting, check for:\n" +
                        "  • A disconnected branch stub — redraw the pipe to rebuild connector data\n" +
                        "  • A pipe with no drainage fall — add a slope so flow direction can be determined\n\n" +
                        "Fix and re-run to clear the markers.",
                        "WCT-PlumbSize — Network Integrity Issue",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                this.Close();
            }
            catch (OperationCanceledException)
            {
                this.Show();
            }
        }

        private void ClearMarkers_Click(object sender, RoutedEventArgs e)
        {
            var view   = _uidoc.ActiveView;
            var viewId = view.Id;

            if (_markersByView.TryGetValue(viewId, out var ids))
            {
                PipeNetworkTraverser.ClearSizingMarkers(_doc, ids, view,
                    _brokenByView.GetValueOrDefault(viewId));
                _markersByView.Remove(viewId);
                _brokenByView.Remove(viewId);
            }

            this.Close();
        }

        private class PipeSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)         => elem is MEPCurve;
            public bool AllowReference(Reference r, XYZ p) => false;
        }
    }
}
