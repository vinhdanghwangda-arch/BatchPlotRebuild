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
        public static readonly string[] Anchors = { "TopLeft", "TopRight", "BottomLeft", "BottomRight" };
        const string SttLayer = "BPL_STT";

        public static SelectionFilter MakeFilter(BpConfig cfg, bool layerOnly)
        {
            string layers = string.Join(",", cfg.Samples.Select(s => s.Layer).Distinct().ToArray());
            var tv = new List<TypedValue>();
            if (!layerOnly) tv.Add(new TypedValue((int)DxfCode.Start, "LWPOLYLINE,POLYLINE,INSERT"));
            tv.Add(new TypedValue((int)DxfCode.LayerName, layers));
            tv.Add(new TypedValue((int)DxfCode.LayoutName, LayoutManager.Current.CurrentLayout));
            return new SelectionFilter(tv.ToArray());
        }

        static bool IsSampleLayer(BpConfig cfg, string layer)
        {
            return cfg.Samples.Any(s => string.Equals(s.Layer, layer, StringComparison.OrdinalIgnoreCase));
        }

        public static List<Frame> ToFrames(Database db, IEnumerable<ObjectId> ids, BpConfig cfg)
        {
            var list = new List<Frame>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    if (!(ent is Polyline || ent is Polyline2d || ent is BlockReference)) continue;
                    if (!IsSampleLayer(cfg, ent.Layer)) continue;
                    try
                    {
                        Extents3d e = ent.GeometricExtents;
                        list.Add(new Frame
                        {
                            Id = id,
                            Layer = ent.Layer,
                            MinX = e.MinPoint.X, MinY = e.MinPoint.Y,
                            MaxX = e.MaxPoint.X, MaxY = e.MaxPoint.Y
                        });
                    }
                    catch { }
                }
                tr.Commit();
            }
            return list;
        }

        public static string Diagnose(Document doc, BpConfig cfg)
        {
            var r = doc.Editor.SelectAll(MakeFilter(cfg, true));
            if (r.Status != PromptStatus.OK)
                return "Không có đối tượng nào trên layer khung mẫu ở tab hiện tại.";
            var g = r.Value.GetObjectIds()
                     .GroupBy(i => i.ObjectClass.DxfName)
                     .Select(x => x.Key + ": " + x.Count());
            return "Không thấy khung hợp lệ (chỉ nhận polyline/block). Trên layer mẫu có: " + string.Join(", ", g.ToArray());
        }

        // ---------- Sap xep ----------
        static Point2d AnchorOf(Frame f, BpConfig cfg)
        {
            var s = cfg.Samples.FirstOrDefault(x => string.Equals(x.Layer, f.Layer, StringComparison.OrdinalIgnoreCase));
            string a = (s != null ? s.Anchor : "TopLeft").ToUpperInvariant();
            double x2 = a.EndsWith("LEFT") ? f.MinX : f.MaxX;
            double y2 = a.StartsWith("TOP") ? f.MaxY : f.MinY;
            return new Point2d(x2, y2);
        }

        class Item { public Frame F; public double Maj; public double Min; }

        public static List<Frame> Sort(List<Frame> frames, BpConfig cfg)
        {
            if (frames.Count == 0 || cfg.SortMode == "Pick") return new List<Frame>(frames);

            bool rows = cfg.SortMode != "LeftRight"; // TopDown: hang tren->duoi, trong hang trai->phai
            double tol = 0.5 * (rows ? frames.Min(f => f.H) : frames.Min(f => f.W));

            var items = frames.Select(f =>
            {
                Point2d p = AnchorOf(f, cfg);
                return new Item { F = f, Maj = rows ? p.Y : p.X, Min = rows ? p.X : p.Y };
            }).ToList();

            items = (rows ? items.OrderByDescending(i => i.Maj) : items.OrderBy(i => i.Maj)).ToList();

            var result = new List<Frame>();
            var cur = new List<Item>();
            double refv = 0;
            Action flush = () =>
            {
                var g = rows ? cur.OrderBy(i => i.Min) : cur.OrderByDescending(i => i.Min);
                result.AddRange(g.Select(i => i.F));
                cur.Clear();
            };
            foreach (var it in items)
            {
                if (cur.Count > 0 && Math.Abs(it.Maj - refv) < tol) cur.Add(it);
                else { flush(); cur.Add(it); refv = it.Maj; }
            }
            flush();
            return result;
        }

        // ---------- STT ----------
        public static void AddNumbers(Document doc, List<Frame> ordered)
        {
            ClearNumbers(doc);
            Database db = doc.Database;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(SttLayer))
                {
                    lt.UpgradeOpen();
                    var ltr = new LayerTableRecord
                    {
                        Name = SttLayer,
                        IsPlottable = false,
                        Color = Color.FromColorIndex(ColorMethod.ByAci, 1)
                    };
                    lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                }
                var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                int n = 0;
                foreach (Frame f in ordered)
                {
                    n++;
                    var c = new Point3d((f.MinX + f.MaxX) / 2.0, (f.MinY + f.MaxY) / 2.0, 0);
                    var t = new DBText();
                    t.SetDatabaseDefaults();
                    t.Layer = SttLayer;
                    t.TextString = n.ToString();
                    t.Height = 0.15 * f.H;
                    t.Position = c;
                    t.HorizontalMode = TextHorizontalMode.TextCenter;
                    t.VerticalMode = TextVerticalMode.TextVerticalMid;
                    t.AlignmentPoint = c;
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
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var t = tr.GetObject(id, OpenMode.ForRead) as DBText;
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
