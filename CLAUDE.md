# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET Framework 4.8 class library that plugs into AutoCAD Civil 3D 2023 as a `NETLOAD`-able command
assembly. It is not a standalone application — it only runs inside the Civil 3D process, invoked via
a command typed at the AutoCAD command line.

## Build

Requires AutoCAD Civil 3D 2023 installed locally (the project references its managed DLLs directly via
absolute `HintPath`s under `C:\Program Files\Autodesk\AutoCAD 2023\...`; there are no NuGet packages).
Build with MSBuild (Visual Studio or `msbuild ExtractHatchBoundaries.sln`) targeting `x64` — the project
is pinned to `PlatformTarget=x64` in both Debug and Release configs because Civil 3D 2023 is a 64-bit
host. There is no `dotnet build` path since this targets classic .NET Framework, and no test project or
CI config in this repo.

To try a change: build, then in a running Civil 3D 2023 session run `NETLOAD` and select the built
`ExtractHatchBoundaries.dll`, then invoke the command (see below).

## Architecture

Everything lives in `ExtractHatchBoundaries/Class1.cs`, in class `HatchBoundaryMerger`, exposing a single
AutoCAD command method:

- **`MERGEHATCHBOUNDARIES`** — batch-processes DWG files, one at a time:
  1. Reads every `*.dwg` in a source folder (currently hardcoded as `sourceFolder` in
     `MergeHatchBoundaries()` — update this path before running against real data) into a side-loaded,
     never-saved `Database` (`ReadDwgFile` with no editor/document opened for it).
  2. Walks model space in that source database for `AcDbHatch` entities whose layer is in
     `LayerFilter` (currently `HATCH-A`, `HATCH-B` — case-insensitive).
  3. For each hatch, converts every loop (`HatchLoop`) to a closed `Polyline` via `LoopToPolyline`, and
     appends it into the *same source database's* model space (so it can be wblock-cloned).
  4. `WblockCloneObjects` clones just those new polyline entities from the source db into the current
     space of the destination database (the active document, i.e. whatever drawing was open in Civil 3D
     when the command was run).
  5. The source database is disposed without ever being written back to disk — source DWGs are
     read-only inputs.

Key implementation detail: `LoopToPolyline` handles polyline-type hatch loops exactly (vertex + bulge),
and line/arc curve loops via bulge reconstruction from arc sweep angle. Spline/ellipse curve segments in
a loop are **not** reconstructed accurately — they're approximated by taking just the curve's start point
(straight-line approximation). If a task involves hatches with curved (spline) boundaries, this is the
function to extend (build a `Spline`/`CompositeCurve` instead of relying on bulge-only `Polyline`
vertices).

`EnsureLayers` creates any missing entries in `LayerFilter` on the destination database before cloning,
so extracted polylines always land on a layer matching their source hatch's layer name.