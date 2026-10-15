using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(BatchPlotBP.BpCommands))]

namespace BatchPlotBP
{
    public class BpCommands
    {
        [CommandMethod("BP")]
        public void Run()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            BpConfig cfg = BpConfig.Load();
            BpState st = new BpState();

            // Hop thoai dong lai de nguoi dung chon doi tuong tren ban ve, xong mo lai
            while (true)
            {
                BpForm form = new BpForm(cfg, st);
                Application.ShowModalDialog(null, form, false);
                BpAction act = form.Action;
                form.Dispose();
                if (act == BpAction.None) break;
                try { Handle(act, doc, ed, cfg, st); }
                catch (System.Exception ex) { st.Status = "Lỗi: " + ex.Message; }
            }
        }

        void Handle(BpAction act, Document doc, Editor ed, BpConfig cfg, BpState st)
        {
            switch (act)
            {
                case BpAction.Pick:
                    PickSample(doc, ed, cfg, st);
                    break;

                case BpAction.Clear:
                    cfg.Samples.Clear();
                    st.Frames.Clear();
                    cfg.Save();
                    st.Status = "Đã xóa khung mẫu.";
                    break;

                case BpAction.Scan:
                    Scan(doc, ed, cfg, st);
                    break;

                case BpAction.Number:
                    {
                        if (st.Frames.Count == 0) Scan(doc, ed, cfg, st);
                        if (st.Frames.Count > 0)
                        {
                            FrameService.AddNumbers(doc, FrameService.Sort(st.Frames, cfg));
                            st.Status = "Đã đánh STT cho " + st.Frames.Count + " khung (layer BPL_STT, không bị in).";
                        }
                        break;
                    }

                case BpAction.ClearNumber:
                    {
                        int n = FrameService.ClearNumbers(doc);
                        st.Status = "Đã xóa " + n + " STT.";
                        break;
                    }

                case BpAction.Plot:
                    {
                        if (st.Frames.Count == 0) Scan(doc, ed, cfg, st);
                        if (st.Frames.Count == 0) break;
                        List<Frame> ordered = FrameService.Sort(st.Frames, cfg);
                        string err;
                        int ok = PlotService.Plot(doc, ordered, cfg, out err);
                        st.Status = "Hoàn tất: " + ok + "/" + ordered.Count + " trang."
                                    + (err != null ? " Lỗi: " + err : "");
                        break;
                    }

                case BpAction.OpenFolder:
                    Process.Start("explorer.exe", PlotService.OutFolder(doc));
                    break;
            }
        }

        void PickSample(Document doc, Editor ed, BpConfig cfg, BpState st)
        {
            PromptEntityResult per = ed.GetEntity("\nChọn 1 khung mẫu: ");
            if (per.Status != PromptStatus.OK) return;

            string layer;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var ent = (Entity)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                layer = ent.Layer;
                tr.Commit();
            }

            var pko = new PromptKeywordOptions("\nGóc chuẩn để sắp xếp");
            foreach (string a in FrameService.Anchors) pko.Keywords.Add(a);
            pko.Keywords.Default = "TopLeft";
            pko.AllowNone = true;
            PromptResult kr = ed.GetKeywords(pko);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            string anchor = string.IsNullOrEmpty(kr.StringResult) ? "TopLeft" : kr.StringResult;

            cfg.Samples.RemoveAll(s => string.Equals(s.Layer, layer, StringComparison.OrdinalIgnoreCase));
            cfg.Samples.Add(new SampleDef { Layer = layer, Anchor = anchor });
            cfg.Save();
            st.Frames.Clear();
            st.Status = "Đã thêm khung mẫu: layer " + layer + " / " + anchor + ". Hãy quét khung.";
        }

        void Scan(Document doc, Editor ed, BpConfig cfg, BpState st)
        {
            st.Frames.Clear();
            if (cfg.Samples.Count == 0)
            {
                st.Status = "Chưa có khung mẫu. Bấm 'Chọn khung mẫu' trước.";
                return;
            }

            var ids = new List<ObjectId>();
            if (cfg.SortMode == "Pick")
            {
                ed.WriteMessage("\nChọn từng khung theo thứ tự in. Enter để kết thúc.");
                while (true)
                {
                    PromptEntityResult r = ed.GetEntity("\nChọn khung (Enter để kết thúc): ");
                    if (r.Status != PromptStatus.OK) break;
                    ids.Add(r.ObjectId);
                }
            }
            else
            {
                var pko = new PromptKeywordOptions("\nQuét khung [Toan/Vung]");
                pko.Keywords.Add("Toan");
                pko.Keywords.Add("Vung");
                pko.Keywords.Default = "Toan";
                pko.AllowNone = true;
                PromptResult kr = ed.GetKeywords(pko);
                if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;

                SelectionFilter filter = FrameService.MakeFilter(cfg, false);
                PromptSelectionResult sr = (kr.StringResult == "Vung")
                    ? ed.GetSelection(filter)
                    : ed.SelectAll(filter);
                if (sr.Status == PromptStatus.OK) ids.AddRange(sr.Value.GetObjectIds());
            }

            st.Frames = FrameService.ToFrames(doc.Database, ids, cfg);
            st.Status = st.Frames.Count > 0
                ? "Đã quét: " + st.Frames.Count + " khung sẵn sàng."
                : FrameService.Diagnose(doc, cfg);
        }
    }
}
