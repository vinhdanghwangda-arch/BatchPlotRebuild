using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace BatchPlotBP
{
    public class Frame
    {
        public ObjectId Id;
        public double MinX, MinY, MaxX, MaxY;
        public string Layer;

        public double W
        {
            get { return MaxX - MinX; }
        }

        public double H
        {
            get { return MaxY - MinY; }
        }
    }

    public static class FrameService
    {
        public static readonly string[] Anchors =
        {
            "TopLeft",
            "TopRight",
            "BottomLeft",
            "BottomRight"
        };

        private const string SttLayer = "BPL_STT";

        public static SelectionFilter MakeFilter(
            BpConfig cfg,
            bool layerOnly)
        {
            string layers = string.Join(
                ",",
                cfg.Samples
                    .Select(s => s.Layer)
                    .Distinct()
                    .ToArray());

            var tv = new List<TypedValue>();

            if (!layerOnly)
            {
                tv.Add(
                    new TypedValue(
                        (int)DxfCode.Start,
                        "LWPOLYLINE,POLYLINE,INSERT"));
            }

            tv.Add(
                new TypedValue(
                    (int)DxfCode.LayerName,
                    layers));

            tv.Add(
                new TypedValue(
                    (int)DxfCode.LayoutName,
                    LayoutManager.Current.CurrentLayout));

            return new SelectionFilter(tv.ToArray());
        }

        private static bool IsSampleLayer(
            BpConfig cfg,
            string layer)
        {
            return cfg.Samples.Any(
                s => string.Equals(
                    s.Layer,
                    layer,
                    StringComparison.OrdinalIgnoreCase));
        }

        public static List<Frame> ToFrames(
            Database db,
            IEnumerable<ObjectId> ids,
            BpConfig cfg)
        {
            var list = new List<Frame>();

            using (var tr =
                db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    var ent = tr.GetObject(
                        id,
                        OpenMode.ForRead) as Entity;

                    if (ent == null)
                        continue;

                    if (!(ent is Polyline ||
                          ent is Polyline2d ||
                          ent is BlockReference))
                    {
                        continue;
                    }

                    if (!IsSampleLayer(cfg, ent.Layer))
                        continue;

                    try
                    {
                        Extents3d e = ent.GeometricExtents;

                        list.Add(
                            new Frame
                            {
                                Id = id,
                                Layer = ent.Layer,
                                MinX = e.MinPoint.X,
                                MinY = e.MinPoint.Y,
                                MaxX = e.MaxPoint.X,
                                MaxY = e.MaxPoint.Y
                            });
                    }
                    catch
                    {
                        // Ignore objects without valid geometric extents.
                    }
                }

                tr.Commit();
            }

            return list;
        }

        public static string Diagnose(
            Document doc,
            BpConfig cfg)
        {
            var r = doc.Editor.SelectAll(
                MakeFilter(cfg, true));

            if (r.Status != PromptStatus.OK)
            {
                return
                    "Không có đối tượng nào trên layer " +
                    "khung mẫu ở tab hiện tại.";
            }

            var g = r.Value.GetObjectIds()
                .GroupBy(i => i.ObjectClass.DxfName)
                .Select(
                    x => x.Key + ": " + x.Count());

            return
                "Không thấy khung hợp lệ (chỉ nhận " +
                "polyline/block). Trên layer mẫu có: " +
                string.Join(", ", g.ToArray());
        }

        // ---------- Sorting ----------

        private static Point2d AnchorOf(
            Frame f,
            BpConfig cfg)
        {
            var s = cfg.Samples.FirstOrDefault(
                x => string.Equals(
                    x.Layer,
                    f.Layer,
                    StringComparison.OrdinalIgnoreCase));

            string a =
                (s != null ? s.Anchor : "TopLeft")
                .ToUpperInvariant();

            double x2 =
                a.EndsWith("LEFT")
                    ? f.MinX
                    : f.MaxX;

            double y2 =
                a.StartsWith("TOP")
                    ? f.MaxY
                    : f.MinY;

            return new Point2d(x2, y2);
        }

        private class Item
        {
            public Frame F;
            public double Maj;
            public double Min;
        }

        public static List<Frame> Sort(
            List<Frame> frames,
            BpConfig cfg)
        {
            if (frames.Count == 0 ||
                cfg.SortMode == "Pick")
            {
                return new List<Frame>(frames);
            }

            string anchor =
                cfg.Samples.Count > 0
                    ? cfg.Samples[0].Anchor
                    : "TopLeft";

            bool fromTop = anchor.StartsWith(
                "Top",
                StringComparison.OrdinalIgnoreCase);

            bool fromLeft = anchor.EndsWith(
                "Left",
                StringComparison.OrdinalIgnoreCase);

            bool rows = cfg.SortMode != "LeftRight";

            double tol =
                0.5 * Math.Max(
                    1e-6,
                    rows
                        ? frames.Min(f => f.H)
                        : frames.Min(f => f.W));

            var items = frames.Select(
                f =>
                {
                    Point2d p = AnchorOf(f, cfg);

                    return new Item
                    {
                        F = f,
                        Maj = rows ? p.Y : p.X,
                        Min = rows ? p.X : p.Y
                    };
                }).ToList();

            bool majorDescending =
                rows ? fromTop : !fromLeft;

            bool minorAscending =
                rows ? fromLeft : !fromTop;

            items = (
                majorDescending
                    ? items.OrderByDescending(i => i.Maj)
                    : items.OrderBy(i => i.Maj)
                ).ToList();

            var result = new List<Frame>();
            var cur = new List<Item>();
            double refv = 0;

            Action flush = () =>
            {
                IEnumerable<Item> group =
                    minorAscending
                        ? cur.OrderBy(i => i.Min)
                        : cur.OrderByDescending(i => i.Min);

                result.AddRange(group.Select(i => i.F));
                cur.Clear();
            };

            foreach (var it in items)
            {
                if (cur.Count > 0 &&
                    Math.Abs(it.Maj - refv) < tol)
                {
                    cur.Add(it);
                }
                else
                {
                    if (cur.Count > 0)
                        flush();

                    cur.Add(it);
                    refv = it.Maj;
                }
            }

            if (cur.Count > 0)
                flush();

            return result;
        }

        // ---------- Drawing numbering ----------

        public static void AddNumbers(
            Document doc,
            List<Frame> ordered)
        {
            ClearNumbers(doc);

            if (ordered == null || ordered.Count == 0)
                return;

            Database db = doc.Database;

            double minHeight = ordered
                .Where(f => f != null && f.H > 0)
                .Select(f => f.H)
                .DefaultIfEmpty(0)
                .Min();

            if (minHeight <= 0)
                return;

            // Text height equals two-thirds of the smallest frame height.
            double textHeight = minHeight * 2.0 / 3.0;

            using (var tr =
                db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(
                    db.LayerTableId,
                    OpenMode.ForRead);

                if (!lt.Has(SttLayer))
                {
                    lt.UpgradeOpen();

                    var ltr = new LayerTableRecord
                    {
                        Name = SttLayer,
                        IsPlottable = false,
                        Color = Color.FromColorIndex(
                            ColorMethod.ByAci,
                            1)
                    };

                    lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                }

                var ms = (BlockTableRecord)tr.GetObject(
                    db.CurrentSpaceId,
                    OpenMode.ForWrite);

                int n = 0;

                foreach (Frame f in ordered)
                {
                    if (f == null || f.W <= 0 || f.H <= 0)
                        continue;

                    n++;

                    // Diagonal from bottom-left to top-right.
                    var diagonal = new Line(
                        new Point3d(f.MinX, f.MinY, 0),
                        new Point3d(f.MaxX, f.MaxY, 0));

                    diagonal.SetDatabaseDefaults();
                    diagonal.Layer = SttLayer;

                    ms.AppendEntity(diagonal);
                    tr.AddNewlyCreatedDBObject(
                        diagonal,
                        true);

                    var center = new Point3d(
                        (f.MinX + f.MaxX) / 2.0,
                        (f.MinY + f.MaxY) / 2.0,
                        0);

                    var t = new DBText();

                    t.SetDatabaseDefaults();
                    t.Layer = SttLayer;
                    t.TextString = n.ToString();
                    t.Height = textHeight;
                    t.Position = center;

                    t.HorizontalMode =
                        TextHorizontalMode.TextCenter;

                    t.VerticalMode =
                        TextVerticalMode.TextVerticalMid;

                    t.AlignmentPoint = center;

                    ms.AppendEntity(t);
                    tr.AddNewlyCreatedDBObject(t, true);
                }

                tr.Commit();
            }
        }

        public static int ClearNumbers(Document doc)
        {
            int count = 0;
            Database db = doc.Database;

            using (var tr =
                db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(
                    db.CurrentSpaceId,
                    OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(
                        id,
                        OpenMode.ForRead) as Entity;

                    if (ent == null ||
                        !string.Equals(
                            ent.Layer,
                            SttLayer,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!(ent is DBText) &&
                        !(ent is Line))
                    {
                        continue;
                    }

                    ent.UpgradeOpen();
                    ent.Erase();
                    count++;
                }

                tr.Commit();
            }

            return count;
        }
    }
}
