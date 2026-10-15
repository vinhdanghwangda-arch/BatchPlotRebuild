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

        public double W { get { return MaxX - MinX; } }
        public double H { get { return MaxY - MinY; } }
    }

    public static class FrameService
    {
        public static readonly string[] Anchors =
        {
            "TopLeft", "TopRight", "BottomLeft", "BottomRight"
        };

        const string SttLayer = "BPL_STT";

        public static SelectionFilter MakeFilter(
            BpConfig cfg, bool layerOnly)
        {
            string layers = string.Join(",",
                cfg.Samples.Select(s => s.Layer)
                    .Distinct().ToArray());

            var tv = new List<TypedValue>();

            if (!layerOnly)
                tv.Add(new TypedValue(
                    (int)DxfCode.Start,
                    "LWPOLYLINE,POLYLINE,INSERT"));

            tv.Add(new TypedValue(
                (int)DxfCode.LayerName, layers));

            tv.Add(new TypedValue(
                (int)DxfCode.LayoutName,
                LayoutManager.Current.CurrentLayout));

            return new SelectionFilter(tv.ToArray());
        }

        static bool IsSampleLayer(BpConfig cfg, string layer)
        {
            return cfg.Samples.Any(s =>
                string.Equals(
                    s.Layer, layer,
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
                        id, OpenMode.ForRead) as Entity;

                    if (ent == null) continue;

                    if (!(ent is Polyline ||
                          ent is Polyline2d ||
                          ent is BlockReference))
                        continue;

                    if (!IsSampleLayer(cfg, ent.Layer))
                        continue;

                    try
                    {
                        Extents3d e = ent.GeometricExtents;

                        list.Add(new Frame
                        {
                            Id = id,
                            Layer = ent.Layer,
                            MinX = e.MinPoint.X,
                            MinY = e.MinPoint.Y,
                            MaxX = e.MaxPoint.X,
                            MaxY = e.MaxPoint.Y
                        });
                    }
                    catch { }
                }

                tr.Commit();
            }

            return list;
        }

        public static string Diagnose(
            Document doc, BpConfig cfg)
        {
            var r = doc.Editor.SelectAll(
                MakeFilter(cfg, true));

            if (r.Status != PromptStatus.OK)
                return "Không tìm thấy đối tượng trên layer khung mẫu.";

            var groups = r.Value.GetObjectIds()
                .GroupBy(i => i.ObjectClass.DxfName)
                .Select(x => x.Key + ": " + x.Count());

            return "Không tìm thấy khung hợp lệ. Đối tượng trên layer mẫu: "
                + string.Join(", ", groups.ToArray());
        }

        static string GetAnchor(Frame f, BpConfig cfg)
        {
            var s = cfg.Samples.FirstOrDefault(x =>
                string.Equals(
                    x.Layer, f.Layer,
                    StringComparison.OrdinalIgnoreCase));

            return s == null || string.IsNullOrWhiteSpace(s.Anchor)
                ? "TopLeft"
                : s.Anchor;
        }

        public static List<Frame> Sort(
            List<Frame> frames, BpConfig cfg)
        {
            if (frames == null || frames.Count == 0)
                return new List<Frame>();

            if (cfg.SortMode == "Pick")
                return new List<Frame>(frames);

            string anchor = GetAnchor(frames[0], cfg);

            bool fromBottom =
                anchor.StartsWith(
                    "Bottom", StringComparison.OrdinalIgnoreCase);

            bool fromRight =
                anchor.EndsWith(
                    "Right", StringComparison.OrdinalIgnoreCase);

            bool rows = cfg.SortMode != "LeftRight";

            var items = frames.Select(f =>
            {
                string a = GetAnchor(f, cfg);
                bool bottom = a.StartsWith(
                    "Bottom", StringComparison.OrdinalIgnoreCase);
                bool right = a.EndsWith(
                    "Right", StringComparison.OrdinalIgnoreCase);

                return new
                {
                    Frame = f,
                    Major = rows
                        ? (bottom ? f.MinY : f.MaxY)
                        : (right ? f.MaxX : f.MinX),
                    Minor = rows
                        ? (right ? f.MaxX : f.MinX)
                        : (bottom ? f.MinY : f.MaxY)
                };
            }).ToList();

            double tolerance = Math.Max(
                1.0,
                0.25 * Math.Min(
                    frames.Min(f => f.W),
                    frames.Min(f => f.H)));

            var majorSorted = rows
                ? items.OrderByDescending(x => x.Major).ToList()
                : items.OrderBy(x => x.Major).ToList();

            var result = new List<Frame>();
            var group = new List<dynamic>();

            double reference = 0;

            Action flush = () =>
            {
                if (group.Count == 0) return;

                IEnumerable<dynamic> sortedMinor = rows
                    ? group.OrderBy(x => fromRight
                        ? -x.Minor : x.Minor)
                    : group.OrderBy(x => fromBottom
                        ? -x.Minor : x.Minor);

                foreach (var item in sortedMinor)
                    result.Add((Frame)item.Frame);

                group.Clear();
            };

            foreach (var item in majorSorted)
            {
                if (group.Count > 0 &&
                    Math.Abs(item.Major - reference) > tolerance)
                {
                    flush();
                }

                if (group.Count == 0)
                    reference = item.Major;

                group.Add(item);
            }

            flush();
            return result;
        }

        public static void AddNumbers(
            Document doc, List<Frame> ordered)
        {
            ClearNumbers(doc);

            Database db = doc.Database;

            using (var tr =
                db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(
                    db.LayerTableId, OpenMode.ForRead);

                if (!lt.Has(SttLayer))
                {
                    lt.UpgradeOpen();

                    var ltr = new LayerTableRecord
                    {
                        Name = SttLayer,
                        IsPlottable = false,
                        Color = Color.FromColorIndex(
                            ColorMethod.ByAci, 1)
                    };

                    lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                }

                var ms = (BlockTableRecord)tr.GetObject(
                    db.CurrentSpaceId, OpenMode.ForWrite);

                int n = 0;

                foreach (Frame f in ordered)
                {
                    n++;

                    var center = new Point3d(
                        (f.MinX + f.MaxX) / 2.0,
                        (f.MinY + f.MaxY) / 2.0,
                        0);

                    var t = new DBText();
                    t.SetDatabaseDefaults();
                    t.Layer = SttLayer;
                    t.TextString = n.ToString();
                    t.Height = 0.15 * f.H;
                    t.Position = center;
                    t.HorizontalMode = TextHorizontalMode.TextCenter;
                    t.VerticalMode = TextVerticalMode.TextVerticalMid;
                    t.AlignmentPoint = center;

                    ms.AppendEntity(t);
                    tr.AddNewlyCreatedDBObject(t, true);
                }

                tr.Commit();
            }
        }

        public static int ClearNumbers(Document doc)
        {
            int n = 0;
            Database db = doc.Database;

            using (var tr =
                db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(
                    db.CurrentSpaceId, OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var t = tr.GetObject(
                        id, OpenMode.ForRead) as DBText;

                    if (t != null && t.Layer == SttLayer)
                    {
                        t.UpgradeOpen();
                        t.Erase();
                        n++;
                    }
                }

                tr.Commit();
            }

            return n;
        }
    }
}
