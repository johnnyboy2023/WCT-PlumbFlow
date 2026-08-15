# WCT PlumbFlow — Revit Add-in

Revit 2025/2026 add-in implementing two plumbing tools for Australian hydraulic engineers:

- **WCT-PlumbCheck** — connector quality control. Validates plumbing family connector configuration against AS3500 requirements (system type, flow direction, loading units, diameter, category). Reference showcase tool — operates on families in the loaded WCT Sample Model.
- **WCT-PlumbFlow** — stormwater and water supply network traversal. Accumulates L/s from rain water outlets and LU from fixtures through the Revit MEP network. Labels junctions with flow and Manning's pipe size recommendations (AS3500.3, 75% full bore). Educational — not a design deliverable.

The source code for **PlumbFlow is open source**. PlumbCheck source is not included in this package.

## Build

```
dotnet build RevitMCPApplication.csproj -c R2025 --no-incremental
```

Output goes to `bin\R2025\net8.0-windows\`. The PostBuildEvent in the csproj xcopy-deploys to `C:\ProgramData\Autodesk\Revit\Addins\2025\` automatically on build. **Revit must be closed before building** — the DLL is locked while Revit is running.

## Key Architecture

### Threading constraint
Revit API calls must happen on Revit's main thread. HTTP requests from the MCP server arrive on a background thread. The pattern used throughout is `ExternalEvent` + `IExternalEventHandler` — the handler is raised from the background thread and Revit executes it on the main thread at the next idle moment.

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
| `StormwaterSizeCommand.cs` | Ribbon command entry point for PlumbFlow stormwater |
| `WaterSizeCommand.cs` | Ribbon command entry point for PlumbFlow water supply |
| `App.cs` | Ribbon tab registration for WCT-PlumbCheck and WCT-PlumbFlow |
| `AboutCommand.cs` / `AboutWindow.xaml` | About dialog with disclaimer |

## Design Decisions (don't reverse these)

**PlumbFlow does not resize pipes.** It reports recommended sizes — the engineer or drafter makes the change in Revit. Revit's fitting geometry coupling makes programmatic pipe resizing unreliable and error-prone.

**PlumbFlow is educational, not a design deliverable.** Outputs are indicative sizing guidance based on AS3500 formula implementation. They must not be submitted as a formal engineering assessment.

**Sub-meter naming uses SUB-WM**, not WM. WM is reserved for Washing Machine in MAP (Mechanical, Air, Plumbing) practice.

**`PickObject()` cannot be called from inside a WPF `ShowDialog()` context** without invalidating the transaction context. Pattern: dialog sets a flag and closes; `Execute()` calls `PickObject()` and writes results after `ShowDialog()` returns.

## Governing Standards

- AS3500.1 — Water supply (PSFR formula, loading units per fixture type)
- AS3500.3 — Stormwater (Manning's formula, 75% full-bore design)

## For AI Assistants Other Than Claude Code

If you're using Cursor, Copilot, Cline, or another tool — point it at this file for project context. All the architecture and build information above applies regardless of which AI assistant you use.
