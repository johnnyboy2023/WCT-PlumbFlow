# WCT-PlumbFlow — Revit Add-in

Stormwater and water supply network traversal for Autodesk Revit 2025, aligned to AS3500 (Australian Plumbing Standard). Open-source component of the WCT Plumbing Tools package — [watercad.com.au](https://watercad.com.au)

**WCT-PlumbFlow** accumulates L/s from rain water outlets and LU from fixtures through the Revit MEP connector graph. Labels junctions with flow and Manning's pipe size recommendations (AS3500.3, 75% full bore). Educational — not a design deliverable.

## Build

```
dotnet build WCT-PlumbFlow.csproj -c R2025 --no-incremental
```

Output goes to `bin\R2025\`. The PostBuildEvent auto-deploys to your Revit 2025 addins folder. **Close Revit before building** — the DLL is locked while Revit is running.

## Key Architecture

### Network traversal (PipeNetworkTraverser.cs)
Directional BFS walking the Revit MEP connector graph. Slope is detected by comparing connector Z values (tolerance = 0.05 ft). BFS seeds from the upper connector of a sloped pipe and refuses to walk to connectors lower than the arrival Z — this prevents traversal from going downstream. Flat pipes (no detectable slope) seed from both ends.

### Level filtering for markers
- **RWO labels** — filtered by `LevelId` (reliable for family instances).
- **Junction markers (▲)** — filtered by Z-coordinate midpoint zones. Pipe fitting `LevelId` is unreliable in Revit (inherited from the view in which the fitting was created). Midpoint zones are computed from adjacent level elevations; a fitting belongs to the level whose zone contains its Z.

### Manning's sizing
`ManningsFormula.cs` — `DesignFillFactor = 0.75` (75% of full-bore capacity per AS3500.3). Sizes to the next standard DN from the computed required diameter.

### Water supply traversal (WaterSupplyTraverser.cs)
Two-pass BFS. Pass 1 identifies the building main (highest accumulated LU = root pipe). Pass 2 BFS from root tracks cumulative pipe length to each sub-meter — hydraulic distance ranking. PSFR formula: `Q = 0.09 × √(ΣLU)` (AS3500.1).

## Key Files

| File | Purpose |
|------|---------|
| `PipeNetworkTraverser.cs` | Stormwater traversal engine, junction marker placement, broken fitting detection |
| `WaterSupplyTraverser.cs` | Water supply BFS, sub-meter ranking, PSFR calculation |
| `ManningsFormula.cs` | Pipe sizing from flow (L/s) and grade |
| `StormwaterSizeCommand.cs` | Ribbon command entry point — stormwater |
| `WaterSizeCommand.cs` | Ribbon command entry point — water supply |
| `App.cs` | Ribbon tab registration |
| `AboutCommand.cs` / `AboutWindow.xaml` | About dialog with disclaimer |

## Design Decisions (don't reverse these)

**PlumbFlow does not resize pipes.** It reports recommended sizes — the engineer or drafter makes the change in Revit. Revit's fitting geometry coupling makes programmatic pipe resizing unreliable and error-prone.

**PlumbFlow is educational, not a design deliverable.** Outputs are indicative sizing guidance based on AS3500 formula implementation. They must not be submitted as a formal engineering assessment.

**Sub-meter naming uses SUB-WM**, not WM. WM is reserved for Washing Machine in MAP (Mechanical, Air, Plumbing) practice.

**`PickObject()` cannot be called from inside a WPF `ShowDialog()` context** without invalidating the transaction context. Pattern: dialog sets a flag and closes; `Execute()` calls `PickObject()` and writes results after `ShowDialog()` returns.

## Governing Standards

- AS3500.1 — Water supply (PSFR formula, loading units per fixture type)
- AS3500.3 — Stormwater (Manning's formula, 75% full-bore design)

## For AI Assistants

Point any AI assistant at this file for full project context — build commands, architecture, and design decisions all apply regardless of which tool you use.
