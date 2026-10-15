using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using System.Windows.Forms;
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
            if (doc == null) return;

            Editor ed = doc.Editor;
            BpConfig cfg = BpConfig.Load();
            BpState st = new BpState();

            while (true)
            {
                BpForm form = new BpForm(cfg, st);
                Application.ShowModalDialog(null, form, false);

                BpAction act = form.Action;
                form.Dispose();

                if (act == BpAction.None) break;

                try
                {
                    Handle(act, doc, ed, cfg, st);
                }
                catch (System.Exception ex)
                {
                    st.Status = "Lỗi: " + ex.Message;
                }
            }
        }

        void Handle(
            BpAction act,
            Document doc,
            Editor ed,
            BpConfig cfg,
            BpState st)
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
                    if (st.Frames.Count == 0)
                        Scan(doc, ed, cfg, st);

                    if (st.Frames.Count > 0)
                    {
                        FrameService.AddNumbers(
                            doc,
                            FrameService.Sort(st.Frames, cfg));

                        st.Status =
                            "Đã đánh STT cho " +
                            st.Frames.Count +
                            " khung (layer BPL_STT).";
                    }
                    break;

                case BpAction.ClearNumber:
                    {
                        int n = FrameService.ClearNumbers(doc);
                        st.Status = "Đã xóa " + n + " STT.";
                    }
                    break;

                case BpAction.Plot:
                    Plot(doc, ed, cfg, st);
                    break;

                case BpAction.OpenFolder:
                    OpenOutputFolder(st);
                    break;
            }
        }

        void Plot(
            Document doc,
            Editor ed,
            BpConfig cfg,
            BpState st)
        {
            if (st.Frames.Count == 0)
                Scan(doc, ed, cfg, st);

            if (st.Frames.Count == 0)
                return;

            // Sort frames before checking names and plotting.
            List<Frame> ordered = FrameService.Sort(st.Frames, cfg);

            // Check duplicate TEXT/MTEXT names before plotting.
            List<string> duplicates =
                PlotService.FindDuplicateTextNames(
                    doc,
                    ordered,
                    cfg);

            if (duplicates.Count > 0)
            {
                string duplicateList =
                    string.Join(
                        Environment.NewLine,
                        duplicates);

                string message =
                    "Phát hiện tên TEXT/MTEXT bị trùng:" +
                    Environment.NewLine +
                    Environment.NewLine +
                    duplicateList +
                    Environment.NewLine +
                    Environment.NewLine;

                if (cfg.MergePdf)
                {
                    message +=
                        "Bạn đang bật chế độ gộp PDF. " +
                        "Tất cả khung sẽ được in vào một file PDF nhiều trang." +
                        Environment.NewLine +
                        "Tên từng khung không được dùng làm tên file PDF riêng." +
                        Environment.NewLine +
                        Environment.NewLine +
                        "Bạn có muốn tiếp tục in không?";
                }
                else
                {
                    message +=
                        "Nếu tiếp tục, plugin sẽ tự thêm hậu tố " +
                        "(1), (2)... khi cần để tránh trùng tên file PDF." +
                        Environment.NewLine +
                        Environment.NewLine +
                        "PDF đã tồn tại sẽ không bị ghi đè." +
                        Environment.NewLine +
                        Environment.NewLine +
                        "Bạn có muốn tiếp tục in không?";
                }

                DialogResult answer = MessageBox.Show(
                    message,
                    "Cảnh báo trùng tên PDF",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (answer != DialogResult.Yes)
                {
                    st.LastPlotSucceeded = false;
                    st.OutputFolder = "";
                    st.Status =
                        "Đã hủy in do tên TEXT/MTEXT bị trùng.";
                    return;
                }
            }

            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Chọn thư mục lưu các file PDF";
                dialog.ShowNewFolderButton = true;

                string initial = PlotService.OutFolder(doc, cfg);

                if (!string.IsNullOrWhiteSpace(initial) &&
                    System.IO.Directory.Exists(initial))
                {
                    dialog.SelectedPath = initial;
                }

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    st.Status = "Đã hủy thao tác in.";
                    return;
                }

                cfg.OutputFolder = dialog.SelectedPath;
                cfg.Save();
            }

            string err;

            int ok = PlotService.Plot(
                doc,
                ordered,
                cfg,
                out err);

            st.LastPlotSucceeded =
                ok == ordered.Count &&
                string.IsNullOrEmpty(err);

            st.OutputFolder =
                st.LastPlotSucceeded
                    ? cfg.OutputFolder
                    : "";

            st.Status =
                "Hoàn tất: " +
                ok + "/" + ordered.Count + " trang." +
                (string.IsNullOrEmpty(err)
                    ? ""
                    : " Lỗi: " + err) +
                (st.LastPlotSucceeded
                    ? " Thư mục: " + st.OutputFolder
                    : "");
        }

        void OpenOutputFolder(BpState st)
        {
            if (st.LastPlotSucceeded &&
                !string.IsNullOrWhiteSpace(st.OutputFolder) &&
                System.IO.Directory.Exists(st.OutputFolder))
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = st.OutputFolder,
                        UseShellExecute = true
                    });
            }
            else
            {
                st.Status =
                    "Chưa có thư mục PDF hợp lệ. Hãy in thành công trước.";
            }
        }

        void PickSample(
            Document doc,
            Editor ed,
            BpConfig cfg,
            BpState st)
        {
            PromptEntityResult per =
                ed.GetEntity("\nChọn 1 khung mẫu: ");

            if (per.Status != PromptStatus.OK)
                return;

            string layer;

            using (var tr =
                doc.Database.TransactionManager.StartTransaction())
            {
                Entity ent =
                    tr.GetObject(
                        per.ObjectId,
                        OpenMode.ForRead) as Entity;

                if (ent == null)
                {
                    st.Status = "Đối tượng đã chọn không hợp lệ.";
                    return;
                }

                layer = ent.Layer;
                tr.Commit();
            }

            var pko = new PromptKeywordOptions(
                "\nGóc chuẩn để sắp xếp");

            foreach (string a in FrameService.Anchors)
                pko.Keywords.Add(a);

            pko.Keywords.Default = "TopLeft";
            pko.AllowNone = true;

            PromptResult kr = ed.GetKeywords(pko);

            if (kr.Status != PromptStatus.OK &&
                kr.Status != PromptStatus.None)
                return;

            string anchor =
                string.IsNullOrEmpty(kr.StringResult)
                    ? "TopLeft"
                    : kr.StringResult;

            cfg.Samples.RemoveAll(
                s => string.Equals(
                    s.Layer,
                    layer,
                    StringComparison.OrdinalIgnoreCase));

            cfg.Samples.Add(
                new SampleDef
                {
                    Layer = layer,
                    Anchor = anchor
                });

            cfg.Save();

            st.Frames.Clear();

            st.Status =
                "Đã thêm khung mẫu: layer " +
                layer + " / " + anchor +
                ". Hãy quét khung.";
        }

        void Scan(
            Document doc,
            Editor ed,
            BpConfig cfg,
            BpState st)
        {
            st.Frames.Clear();

            if (cfg.Samples.Count == 0)
            {
                st.Status =
                    "Chưa có khung mẫu. Bấm 'Chọn khung mẫu' trước.";
                return;
            }

            var ids = new List<ObjectId>();

            if (cfg.SortMode == "Pick")
            {
                ed.WriteMessage(
                    "\nChọn từng khung theo thứ tự in. Enter để kết thúc.");

                while (true)
                {
                    PromptEntityResult r =
                        ed.GetEntity(
                            "\nChọn khung (Enter để kết thúc): ");

                    if (r.Status != PromptStatus.OK)
                        break;

                    ids.Add(r.ObjectId);
                }
            }
            else
            {
                var pko = new PromptKeywordOptions(
                    "\nQuét khung [Toan/Vung]");

                pko.Keywords.Add("Toan");
                pko.Keywords.Add("Vung");
                pko.Keywords.Default = "Toan";
                pko.AllowNone = true;

                PromptResult kr = ed.GetKeywords(pko);

                if (kr.Status != PromptStatus.OK &&
                    kr.Status != PromptStatus.None)
                    return;

                SelectionFilter filter =
                    FrameService.MakeFilter(cfg, false);

                PromptSelectionResult sr =
                    string.Equals(
                        kr.StringResult,
                        "Vung",
                        StringComparison.OrdinalIgnoreCase)
                        ? ed.GetSelection(filter)
                        : ed.SelectAll(filter);

                if (sr.Status == PromptStatus.OK)
                    ids.AddRange(sr.Value.GetObjectIds());
            }

            st.Frames =
                FrameService.ToFrames(
                    doc.Database,
                    ids,
                    cfg);

            st.Status = st.Frames.Count > 0
                ? "Đã quét: " +
                  st.Frames.Count +
                  " khung sẵn sàng."
                : FrameService.Diagnose(doc, cfg);
        }
    }
}
