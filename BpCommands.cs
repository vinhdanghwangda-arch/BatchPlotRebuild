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
        private static BpForm _form;
        private static BpConfig _cfg;
        private static BpState _state;
        private static Document _sessionDoc;
        private static BpAction _pendingAction = BpAction.None;
        private static bool _actionQueued;

        [CommandMethod("BP")]
        public void Run()
        {
            Document doc =
                Application.DocumentManager.MdiActiveDocument;

            if (doc == null)
                return;

            if (_form != null && !_form.IsDisposed)
            {
                _form.Activate();
                return;
            }

            _sessionDoc = doc;
            _cfg = BpConfig.Load();
            _state = new BpState();

            _form = new BpForm(_cfg, _state);
            _form.ActionRequested += QueueAction;

            _form.FormClosed += (sender, args) =>
            {
                _form = null;
                _sessionDoc = null;
                _pendingAction = BpAction.None;
                _actionQueued = false;
            };

            Application.ShowModelessDialog(_form);
        }

        private void QueueAction(BpAction action)
        {
            if (_sessionDoc == null ||
                _form == null ||
                _form.IsDisposed ||
                _actionQueued)
            {
                return;
            }

            _pendingAction = action;
            _actionQueued = true;

            _sessionDoc.SendStringToExecute(
                "BP_ACTION\n",
                true,
                false,
                false);
        }

        [CommandMethod("BP_ACTION")]
        public void RunPendingAction()
        {
            Document doc =
                Application.DocumentManager.MdiActiveDocument;

            if (doc == null ||
                _form == null ||
                _form.IsDisposed ||
                _cfg == null ||
                _state == null)
            {
                _actionQueued = false;
                return;
            }

            BpAction action = _pendingAction;
            _pendingAction = BpAction.None;
            _actionQueued = false;

            if (action == BpAction.None)
                return;

            try
            {
                Handle(
                    action,
                    doc,
                    doc.Editor,
                    _cfg,
                    _state);
            }
            catch (System.Exception ex)
            {
                _state.Status = "Lỗi: " + ex.Message;
                doc.Editor.WriteMessage(
                    "\nBatchPlotBP: " + ex.Message);
            }

            if (_form != null && !_form.IsDisposed)
                _form.RefreshFromState();
        }

        private void Handle(
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
                        List<Frame> ordered =
                            FrameService.Sort(st.Frames, cfg);

                        FrameService.AddNumbers(doc, ordered);

                        st.Status =
                            "Đã đánh STT cho " +
                            ordered.Count +
                            " khung (layer BPL_STT, không bị in).";
                    }
                    break;

                case BpAction.ClearNumber:
                    {
                        int n = FrameService.ClearNumbers(doc);

                        st.Status =
                            "Đã xóa " + n +
                            " đối tượng STT và đường chéo.";
                        break;
                    }

                case BpAction.Plot:
                    {
                        if (st.Frames.Count == 0)
                            Scan(doc, ed, cfg, st);

                        if (st.Frames.Count == 0)
                            break;

                        using (var dialog = new FolderBrowserDialog())
                        {
                            dialog.Description =
                                "Chọn thư mục lưu các file PDF";

                            dialog.ShowNewFolderButton = true;

                            string initial =
                                !string.IsNullOrWhiteSpace(cfg.OutputFolder) &&
                                System.IO.Directory.Exists(cfg.OutputFolder)
                                    ? cfg.OutputFolder
                                    : PlotService.OutFolder(doc, cfg);

                            dialog.SelectedPath = initial;

                            if (dialog.ShowDialog() != DialogResult.OK)
                            {
                                st.Status = "Đã hủy thao tác in.";
                                break;
                            }

                            cfg.OutputFolder = dialog.SelectedPath;
                            cfg.Save();
                        }

                        List<Frame> ordered =
                            FrameService.Sort(st.Frames, cfg);

                        string err;

                        int ok = PlotService.Plot(
                            doc,
                            ordered,
                            cfg,
                            out err);

                        st.LastPlotSucceeded =
                            ok > 0 && string.IsNullOrEmpty(err);

                        st.OutputFolder =
                            st.LastPlotSucceeded
                                ? cfg.OutputFolder
                                : "";

                        st.Status =
                            "Hoàn tất: " +
                            ok + "/" + ordered.Count +
                            " trang." +
                            (err != null ? " Lỗi: " + err : "") +
                            (st.LastPlotSucceeded
                                ? " Thư mục: " + st.OutputFolder
                                : "");

                        break;
                    }

                case BpAction.OpenFolder:
                    if (!string.IsNullOrWhiteSpace(st.OutputFolder) &&
                        System.IO.Directory.Exists(st.OutputFolder))
                    {
                        Process.Start(
                            "explorer.exe",
                            "\"" + st.OutputFolder + "\"");
                    }
                    else
                    {
                        st.Status =
                            "Chưa có thư mục PDF hợp lệ. " +
                            "Hãy in thành công trước.";
                    }
                    break;
            }
        }

        private void PickSample(
            Document doc,
            Editor ed,
            BpConfig cfg,
            BpState st)
        {
            PromptEntityResult per =
                ed.GetEntity("\nChọn 1 khung mẫu: ");

            if (per.Status != PromptStatus.OK)
            {
                st.Status = "Đã hủy chọn khung mẫu.";
                return;
            }

            string layer;

            using (var tr =
                doc.Database.TransactionManager.StartTransaction())
            {
                var ent = (Entity)tr.GetObject(
                    per.ObjectId,
                    OpenMode.ForRead);

                layer = ent.Layer;
                tr.Commit();
            }

            var pko =
                new PromptKeywordOptions(
                    "\nChọn góc chuẩn để sắp xếp");

            foreach (string anchor in FrameService.Anchors)
                pko.Keywords.Add(anchor);

            pko.Keywords.Default = "TopLeft";
            pko.AllowNone = true;

            PromptResult kr = ed.GetKeywords(pko);

            if (kr.Status != PromptStatus.OK &&
                kr.Status != PromptStatus.None)
            {
                st.Status = "Đã hủy chọn góc chuẩn.";
                return;
            }

            string selectedAnchor =
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
                    Anchor = selectedAnchor
                });

            cfg.Save();
            st.Frames.Clear();

            st.Status =
                "Đã thêm khung mẫu: layer " +
                layer + " / " + selectedAnchor +
                ". Hãy quét khung.";
        }

        private void Scan(
            Document doc,
            Editor ed,
            BpConfig cfg,
            BpState st)
        {
            st.Frames.Clear();

            if (cfg.Samples.Count == 0)
            {
                st.Status =
                    "Chưa có khung mẫu. " +
                    "Bấm 'Chọn khung mẫu' trước.";
                return;
            }

            var ids = new List<ObjectId>();

            if (cfg.SortMode == "Pick")
            {
                ed.WriteMessage(
                    "\nChọn từng khung theo thứ tự in. " +
                    "Nhấn Enter để kết thúc.");

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
                var pko =
                    new PromptKeywordOptions(
                        "\nChọn phạm vi quét " +
                        "[Toàn bản vẽ = ToanBanVe/" +
                        "Vùng quét = VungQuet]");

                pko.Keywords.Add("ToanBanVe");
                pko.Keywords.Add("VungQuet");
                pko.Keywords.Default = "ToanBanVe";
                pko.AllowNone = true;

                PromptResult kr = ed.GetKeywords(pko);

                if (kr.Status != PromptStatus.OK &&
                    kr.Status != PromptStatus.None)
                {
                    st.Status = "Đã hủy quét khung.";
                    return;
                }

                string scope =
                    string.IsNullOrEmpty(kr.StringResult)
                        ? "ToanBanVe"
                        : kr.StringResult;

                SelectionFilter filter =
                    FrameService.MakeFilter(cfg, false);

                PromptSelectionResult sr;

                if (string.Equals(
                    scope,
                    "VungQuet",
                    StringComparison.OrdinalIgnoreCase))
                {
                    sr = ed.GetSelection(filter);
                }
                else
                {
                    sr = ed.SelectAll(filter);
                }

                if (sr.Status == PromptStatus.OK)
                    ids.AddRange(sr.Value.GetObjectIds());
            }

            st.Frames =
                FrameService.ToFrames(
                    doc.Database,
                    ids,
                    cfg);

            st.Status = st.Frames.Count > 0
                ? "Đã quét: " + st.Frames.Count +
                  " khung sẵn sàng."
                : FrameService.Diagnose(doc, cfg);
        }
    }
}
