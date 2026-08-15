# WCT-PlumbFlow

Open-source Revit add-in for stormwater and water supply network traversal, aligned to AS3500 (Australian Plumbing Standard).

Part of the **WCT Plumbing Tools** package — [watercad.com.au](https://watercad.com.au)

---

## What it does

**WCT-PlumbFlow** adds a ribbon tab to Autodesk Revit with two tools:

### Stormwater Traversal
Walks the Revit MEP connector graph upstream from a selected pipe, accumulating flow (L/s) from rain water outlet (RWO) families. Labels junction nodes with accumulated flow and a Manning's pipe size recommendation at 75% full-bore capacity per AS3500.3.

### Water Supply Traversal
Traverses the domestic water supply network, locates sub-meters, ranks them by hydraulic distance (most disadvantaged fixture), and calculates the building main peak simultaneous demand using the AS3500.1 PSFR formula:

```
Q = 0.09 × √(ΣLU)
```

---

## Requirements

- Autodesk Revit 2025
- .NET 8.0 (included with Revit 2025)
- A correctly configured Revit plumbing model with WCT-compliant families

---

## Build

```
dotnet build WCT-PlumbFlow.csproj -c R2025 --no-incremental
```

Output goes to `bin\R2025\`. The PostBuildEvent auto-deploys to your Revit 2025 addins folder. **Close Revit before building.**

---

## Educational Purpose

WCT-PlumbFlow is a reference implementation demonstrating how hydraulic engineering workflows can be embedded within a Revit BIM environment using AS3500 as the governing standard.

This tool is provided for educational and demonstrative purposes only, therefore does not constitute a design deliverable and must not be relied upon as a substitute for the independent judgement of a suitably qualified hydraulic engineer.

The software is provided without warranty of any kind. Use of this software does not confer any right to apply its outputs as a formal engineering assessment.

---

## Key Files

| File | Purpose |
|------|---------|
| `PipeNetworkTraverser.cs` | Core BFS traversal engine, junction marker placement, Manning's sizing |
| `WaterSupplyTraverser.cs` | Water supply BFS, sub-meter ranking, PSFR calculation |
| `ManningsFormula.cs` | Pipe sizing from flow (L/s) and grade per AS3500.3 |
| `StormwaterSizeCommand.cs` | Ribbon command entry point — stormwater |
| `WaterSizeCommand.cs` | Ribbon command entry point — water supply |
| `App.cs` | Ribbon tab registration |

---

## For AI Assistants

See `AIassist.md` for full project context including build instructions, architecture notes, and design decisions. Point any AI assistant at that file for immediate project context.

---

## Licence

MIT — free to use, modify, and distribute with attribution.
