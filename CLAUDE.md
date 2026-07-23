# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET Framework 4.8 class library that plugs into AutoCAD Civil 3D 2023 as a `NETLOAD`-able command
assembly. It is not a standalone application — it only runs inside the Civil 3D process, invoked via
a command typed at the AutoCAD command line.

## Build

Requires AutoCAD Civil 3D 2023 installed locally (the project references its managed DLLs directly via
absolute `HintPath`s under `C:\Program Files\Autodesk\AutoCAD 2023\...`; there are no NuGet packages).
Those references are `<Private>False</Private>` (not copied to the output folder), since Civil 3D already
provides them at runtime when the assembly is `NETLOAD`ed. Build with MSBuild (Visual Studio or
`msbuild ExtractHatchBoundaries.sln`) targeting `x64` — the project
is pinned to `PlatformTarget=x64` in both Debug and Release configs because Civil 3D 2023 is a 64-bit
host. There is no `dotnet build` path since this targets classic .NET Framework, and no test project or
CI config in this repo.

To try a change: build, then in a running Civil 3D 2023 session run `NETLOAD` and select the built
`ExtractHatchBoundaries.dll`, then invoke the command (see below).

## Architecture

Everything lives in `ExtractHatchBoundaries/plugin.cs`, in class `HatchBoundaryMerger`, exposing a single
AutoCAD command method:

- **`ExtractHatchBoundaries`** — batch-processes DWG files, one at a time:
  1. Prompts on the command line for a source folder path (`ed.GetString`, spaces/quotes allowed), then
     reads every `*.dwg` in it into a side-loaded, never-saved `Database` (`ReadDwgFile` with no
     editor/document opened for it).
  2. `ExplodeNestedBlocks` recursively explodes every block reference in that source database's model
     space that isn't on a frozen layer (frozen ones are left alone, matching AutoCAD's own
     display/regen behavior), repeating passes until nothing new is exploded — this surfaces hatches
     buried arbitrarily deep inside nested blocks as top-level model-space entities. A block that fails
     to explode (e.g. a proxy entity from a missing object enabler) is reported via `ed.WriteMessage` and
     skipped rather than aborting the whole source file.
  3. Walks model space for `AcDbHatch` entities. Non-solid-fill hatches (`hatch.IsSolidFill`) are
     skipped — only solid fills are extracted.
  4. For each remaining hatch, `GetBoundarySuffix` inspects the hatch's *original* layer name for a
     `PM`/`P1`/`P2`/`P3` token (case-insensitive substring match) and maps it to a single suffix
     character (`M`/`1`/`2`/`3`). Hatches whose layer contains none of those tokens are skipped with a
     command-line warning. The boundary layer name is `<dwgFileNameWithoutExtension><suffix>` (e.g. a
     hatch on `S-PV-02-HATCH_P1_COLOR 1` in `EA.dwg` becomes layer `EA1`). If that layer doesn't exist
     yet in the source db, it's created and its `Color` is copied from the hatch's original source layer.
  5. Every loop (`HatchLoop`) on the hatch is converted to a closed `Polyline` via `LoopToPolyline` and
     appended into the *same source database's* model space (so it can be wblock-cloned). The entity is
     appended **before** `pline.Layer` is set — see the comment at that call site: a non-database-resident
     entity resolves symbol names like `Layer` against `HostApplicationServices.WorkingDatabase` (the
     active document), not its eventual owner, so setting `.Layer` too early throws `eKeyNotFound` for any
     layer that only exists in the side-loaded source db.
  6. `WblockCloneObjects` clones just those new polyline entities from the source db into the current
     space of the destination database (the active document, i.e. whatever drawing was open in Civil 3D
     when the command was run). Referenced records (like the new boundary layers) get auto-cloned into
     the destination db as part of this — there's no separate step to pre-create layers there.
  7. The source database is disposed without ever being written back to disk — source DWGs are
     read-only inputs.

Key implementation detail: `LoopToPolyline` handles polyline-type hatch loops exactly (vertex + bulge),
and line/arc curve loops via bulge reconstruction from arc sweep angle. Spline/ellipse curve segments in
a loop are **not** reconstructed accurately — they're approximated by taking just the curve's start point
(straight-line approximation). If a task involves hatches with curved (spline) boundaries, this is the
function to extend (build a `Spline`/`CompositeCurve` instead of relying on bulge-only `Polyline`
vertices).