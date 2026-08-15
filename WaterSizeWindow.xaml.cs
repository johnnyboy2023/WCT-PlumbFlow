using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Windows;

namespace RevitMCPApplication
{
    // Row model for the DataGrid
    internal class SubMeterRow
    {
        public string Mark          { get; set; }
        public string FuDisplay     { get; set; }
        public string QPsfrDisplay  { get; set; }
        public string NEquivDisplay { get; set; }
    }

    public partial class WaterSizeWindow : Window
    {
        private readonly Document    _doc;
        private readonly UIDocument  _uidoc;
        private          WaterSupplyResult _lastResult;

        public   bool              NumberingRequested { get; private set; }
        internal WaterSupplyResult LastResult         => _lastResult;

        public WaterSizeWindow(Document doc, UIDocument uidoc)
        {
            InitializeComponent();
            _doc   = doc;
            _uidoc = uidoc;
        }

        private void SelectPipeButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            try
            {
                var pickedRef = _uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new PipeSelectionFilter(),
                    "Select a water supply pipe");

                if (pickedRef == null) { Show(); return; }

                var startId = pickedRef.ElementId;
                _lastResult        = WaterSupplyTraverser.TraverseFromPipe(_doc, startId);
                NumberingRequested = _lastResult.SubMeterCount > 0;
                ShowResults(_lastResult);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // User pressed Escape — restore window silently
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Traversal failed: {ex.Message}", "WCT-PlumbFlow");
            }
            finally
            {
                Show();
            }
        }

        private void ShowResults(WaterSupplyResult result)
        {
            var rows = new List<SubMeterRow>();
            int rowNum = 1;
            foreach (var m in result.SubMeters)
            {
                rows.Add(new SubMeterRow
                {
                    Mark          = $"SubWM-{rowNum:D2}",
                    FuDisplay     = m.FixtureUnits.ToString("F0"),
                    QPsfrDisplay  = $"{m.QPsfr:F3} L/s",
                    NEquivDisplay = m.NEquiv.ToString("F3")
                });
                rowNum++;
            }

            ResultsGrid.ItemsSource = rows;

            if (result.SubMeterCount == 0)
            {
                MeterCountText.Text = "0";
                NEquivText.Text     = "—";
                QMainText.Text      = "—";
                StatusText.Text     = "No sub-meters (MAP_SubMeter=Yes) found in the connected network.";
                return;
            }

            MeterCountText.Text = result.SubMeterCount.ToString();
            NEquivText.Text     = result.TotalNEquiv.ToString("F2");
            QMainText.Text      = $"{result.QMain:F2} L/s";
            StatusText.Text     = $"AS3500.1: Q = 0.03N + 0.4554√N  |  N = {result.TotalNEquiv:F2} equiv. dwellings";
        }

private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
