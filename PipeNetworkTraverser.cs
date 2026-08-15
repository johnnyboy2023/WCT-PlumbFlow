using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCPApplication
{
    internal record RwoEntry(ElementId ElementId, string Mark, string FamilyName, double FlowLps);

    // A flow label in the network. IsRwo=true → individual RWO label; false → junction accumulation label.
    // PipeDir and PipeDiamMm are set on junction markers for aligned text and diameter-based offset.
    internal record PipeSizingMarker(
        XYZ       Location,
        double    FlowLps,
        bool      IsRwo        = false,
        XYZ       PipeDir      = null,
        double    PipeDiamMm   = 100,
        ElementId LevelId      = null,   // host level — used to filter placement per plan view
        int       RequiredDnMm = 0,      // Manning's required DN for the outlet pipe (0 = not computed)
        double    Slope        = 0);     // pipe slope used for sizing (m/m) — shown in label

    internal class StormwaterScanResult
    {
        public List<RwoEntry> Rwos { get; }
        public double TotalLps => Rwos.Sum(r => r.FlowLps);
        public StormwaterScanResult(List<RwoEntry> rwos) => Rwos = rwos;
    }

    internal static class PipeNetworkTraverser
    {
        public static void AssignSequentialMarks(Document doc)
        {
            var candidates = new List<(FamilyInstance fi, double elevation, double x, double y)>();

            var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType();

            foreach (FamilyInstance fi in collector)
            {
                var param = fi.LookupParameter("LitresPerSecond")
                         ?? fi.Symbol?.LookupParameter("LitresPerSecond");
                if (param == null || param.StorageType != StorageType.Double) continue;
                if (param.AsDouble() <= 0) continue;

                double elevation = 0;
                if (fi.LevelId != ElementId.InvalidElementId)
                    elevation = (doc.GetElement(fi.LevelId) as Level)?.Elevation ?? 0;

                var loc = fi.Location as LocationPoint;
                double x = loc?.Point.X ?? 0;
                double y = loc?.Point.Y ?? 0;

                candidates.Add((fi, elevation, x, y));
            }

            var sorted = candidates
                .OrderBy(r => r.elevation)
                .ThenBy(r => r.x)
                .ThenBy(r => r.y)
                .ToList();

            int padWidth = sorted.Count.ToString().Length;

            using var tx = new Transaction(doc, "WCT-PlumbFlow: Assign RWO Marks");
            tx.Start();
            for (int i = 0; i < sorted.Count; i++)
            {
                sorted[i].fi
                    .get_Parameter(BuiltInParameter.ALL_MODEL_MARK)
                    ?.Set($"RWO-{(i + 1).ToString($"D{padWidth}")}");
            }
            tx.Commit();
        }

        // ── Graph traversal ─────────────────────────────────────────────────────

        // Minimum elevation difference (in Revit internal ft) to treat a pipe as sloped,
        // and maximum downhill movement allowed before blocking traversal.
        // ~1.5 mm — below any real drainage fall, well above floating-point noise.
        private const double SlopeTolerance = 0.005;

        public static StormwaterScanResult TraverseFromElement(Document doc, ElementId startId)
        {
            var startElem = doc.GetElement(startId);
            var startCm   = GetConnectorManager(startElem);
            if (startCm == null) return new StormwaterScanResult(new List<RwoEntry>());

            var visited = new HashSet<ElementId>();
            // Queue carries the element and the Z of the connector we arrived through,
            // so we can refuse to walk back downhill.
            var queue = new Queue<(Element elem, double arrivalZ)>();
            var rwos  = new List<RwoEntry>();
            visited.Add(startId);

            // Find the elevation spread of the starting element's connectors.
            // Origin is only valid on physical connectors — TryGetZ returns null for logical ones.
            double maxZ = double.MinValue, minZ = double.MaxValue;
            foreach (Connector c in startCm.Connectors)
            {
                var z = TryGetZ(c);
                if (z == null) continue;
                if (z > maxZ) maxZ = z.Value;
                if (z < minZ) minZ = z.Value;
            }
            bool hasSlope = maxZ != double.MinValue && (maxZ - minZ) >= SlopeTolerance;

            // Seed from the highest connector only (sloped pipe) or all connectors (flat).
            foreach (Connector c in startCm.Connectors)
            {
                var cz = TryGetZ(c);
                if (cz == null) continue;
                if (hasSlope && cz.Value < maxZ - SlopeTolerance) continue;
                if (!c.IsConnected) continue;
                foreach (Connector refC in c.AllRefs)
                {
                    var rz = TryGetZ(refC);
                    if (rz == null) continue;
                    var neighbor = refC.Owner;
                    if (neighbor == null || visited.Contains(neighbor.Id)) continue;
                    visited.Add(neighbor.Id);
                    TryAddRwo(neighbor, rwos);
                    queue.Enqueue((neighbor, rz.Value));
                }
            }

            while (queue.Count > 0)
            {
                var (elem, arrivalZ) = queue.Dequeue();
                var cm = GetConnectorManager(elem);
                if (cm == null) continue;

                bool isPipe = elem is MEPCurve;
                foreach (Connector c in cm.Connectors)
                {
                    var cz = TryGetZ(c);
                    if (cz == null) continue;
                    // Elevation filter on pipes only — prevents traversal back toward the outfall.
                    // Fittings are point elements; the branch connector on a sloped tee sits at
                    // the fitting centre Z which can fall below the arriving connector, so applying
                    // the filter there incorrectly blocks upstream branches (e.g. RWO-02 miss).
                    if (isPipe && cz.Value < arrivalZ - SlopeTolerance) continue;
                    if (!c.IsConnected) continue;
                    foreach (Connector refC in c.AllRefs)
                    {
                        var rz = TryGetZ(refC);
                        if (rz == null) continue;
                        var neighbor = refC.Owner;
                        if (neighbor == null || visited.Contains(neighbor.Id)) continue;
                        visited.Add(neighbor.Id);
                        TryAddRwo(neighbor, rwos);
                        queue.Enqueue((neighbor, rz.Value));
                    }
                }
            }

            rwos = rwos.OrderBy(r => r.Mark).ThenBy(r => r.FamilyName).ToList();
            return new StormwaterScanResult(rwos);
        }

        private static double? TryGetZ(Connector c)
        {
            try   { return c.Origin.Z; }
            catch { return null; }
        }

        private static void TryAddRwo(Element elem, List<RwoEntry> rwos)
        {
            if (elem is not FamilyInstance fi) return;
            var lpsParam = fi.LookupParameter("LitresPerSecond")
                        ?? fi.Symbol?.LookupParameter("LitresPerSecond");
            if (lpsParam?.StorageType != StorageType.Double || lpsParam.AsDouble() <= 0) return;
            double lps    = UnitUtils.ConvertFromInternalUnits(lpsParam.AsDouble(), UnitTypeId.LitersPerSecond);
            string mark   = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
            string family = fi.Symbol?.FamilyName ?? "Unknown";
            rwos.Add(new RwoEntry(fi.Id, mark, family, lps));
        }

        private static ConnectorManager GetConnectorManager(Element elem) => elem switch
        {
            MEPCurve       curve => curve.ConnectorManager,
            FamilyInstance fi    => fi.MEPModel?.ConnectorManager,
            _                    => null
        };

        // ── Flow accumulation markers ────────────────────────────────────────
        // Undirected BFS from startId collects all connected pipes and non-RWO fittings.
        // For each pipe, TraverseFromElement gives the accumulated upstream flow.
        // At each tee (3+ connected pipes) where the outlet carries more flow than the
        // incoming collector, a marker is placed showing the accumulated L/s.
        public static (List<PipeSizingMarker> markers, int rwoCount, List<ElementId> brokenFittings) FindSizeTransitions(Document doc, ElementId startId)
        {
            var markers        = new List<PipeSizingMarker>();
            var brokenFittings = new List<ElementId>();

            // ── collect all connected pipes, non-RWO fittings, and RWOs ──
            var visited     = new HashSet<ElementId>();
            var allPipes    = new List<MEPCurve>();
            var allFittings = new List<FamilyInstance>();
            var allRwos     = new List<(FamilyInstance fi, double flowLps)>();
            var queue       = new Queue<Element>();

            var startElem = doc.GetElement(startId);
            if (startElem == null) return (markers, 0, brokenFittings);
            visited.Add(startId);
            queue.Enqueue(startElem);

            while (queue.Count > 0)
            {
                var elem = queue.Dequeue();
                if (elem is MEPCurve curve)
                {
                    allPipes.Add(curve);
                }
                else if (elem is FamilyInstance fi)
                {
                    var lpsParam = fi.LookupParameter("LitresPerSecond")
                                ?? fi.Symbol?.LookupParameter("LitresPerSecond");
                    bool isRwo = lpsParam?.StorageType == StorageType.Double && lpsParam.AsDouble() > 0;
                    if (isRwo)
                    {
                        double lps = UnitUtils.ConvertFromInternalUnits(lpsParam.AsDouble(), UnitTypeId.LitersPerSecond);
                        allRwos.Add((fi, lps));
                    }
                    else
                    {
                        allFittings.Add(fi);
                    }
                }

                var cm = GetConnectorManager(elem);
                if (cm == null) continue;
                foreach (Connector c in cm.Connectors)
                {
                    if (!c.IsConnected) continue;
                    foreach (Connector refC in c.AllRefs)
                    {
                        var nb = refC.Owner;
                        if (nb == null || visited.Contains(nb.Id)) continue;
                        visited.Add(nb.Id);
                        queue.Enqueue(nb);
                    }
                }
            }

            // ── per-pipe accumulated upstream flow ──
            var pipeFlowMap = new Dictionary<ElementId, double>();
            foreach (var pipe in allPipes)
                pipeFlowMap[pipe.Id] = TraverseFromElement(doc, pipe.Id).TotalLps;

            // ── tag each tee where accumulated flow increases ──
            foreach (var fi in allFittings)
            {
                var cm = fi.MEPModel?.ConnectorManager;
                if (cm == null) continue;

                var connPipes = new Dictionary<ElementId, double>();
                foreach (Connector c in cm.Connectors)
                {
                    if (!c.IsConnected) continue;
                    foreach (Connector refC in c.AllRefs)
                    {
                        var nb = refC.Owner;
                        if (nb != null && pipeFlowMap.TryGetValue(nb.Id, out var flow))
                            connPipes[nb.Id] = flow;
                    }
                }
                // Use total connector count to distinguish tees (3) from elbows/couplings (2).
                // connPipes only counts CONNECTED pipes, so an open-ended tee would show count=2
                // even though it IS a junction — the upstream stub simply has no pipe attached.
                int fittingConnCount = 0;
                foreach (Connector _ in cm.Connectors) fittingConnCount++;
                if (fittingConnCount < 3) continue;  // elbow or coupling, not a junction

                if (connPipes.Count < 2) continue;

                // A connected pipe with zero flow means the branch stub has no RWOs upstream —
                // broken connector reference or missing outlet. Flag and skip marker placement.
                if (connPipes.Values.Any(f => f < 0.001))
                {
                    brokenFittings.Add(fi.Id);
                    continue;
                }

                // Pre-compute pipe direction vectors — needed by both 2-pipe and 3-pipe cases.
                var pipeIds  = connPipes.Keys.ToList();
                var pipeDirs = new Dictionary<ElementId, XYZ>();
                foreach (var pid in pipeIds)
                {
                    if (doc.GetElement(pid) is not MEPCurve pc) continue;
                    var pts = new List<XYZ>();
                    foreach (Connector c in pc.ConnectorManager.Connectors)
                        try { pts.Add(c.Origin); } catch { }
                    if (pts.Count >= 2)
                    {
                        var d = pts[1] - pts[0];
                        if (d.GetLength() > 1e-6) pipeDirs[pid] = d.Normalize();
                    }
                }

                // Two connected pipes on a 3-connector fitting: could be a valid first-junction
                // (upstream collector end is open) or a broken branch (branch pipe disconnected
                // from the tee so the BFS never reached it).
                // Distinguish by collinearity: if the two connected pipes are collinear they form
                // the through-run and the BRANCH is what's missing → broken. If they're not
                // collinear, the upstream collector end is open → valid first-junction.
                if (connPipes.Count == 2)
                {
                    var ids2 = connPipes.Keys.ToList();
                    bool canCheck = pipeDirs.TryGetValue(ids2[0], out var d0)
                                 & pipeDirs.TryGetValue(ids2[1], out var d1);
                    if (canCheck && Math.Abs(d0.DotProduct(d1)) > 0.8)
                    {
                        // Collinear through-run with missing branch — broken connection.
                        brokenFittings.Add(fi.Id);
                        continue;
                    }

                    // Valid first-junction: place the opening accumulation marker.
                    double firstFlow = connPipes.Values.Max();
                    if (firstFlow <= 0) continue;
                    var endOutletId = connPipes.OrderByDescending(kv => kv.Value).First().Key;
                    XYZ endDir      = pipeDirs.TryGetValue(endOutletId, out var epd) ? epd : XYZ.BasisX;
                    var loc2 = fi.Location as LocationPoint;
                    if (loc2 == null) continue;
                    double slope2  = doc.GetElement(endOutletId) is MEPCurve endPipe2
                        ? ManningsFormula.PipeSlope(endPipe2) : ManningsFormula.MinSlope;
                    int reqDn2 = ManningsFormula.RequiredDnMm(firstFlow, slope2);
                    markers.Add(new PipeSizingMarker(loc2.Point, firstFlow,
                        IsRwo: false, PipeDir: endDir, PipeDiamMm: GetPipeDiamMm(doc, endOutletId),
                        LevelId: fi.LevelId, RequiredDnMm: reqDn2, Slope: slope2));
                    continue;
                }

                // Identify outlet pipe. Three strategies in order:
                // (a) Clear max-flow: one pipe distinctly higher than ALL others → that's the outlet.
                // (b) Z-direction: compute departure direction of each pipe away from the fitting.
                //     The pipe with the most negative Z departure is the outlet candidate.
                //     Accept if its Z is at least 0.005 more negative than the next-most-negative
                //     (separates real drainage slope from floating-point noise) AND it carries at
                //     least as much flow as the highest-flow inlet. Handles tied-flow scenarios
                //     (e.g. two inlets with equal flow tied against the outlet), where the outlet
                //     is identified by being the only pipe that departs downward.
                // (c) Collinearity fallback for truly flat horizontal networks.
                var sortedByFlow = connPipes.OrderByDescending(kv => kv.Value).ToList();
                ElementId outletId = null, incomingId = null;
                bool usedZDirection = false;

                if (sortedByFlow[0].Value > sortedByFlow[1].Value + 0.001)
                {
                    // (a) Single clear max-flow winner.
                    outletId   = sortedByFlow[0].Key;
                    incomingId = sortedByFlow[1].Key;
                }
                else
                {
                    // Compute departure direction for each pipe: direction FROM the fitting-end
                    // connector TO the far end. Negative Z = departing downward from the fitting.
                    var departureDirs = new Dictionary<ElementId, XYZ>();
                    foreach (var pid in pipeIds)
                    {
                        if (doc.GetElement(pid) is not MEPCurve dp) continue;
                        XYZ fittingEnd = null, farEnd = null;
                        foreach (Connector c in dp.ConnectorManager.Connectors)
                        {
                            XYZ origin; try { origin = c.Origin; } catch { continue; }
                            bool connectsToFitting = false;
                            if (c.IsConnected)
                                foreach (Connector refC in c.AllRefs)
                                    if (refC.Owner?.Id == fi.Id) { connectsToFitting = true; break; }
                            if (connectsToFitting) fittingEnd = origin;
                            else                   farEnd     = origin;
                        }
                        if (fittingEnd != null && farEnd != null)
                        {
                            var d = farEnd - fittingEnd;
                            if (d.GetLength() > 1e-6) departureDirs[pid] = d.Normalize();
                        }
                    }

                    // (b) Find pipe with most negative Z departure, and the next-most-negative.
                    double firstMinZ = double.MaxValue, secondMinZ = double.MaxValue;
                    ElementId mostDown = null;
                    foreach (var kvp in departureDirs)
                    {
                        if (kvp.Value.Z < firstMinZ)
                        {
                            secondMinZ = firstMinZ;
                            firstMinZ  = kvp.Value.Z;
                            mostDown   = kvp.Key;
                        }
                        else if (kvp.Value.Z < secondMinZ)
                        {
                            secondMinZ = kvp.Value.Z;
                        }
                    }

                    // Accept Z-direction if the gap to the next pipe is meaningful (≥0.005,
                    // equivalent to ~1:100 drainage slope in departure angle) and the candidate
                    // carries at least as much flow as the busiest inlet.
                    double highestInletFlow = pipeIds
                        .Where(id => id != mostDown)
                        .Select(id => connPipes.TryGetValue(id, out var f) ? f : 0.0)
                        .DefaultIfEmpty(0).Max();

                    bool zGapOk   = mostDown != null && firstMinZ < secondMinZ - 0.005;
                    bool zFlowOk  = mostDown != null && connPipes.TryGetValue(mostDown, out var mdf)
                                    && mdf >= highestInletFlow - 0.001;

                    if (zGapOk && zFlowOk)
                    {
                        outletId   = mostDown;
                        // Use the lowest-flow inlet as the comparison so the equality guard
                        // passes naturally whenever possible.
                        incomingId = pipeIds
                            .Where(id => id != mostDown)
                            .OrderBy(id => connPipes.TryGetValue(id, out var f) ? f : 0.0)
                            .FirstOrDefault();
                        usedZDirection = true;
                    }
                    else
                    {
                        // (c) Collinearity fallback for flat horizontal networks.
                        double bestScore = -1;
                        for (int ii = 0; ii < pipeIds.Count; ii++)
                        for (int jj = ii + 1; jj < pipeIds.Count; jj++)
                        {
                            if (!pipeDirs.TryGetValue(pipeIds[ii], out var di)) continue;
                            if (!pipeDirs.TryGetValue(pipeIds[jj], out var dj)) continue;
                            double score = Math.Abs(di.DotProduct(dj));
                            if (score > bestScore)
                            {
                                bestScore = score;
                                var a = pipeIds[ii]; var b = pipeIds[jj];
                                if (connPipes[a] >= connPipes[b])
                                    { outletId = a; incomingId = b; }
                                else
                                    { outletId = b; incomingId = a; }
                            }
                        }
                    }
                }
                if (outletId == null) continue;

                double outletFlow   = connPipes[outletId];
                double incomingFlow = incomingId != null ? connPipes[incomingId] : 0;
                // Skip if no real flow increase — UNLESS Z-direction was used, because undirected
                // traversal makes all pipes report the same total even though the downpipe
                // genuinely carries the combined flow of all inlets.
                if (!usedZDirection && outletFlow <= incomingFlow) continue;

                // Marker position: connector on the outlet pipe that connects to this tee
                XYZ markerPt = null;
                if (doc.GetElement(outletId) is MEPCurve outletPipe)
                {
                    foreach (Connector c in outletPipe.ConnectorManager.Connectors)
                    {
                        if (!c.IsConnected) continue;
                        foreach (Connector refC in c.AllRefs)
                        {
                            if (refC.Owner?.Id == fi.Id)
                            {
                                try { markerPt = c.Origin; } catch { }
                                break;
                            }
                        }
                        if (markerPt != null) break;
                    }
                }
                if (markerPt == null)
                {
                    var loc = fi.Location as LocationPoint;
                    if (loc == null) continue;
                    markerPt = loc.Point;
                }

                // Compute definitive downstream direction: from the tee-end connector
                // toward the far end of the outlet pipe. pipeDirs[outletId] is arbitrary
                // (pts[1]-pts[0]) and can point upstream, which flips the perp to the wrong side.
                XYZ juncDir = pipeDirs.TryGetValue(outletId, out var jd) ? jd : XYZ.BasisX;
                if (doc.GetElement(outletId) is MEPCurve outletDirPipe)
                {
                    foreach (Connector dc in outletDirPipe.ConnectorManager.Connectors)
                    {
                        XYZ dcPt; try { dcPt = dc.Origin; } catch { continue; }
                        if ((dcPt - markerPt).GetLength() > 0.001)
                        {
                            juncDir = (dcPt - markerPt).Normalize();
                            break;
                        }
                    }
                }
                double pipeSlope = doc.GetElement(outletId) is MEPCurve outPipe
                    ? ManningsFormula.PipeSlope(outPipe) : ManningsFormula.MinSlope;
                int reqDn = ManningsFormula.RequiredDnMm(outletFlow, pipeSlope);
                markers.Add(new PipeSizingMarker(markerPt, outletFlow,
                    IsRwo: false, PipeDir: juncDir, PipeDiamMm: GetPipeDiamMm(doc, outletId),
                    LevelId: fi.LevelId, RequiredDnMm: reqDn, Slope: pipeSlope));
            }

            // ── individual RWO flow labels ──
            foreach (var (rwo, flowLps) in allRwos)
            {
                var loc = rwo.Location as LocationPoint;
                if (loc == null) continue;
                markers.Add(new PipeSizingMarker(loc.Point, flowLps, IsRwo: true, LevelId: rwo.LevelId));
            }

            return (markers, allRwos.Count, brokenFittings);
        }

        private static double GetPipeDiamMm(Document doc, ElementId pipeId)
        {
            if (doc.GetElement(pipeId) is not MEPCurve pipe) return 100;
            var p = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            return p == null ? 100 : UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters);
        }

        // Places a TextNote showing accumulated L/s at each flow-increase junction.
        // Returns the ElementIds of the placed notes so they can be cleared later.
        public static List<ElementId> PlaceSizingMarkers(
            Document doc, View view, List<PipeSizingMarker> markers,
            IReadOnlyList<ElementId> brokenFittingIds = null)
        {
            var placed = new List<ElementId>();
            if (!markers.Any()) return placed;

            var defaultTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            var defaultType   = doc.GetElement(defaultTypeId) as TextNoteType;

            XYZ offsetDir      = view.UpDirection;
            double rwoOffsetFt = 0.35 / 0.3048;   // 350 mm

            var blueOverride = new OverrideGraphicSettings();
            blueOverride.SetProjectionLineColor(new Color(0, 84, 166));
            blueOverride.SetCutLineColor(new Color(0, 84, 166));

            var brokenOverride = new OverrideGraphicSettings();
            brokenOverride.SetProjectionLineColor(new Color(255, 128, 0));
            brokenOverride.SetCutLineColor(new Color(255, 128, 0));

            var redOverride = new OverrideGraphicSettings();
            redOverride.SetProjectionLineColor(new Color(220, 0, 0));
            redOverride.SetCutLineColor(new Color(220, 0, 0));

            using var tx = new Transaction(doc, "WCT-PlumbFlow: Place Flow Markers");
            tx.Start();

            var wctType = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault(t => t.Name == "WCT-SizingMarker");

            if (wctType == null)
                wctType = defaultType.Duplicate("WCT-SizingMarker") as TextNoteType;

            // Always apply fixed 1mm text height (idempotent — safe on new or existing type).
            if (wctType != null)
            {
                var sizeParam = wctType.get_Parameter(BuiltInParameter.TEXT_SIZE);
                sizeParam?.Set(0.001 / 0.3048);   // 1 mm in Revit internal units (feet)
            }

            var typeId = wctType?.Id ?? defaultTypeId;

            // When placing in a plan view, restrict markers to the view's associated level.
            // TextNotes are 2D — their Z is ignored, so a ground floor label placed in a roof
            // plan view would appear at the correct X,Y regardless of view range.
            ElementId viewLevelId = (view is ViewPlan vp) ? vp.GenLevel?.Id : null;

            // Z-midpoint zone for this level — used to filter both junction markers and broken fittings.
            // Pipe fittings inherit LevelId from the view they were created in, not their physical
            // elevation, so LevelId is unreliable for fittings. Z midpoint zones correctly attribute
            // any element to the level whose elevation zone contains its Z coordinate.
            double levelMinZ = double.MinValue, levelMaxZ = double.MaxValue;
            if (viewLevelId != null && view is ViewPlan planView && planView.GenLevel != null)
            {
                double thisElev = planView.GenLevel.Elevation;
                var sortedLevels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).ToList();
                int li = sortedLevels.FindIndex(l => l.Id == viewLevelId);
                levelMinZ = (li > 0)
                    ? (sortedLevels[li - 1].Elevation + thisElev) / 2.0
                    : double.MinValue;
                levelMaxZ = (li >= 0 && li + 1 < sortedLevels.Count)
                    ? (thisElev + sortedLevels[li + 1].Elevation) / 2.0
                    : double.MaxValue;
            }

            foreach (var m in markers)
            {
                // RWO labels: filter by LevelId — RWOs have a reliable host level.
                if (m.IsRwo && viewLevelId != null && m.LevelId != null && m.LevelId != viewLevelId)
                    continue;
                // Junction labels: filter by Z zone — fitting LevelId is unreliable.
                if (!m.IsRwo && viewLevelId != null && (m.Location.Z < levelMinZ || m.Location.Z >= levelMaxZ))
                    continue;

                // RWO labels: plain "X.XX L/s".
                // Junction labels: "▲X.XX L/s" with Manning's size recommendation on a second line.
                string label;
                if (m.IsRwo)
                {
                    label = $"{m.FlowLps:F2} L/s";
                }
                else
                {
                    string slopeStr = m.Slope > 0 ? $" {ManningsFormula.SlopeLabel(m.Slope)}" : "";
                    string sizeLine = m.RequiredDnMm > 0   ? $"Chk Ø{m.RequiredDnMm}{slopeStr}"
                                    : m.RequiredDnMm == -1 ? $"Chk Ø>375 — Civil{slopeStr}"
                                    : "";
                    label = sizeLine.Length > 0
                        ? $"▲{m.FlowLps:F2} L/s\n{sizeLine}"
                        : $"▲{m.FlowLps:F2} L/s";
                }
                XYZ    pt;
                var    noteOpts = new TextNoteOptions { TypeId = typeId, HorizontalAlignment = HorizontalTextAlignment.Left };

                if (m.IsRwo)
                {
                    pt = m.Location + offsetDir * rwoOffsetFt;
                }
                else if (m.PipeDir != null)
                {
                    // Label reads perpendicular to the outlet pipe — text follows the branch stub direction.
                    // PipeDir is the outlet (collector) downstream direction; rotating 90° CCW gives branch dir.
                    var branchDir = new XYZ(-m.PipeDir.Y, m.PipeDir.X, 0);
                    if (branchDir.GetLength() > 1e-6)
                    {
                        branchDir = branchDir.Normalize();
                        if (branchDir.DotProduct(view.UpDirection) < 0) branchDir = -branchDir;
                    }
                    else { branchDir = view.UpDirection; }

                    // Offset exactly one pipe radius along the branch direction so the ▲ sits on the pipe outer wall.
                    double clearFt = (m.PipeDiamMm / 2.0) / 1000.0 / 0.3048;
                    pt = m.Location + branchDir * clearFt;

                    // Text rotation = branch direction = 90° to outlet pipe.
                    // Clamp to (-90°, 90°] so text is never upside-down.
                    double rotation = Math.Atan2(branchDir.Y, branchDir.X);
                    while (rotation >  Math.PI / 2.0) rotation -= Math.PI;
                    while (rotation <= -Math.PI / 2.0) rotation += Math.PI;
                    noteOpts.Rotation = rotation;
                }
                else
                {
                    pt = m.Location + offsetDir * (0.5 / 0.3048);
                }

                var note = TextNote.Create(doc, view.Id, pt, label, noteOpts);
                view.SetElementOverrides(note.Id, blueOverride);
                placed.Add(note.Id);
            }
            // For each broken fitting: orange override on the fitting + red ✕ TextNote at its centre.
            if (brokenFittingIds != null)
            {
                var xOpts = new TextNoteOptions
                {
                    TypeId = defaultTypeId,
                    HorizontalAlignment = HorizontalTextAlignment.Center
                };
                foreach (var id in brokenFittingIds)
                {
                    if (doc.GetElement(id) is not FamilyInstance fitting) continue;
                    if (fitting.Location is not LocationPoint lp) continue;
                    // Filter by Z zone — LevelId on fittings can be unreliable.
                    if (viewLevelId != null && (lp.Point.Z < levelMinZ || lp.Point.Z >= levelMaxZ))
                        continue;
                    view.SetElementOverrides(id, brokenOverride);
                    var xNote = TextNote.Create(doc, view.Id, lp.Point, "✕", xOpts);
                    view.SetElementOverrides(xNote.Id, redOverride);
                    placed.Add(xNote.Id);
                }
            }

            tx.Commit();
            return placed;
        }

        public static void ClearSizingMarkers(
            Document doc,
            IEnumerable<ElementId> markerIds,
            View view = null,
            IEnumerable<ElementId> brokenFittingIds = null)
        {
            var ids = markerIds.Where(id => doc.GetElement(id) != null).ToList();
            var broken = brokenFittingIds?.Where(id => doc.GetElement(id) != null).ToList()
                         ?? new List<ElementId>();
            if (!ids.Any() && !broken.Any()) return;
            using var tx = new Transaction(doc, "WCT-PlumbFlow: Clear Sizing Markers");
            tx.Start();
            foreach (var id in ids) doc.Delete(id);
            if (view != null)
                foreach (var id in broken)
                    view.SetElementOverrides(id, new OverrideGraphicSettings());
            tx.Commit();
        }

        // ── Model-wide scan ──────────────────────────────────────────────────

        public static StormwaterScanResult ScanAllRwos(Document doc)
        {
            var rwos = new List<RwoEntry>();

            var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType();

            foreach (FamilyInstance fi in collector)
            {
                // Instance param first; fall back to type param
                var param = fi.LookupParameter("LitresPerSecond")
                         ?? fi.Symbol?.LookupParameter("LitresPerSecond");

                if (param == null || param.StorageType != StorageType.Double) continue;

                double raw = param.AsDouble();
                if (raw <= 0) continue;

                // Flow params are stored internally in ft³/s; convert to L/s
                double lps = UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.LitersPerSecond);

                string mark       = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                string familyName = fi.Symbol?.FamilyName ?? "Unknown";

                rwos.Add(new RwoEntry(fi.Id, mark, familyName, lps));
            }

            rwos = rwos.OrderBy(r => r.Mark).ThenBy(r => r.FamilyName).ToList();
            return new StormwaterScanResult(rwos);
        }
    }
}
