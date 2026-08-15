#region Namespaces
using Autodesk.Revit.UI;
using System.Reflection;
using System.Windows.Media.Imaging;
using System.Windows.Media;

#endregion

namespace RevitMCPApplication
{
    internal class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication a)
        {
            CreateRibbon(a);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication a) => Result.Succeeded;

        private static ImageSource LoadIcon(string resourceName)
        {
            try
            {
                var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream == null) return null;
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = stream;
                image.CacheOption  = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }

        private static BitmapSource CreateExclamationIcon()
        {
            const int size = 32;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawEllipse(
                    new SolidColorBrush(Color.FromRgb(255, 210, 0)),
                    new Pen(new SolidColorBrush(Color.FromRgb(180, 140, 0)), 1.5),
                    new System.Windows.Point(16, 16), 14.5, 14.5);
                dc.DrawRectangle(Brushes.Black, null,
                    new System.Windows.Rect(13.5, 7, 5, 12));
                dc.DrawEllipse(Brushes.Black, null,
                    new System.Windows.Point(16, 24), 2.8, 2.8);
            }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        private void CreateRibbon(UIControlledApplication a)
        {
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            a.CreateRibbonTab("WCT-PlumbFlow");

            RibbonPanel swPanel = a.CreateRibbonPanel("WCT-PlumbFlow", "Stormwater");
            var swBtn = new PushButtonData(
                "StormwaterTraversal", "Traverse\nNetwork",
                assemblyPath, "RevitMCPApplication.StormwaterTraversalCommand");
            swBtn.ToolTip    = "Accumulate stormwater flow from all rain water outlets through the Revit MEP network";
            swBtn.LargeImage = LoadIcon("RevitMCPApplication.Images.Storm.png");
            swPanel.AddItem(swBtn);

            RibbonPanel wsPanel = a.CreateRibbonPanel("WCT-PlumbFlow", "Water Supply");
            var wsBtn = new PushButtonData(
                "WaterSupplySize", "Traverse\nNetwork",
                assemblyPath, "RevitMCPApplication.WaterSizeCommand");
            wsBtn.ToolTip    = "Traverse the water supply network, find sub-meters, and calculate building main PSD per AS3500.1";
            wsBtn.LargeImage = LoadIcon("RevitMCPApplication.Images.Cold_Water.png");
            wsPanel.AddItem(wsBtn);

            RibbonPanel aboutPanel = a.CreateRibbonPanel("WCT-PlumbFlow", "Info");
            var aboutBtn = new PushButtonData(
                "PlumbFlowAbout", "About",
                assemblyPath, "RevitMCPApplication.AboutCommand");
            aboutBtn.ToolTip    = "About WCT-PlumbFlow — disclaimer and educational purpose statement";
            aboutBtn.LargeImage = CreateExclamationIcon();
            aboutPanel.AddItem(aboutBtn);
        }
    }
}
