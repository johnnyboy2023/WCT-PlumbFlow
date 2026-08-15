using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCPApplication
{
    internal record SubMeterEntry(
        ElementId MeterId,
        string    Mark,
        double    FixtureUnits,
        double    NEquiv,
        double    QPsfr);

    internal class WaterSupplyResult
    {
        public List<SubMeterEntry> SubMeters { get; }
        public int    SubMeterCount  => SubMeters.Count;
        public double TotalNEquiv    => SubMeters.Sum(m => m.NEquiv);
        public double QMain          => SubMeters.Count > 0
            ? 0.03 * TotalNEquiv + 0.4554 * Math.Sqrt(TotalNEquiv)
            : 0;

        public WaterSupplyResult(List<SubMeterEntry> subMeters) => SubMeters = subMeters;
    }

    internal static class WaterSupplyTraverser
    {
        // Traverses the water supply network connected to startId.
        // Finds all sub-meters (MAP_SubMeter = Yes), reads Fixture Units from
        // their connected pipes, and computes the building main PSD.
        public static WaterSupplyResult TraverseFromPipe(Document doc, ElementId startId)
        {
                var startElem = doc.GetElement(startId);
            if (startElem == null) return new WaterSupplyResult(new List<SubMeterEntry>());

            // ── Pass 1: walk the whole network, find the building main ─────────
            // The pipe with the highest Fixture Units carries the entire building
            // demand — it is always the root regardless of where the user picked.
            var visited1   = new HashSet<ElementId>();
            var queue1     = new Queue<Element>();
            ElementId rootId = startId;
            double    maxFu  = -1;

            visited1.Add(startId);
            queue1.Enqueue(startElem);

            while (queue1.Count > 0)
            {
                var elem = queue1.Dequeue();

                if (elem is MEPCurve pipe1)
                {
                    double fu = GetPipeFixtureUnits(pipe1);
                    if (fu > maxFu) { maxFu = fu; rootId = pipe1.Id; }
                }

                var cm1 = GetConnectorManager(elem);
                if (cm1 == null) continue;
                foreach (Connector c in cm1.Connectors)
                {
                    if (!c.IsConnected) continue;
                    foreach (Connector refC in c.AllRefs)
                    {
                        var nb = refC.Owner;
                        if (nb == null || visited1.Contains(nb.Id)) continue;
                        visited1.Add(nb.Id);
                        queue1.Enqueue(nb);
                    }
                }
            }

            // ── Pass 2: BFS from building main, tracking pipe-length distance ──
            // Ordering by metres gives the correct hydraulic ranking:
            // shortest run = SubWM-01 (closest), longest = most disadvantaged.
            var visited2  = new HashSet<ElementId>();
            var queue2    = new Queue<(Element elem, double dist)>();
            var subMeters = new List<(FamilyInstance fi, double dist)>();

            var rootElem = doc.GetElement(rootId);
            visited2.Add(rootId);
            queue2.Enqueue((rootElem, 0));

            while (queue2.Count > 0)
            {
                var (elem, dist) = queue2.Dequeue();

                if (elem is FamilyInstance fi && IsSubMeter(fi))
                    subMeters.Add((fi, dist));

                double elemLen = elem is MEPCurve pipe2
                    ? (pipe2.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0)
                    : 0;

                var cm2 = GetConnectorManager(elem);
                if (cm2 == null) continue;
                foreach (Connector c in cm2.Connectors)
                {
                    if (!c.IsConnected) continue;
                    foreach (Connector refC in c.AllRefs)
                    {
                        var nb = refC.Owner;
                        if (nb == null || visited2.Contains(nb.Id)) continue;
                        visited2.Add(nb.Id);
                        queue2.Enqueue((nb, dist + elemLen));
                    }
                }
            }

            // ── Per sub-meter: read FU, sort by distance ──────────────────────
            var entries = subMeters
                .OrderBy(s => s.dist)
                .Select(s =>
                {
                    double fu    = GetSubMeterFixtureUnits(doc, s.fi);
                    double qPsfr = fu > 0 ? 0.09 * Math.Sqrt(fu) : 0;
                    double nEq   = fu > 0 ? qPsfr / 0.48 : 0;
                    string mark  = s.fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)
                                       ?.AsString() ?? "";
                    return new SubMeterEntry(s.fi.Id, mark, fu, nEq, qPsfr);
                })
                .ToList();

            return new WaterSupplyResult(entries);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool IsSubMeter(FamilyInstance fi)
        {
            var p = fi.LookupParameter("MAP_SubMeter")
                 ?? fi.Symbol?.LookupParameter("MAP_SubMeter");
            return p?.AsInteger() == 1;   // Yes/No type: Yes = 1
        }

        private static double GetSubMeterFixtureUnits(Document doc, FamilyInstance meter)
        {
            var cm = meter.MEPModel?.ConnectorManager;
            if (cm == null) return 0;

            double bestFu = 0;
            foreach (Connector c in cm.Connectors)
            {
                if (!c.IsConnected) continue;
                foreach (Connector refC in c.AllRefs)
                {
                    if (refC.Owner is not MEPCurve pipe) continue;
                    double fu = GetPipeFixtureUnits(pipe);
                    if (fu > bestFu) bestFu = fu;
                }
            }
            return bestFu;
        }

        private static double GetPipeFixtureUnits(MEPCurve pipe)
        {
            // Try built-in parameter first; fall back to name lookup
            var p = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_FIXTURE_UNITS_PARAM)
                 ?? pipe.LookupParameter("Fixture Units");
            return p?.AsDouble() ?? 0;
        }

        private static ConnectorManager GetConnectorManager(Element elem) => elem switch
        {
            MEPCurve       curve => curve.ConnectorManager,
            FamilyInstance fi    => fi.MEPModel?.ConnectorManager,
            _                    => null
        };
    }
}
