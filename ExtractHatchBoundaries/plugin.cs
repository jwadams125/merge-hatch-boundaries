using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Geometry;

namespace ExtractHatchBoundaries
{
    public class HatchBoundaryMerger
    {
        [CommandMethod("ExtractHatchBoundaries")]
        public void MergeHatchBoundaries()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;

            PromptStringOptions folderOpts =
                new PromptStringOptions("\nEnter source folder path: ") { AllowSpaces = true };
            PromptResult folderResult = ed.GetString(folderOpts);
            if (folderResult.Status != PromptStatus.OK) return;

            string sourceFolder = folderResult.StringResult.Trim().Trim('"');
            if (!Directory.Exists(sourceFolder))
            {
                ed.WriteMessage($"\nFolder not found: {sourceFolder}");
                return;
            }

            string[] files = Directory.GetFiles(sourceFolder, "*.dwg");

            // Destination = the currently open drawing. Could instead be a brand-new
            // Database you create and save out at the end if you don't want to
            // merge into an already-open file.
            Database destDb = doc.Database;

            int totalExtracted = 0;

            foreach (string dwgPath in files)
            {
                using (Database sourceDb = new Database(false, true))
                {
                    // Side-load the file. No editor/document is opened for it.
                    sourceDb.ReadDwgFile(dwgPath, FileOpenMode.OpenForReadAndAllShare, true, null);

                    ObjectIdCollection idsToClone = new ObjectIdCollection();
                    string dwgName = Path.GetFileNameWithoutExtension(dwgPath);

                    using (Transaction tr = sourceDb.TransactionManager.StartTransaction())
                    {
                        BlockTable bt = (BlockTable)tr.GetObject(sourceDb.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(
                            bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        LayerTable lt = (LayerTable)tr.GetObject(sourceDb.LayerTableId, OpenMode.ForRead);

                        // Snapshot ids first since we're about to add new entities to this same space
                        List<ObjectId> existingIds = new List<ObjectId>();
                        foreach (ObjectId id in ms) existingIds.Add(id);

                        foreach (ObjectId id in existingIds)
                        {
                            if (id.ObjectClass.Name != "AcDbHatch") continue;

                            Hatch hatch = (Hatch)tr.GetObject(id, OpenMode.ForRead);
                            string boundaryLayer = dwgName + hatch.Layer;

                            if (!lt.Has(boundaryLayer))
                            {
                                lt.UpgradeOpen();
                                LayerTableRecord ltr = new LayerTableRecord { Name = boundaryLayer };
                                lt.Add(ltr);
                                tr.AddNewlyCreatedDBObject(ltr, true);
                                lt.DowngradeOpen();
                            }

                            for (int i = 0; i < hatch.NumberOfLoops; i++)
                            {
                                Polyline pline = LoopToPolyline(hatch.GetLoopAt(i));
                                if (pline == null) continue;

                                // Append before setting Layer: an entity that isn't yet
                                // database-resident resolves symbol names (Layer, etc.)
                                // against HostApplicationServices.WorkingDatabase (the active
                                // document), not its eventual owner. Since boundaryLayer only
                                // exists in sourceDb, we must make pline resident there first.
                                ObjectId plineId = ms.AppendEntity(pline);
                                tr.AddNewlyCreatedDBObject(pline, true);
                                pline.Layer = boundaryLayer;
                                idsToClone.Add(plineId);
                            }
                        }

                        tr.Commit(); // commits the temp polylines into the in-memory source db only
                    }

                    if (idsToClone.Count > 0)
                    {
                        IdMapping mapping = new IdMapping();
                        destDb.WblockCloneObjects(
                            idsToClone, destDb.CurrentSpaceId, mapping,
                            DuplicateRecordCloning.Ignore, false);
                        totalExtracted += idsToClone.Count;
                    }
                    // sourceDb is disposed here without ever being saved back to disk
                }
            }

            ed.WriteMessage($"\nExtracted {totalExtracted} boundary polylines from {files.Length} files.");
        }

        // Converts a HatchLoop into a Polyline. Handles the common "polyline loop"
        // case exactly, and line/arc loops via bulge reconstruction. Splines/ellipses
        // in a loop are approximated as straight segments between curve endpoints —
        // if your hatches commonly have curved (non-polyline) boundaries with splines,
        // you'll want to extend this to build a Spline/CompositeCurve instead.
        private static Polyline LoopToPolyline(HatchLoop loop)
        {
            Polyline pline = new Polyline();
            int idx = 0;

            if (loop.IsPolyline)
            {
                foreach (BulgeVertex bv in loop.Polyline)
                    pline.AddVertexAt(idx++, bv.Vertex, bv.Bulge, 0, 0);
            }
            else
            {
                foreach (Curve2d curve2d in loop.Curves)
                {
                    if (curve2d is LineSegment2d line)
                    {
                        pline.AddVertexAt(idx++, line.StartPoint, 0, 0, 0);
                    }
                    else if (curve2d is CircularArc2d arc)
                    {
                        double sweep = arc.EndAngle - arc.StartAngle;
                        if (sweep < 0) sweep += 2 * Math.PI;
                        double bulge = Math.Tan(sweep / 4.0);
                        if (arc.IsClockWise) bulge = -bulge;
                        pline.AddVertexAt(idx++, arc.StartPoint, bulge, 0, 0);
                    }
                    else
                    {
                        // Spline/ellipse fallback: just take the start point (straight
                        // segment approximation). Replace with proper NurbCurve2d
                        // handling if you need exact curved boundaries.
                        pline.AddVertexAt(idx++, curve2d.StartPoint, 0, 0, 0);
                    }
                }
            }

            if (pline.NumberOfVertices < 2)
            {
                pline.Dispose();
                return null;
            }

            pline.Closed = true;
            return pline;
        }
    }
}