using Autodesk.Revit.DB;
using System;
using System.Linq;

namespace RevitMCPApplication
{
    internal static class ManningsFormula
    {
        public const double N_PVC          = 0.010;        // AS3500.3 smooth PVC bore
        public const double MinSlope       = 1.0 / 200.0; // AS3500.3 minimum fall — floor for near-flat pipes
        public const double MaxSlope       = 0.50;         // steeper than 1:2 → near-vertical drop, Manning's not applicable
        public const double DesignFillFactor = 0.75;       // size to 75% full-bore capacity — standard AU hydraulic practice

        // Full-bore capacity of a circular pipe (L/s) — Manning's equation
        public static double CapacityLps(double dnMm, double slope)
        {
            double d = dnMm / 1000.0;
            double a = Math.PI * d * d / 4.0;
            double r = d / 4.0;
            double s = Math.Max(slope, MinSlope);
            return (a / N_PVC) * Math.Pow(r, 2.0 / 3.0) * Math.Sqrt(s) * 1000.0; // m³/s → L/s
        }

        // Standard stormwater DN series (AS3500.3 / hydraulic engineering practice AU).
        // Anything requiring > DN375 is Civil engineering scope.
        public static readonly int[] DnSeries = { 100, 150, 225, 300, 375 };

        // Minimum standard DN (mm) to carry flowLps at the given slope.
        // Returns -1 if flow exceeds DN375 capacity — refer to Civil engineer.
        public static int RequiredDnMm(double flowLps, double slope)
        {
            if (flowLps <= 0) return 100;
            // Near-vertical drops (slope > 1:2) — leave sizing to the engineer.
            // Manning's open-channel formula is not valid here; the next horizontal
            // run downstream will carry a valid recommendation.
            if (slope > MaxSlope) return 0;
            foreach (int dn in DnSeries)
            {
                if (CapacityLps(dn, slope) * DesignFillFactor >= flowLps)
                    return dn;
            }
            return -1;
        }

        // Snap a raw diameter in mm to the nearest standard stormwater DN.
        public static int SnapToDn(double mm) =>
            DnSeries.OrderBy(dn => Math.Abs(dn - mm)).First();

        // Slope (dimensionless, m/m) of a pipe from connector Z values and 3D length,
        // both in Revit internal units (feet). The ratio is unit-invariant.
        // Floored at MinSlope so near-horizontal pipes are evaluated at 1:200.
        public static double PipeSlope(MEPCurve pipe)
        {
            double zMax = double.MinValue, zMin = double.MaxValue;
            foreach (Connector c in pipe.ConnectorManager.Connectors)
            {
                double? z = TryGetZ(c);
                if (z == null) continue;
                if (z.Value > zMax) zMax = z.Value;
                if (z.Value < zMin) zMin = z.Value;
            }
            if (zMax == double.MinValue) return MinSlope;

            double deltaZ = zMax - zMin;
            var lenParam  = pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
            if (lenParam == null || lenParam.AsDouble() < 0.001) return MinSlope;

            return Math.Max(deltaZ / lenParam.AsDouble(), MinSlope);
        }

        // "1:100", "1:80", "1:200" etc.
        public static string SlopeLabel(double slope) =>
            $"1:{(int)Math.Round(1.0 / Math.Max(slope, MinSlope))}";

        private static double? TryGetZ(Connector c)
        {
            try   { return c.Origin.Z; }
            catch { return null; }
        }
    }
}
