using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;
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
        private static BpForm _form;
        private static BpConfig _cfg;
        private static BpState _st;

        [CommandMethod("BP")]
        public void Run()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            if (_form != null && !_form.IsDisposed)
            {
                _form.Show();
                if (_form.WindowState == FormWindowState.Minimized)
                    _form.WindowState = FormWindowState.Normal;
                _form.Activate();
                return;
            }

            _cfg = BpConfig.Load();
            _st = new BpState();
            _form = new BpForm(_cfg, _st);
            _form.ActionRequested += OnActionRequested;
            _form.FormClosed += (sender, args) =>
            {
                _form = null;
                _cfg = null;
                _st = null;
            };

            Application.ShowModelessDialog(null, _form, false);
        }

        private void OnActionRequested(BpAction action)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null || _form == null || _form.IsDisposed) return;

            // The form has already hidden itself. Run AutoCAD API work in command context.
            doc.SendStringToExecute("BP_ACTION\n", true, false, false);
        }

        [CommandMethod("BP_ACTION")]
        public void ProcessAction()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null || _form == null || _form.IsDisposed || _cfg == null || _st == null)
                return;

            Editor ed = doc.Editor;
            BpAction action = _form.Action;

            try
            {
                Handle(action, doc, ed, _cfg, _st);
            }
            catch (System.Exception ex)
            {
                _st.Status = "Lỗi: " + ex.Message;
                ed.WriteMessage("\nBatch Plot BP - Lỗi: " + ex.Message);
            }
            finally
            {
                if (_form != null && !_form.IsDisposed)
                {
                    _form.Action = BpAction.None;
                    _form.RefreshFromState();
                    _form.Show();
                    if (_form.WindowState == FormWindowState.Minimized)
                        _form.WindowState = FormWindowState.Normal;
                    _form.Activate();
                }
            }
        }

        private void Handle(BpAction action, Document doc, Editor ed, BpConfig cfg, BpState st)
        {
            switch (action)
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
                        FrameService.AddNumbers(doc, FrameService.Sort(st.Frames, cfg));
                        st.Status = "Đã đánh STT cho " + st.Frames.Count + " khung (layer BPL_STT, không bị in).";
                    }
                    break;

                case BpAction.ClearNumber:
                    {
                        int count = FrameService.ClearNumbers(doc);
                        st.Status = "Đã xóa " + count + " STT.";
                        break;
                    }

                case BpAction.Plot:
                    {
                        if (st.Frames.Count == 0)
                            Scan(doc, ed, cfg, st);
                        if (st.Frames.Count == 0)
                            break;

                        // Custom naming asks for a separate PDF filename for each frame.
                        // PlotService opens the dialogs in the sorted frame order.
                        if (cfg.NameMode != "Custom")
                        {
                            // Use a PDF Save dialog so existing PDFs are visible while navigating.
                            using (var dialog = new SaveFileDialog())
                            {
                                dialog.Title = "Chọn thư mục lưu PDF (các file PDF hiện có sẽ hiển thị)";
                                dialog.Filter = "PDF Document (*.pdf)|*.pdf";
                                dialog.FilterIndex = 1;
                                dialog.DefaultExt = "pdf";
                                dialog.AddExtension = true;
                                dialog.CheckPathExists = true;
                                dialog.OverwritePrompt = false;
                                dialog.RestoreDirectory = true;
                                dialog.FileName = "Chon_thu_muc_luu_PDF.pdf";

                                string initial = !string.IsNullOrWhiteSpace(cfg.OutputFolder) &&
                                                 System.IO.Directory.Exists(cfg.OutputFolder)
                                    ? cfg.OutputFolder
                                    : PlotService.OutFolder(doc, cfg);
                                dialog.InitialDirectory = initial;

                                if (dialog.ShowDialog() != DialogResult.OK)
                                {
                                    st.Status = "Đã hủy thao tác in.";
                                    break;
                                }

                                string selectedFolder = System.IO.Path.GetDirectoryName(dialog.FileName);
                                if (string.IsNullOrWhiteSpace(selectedFolder) ||
                                    !System.IO.Directory.Exists(selectedFolder))
                                {
                                    st.Status = "Thư mục lưu PDF không hợp lệ.";
                                    break;
                                }

                                cfg.OutputFolder = selectedFolder;
                                cfg.Save();
                            }
                        }

                        List<Frame> ordered = FrameService.Sort(st.Frames, cfg);
                        string err;
                        int success = PlotService.Plot(doc, ordered, cfg, out err);
                        st.LastPlotSucceeded = success > 0;
                        st.OutputFolder = st.LastPlotSucceeded ? cfg.OutputFolder : "";
                        st.Status = "Hoàn tất: " + success + "/" + ordered.Count + " trang." +
                                    (err != null ? " Lỗi: " + err : "") +
                                    (st.LastPlotSucceeded ? " Thư mục: " + st.OutputFolder : "");
                        break;
                    }

                case BpAction.OpenFolder:
                    if (!string.IsNullOrWhiteSpace(st.OutputFolder) &&
                        System.IO.Directory.Exists(st.OutputFolder))
                    {
                        Process.Start("explorer.exe", "\"" + st.OutputFolder + "\"");
                    }
                    else
                    {
                        st.Status = "Chưa có thư mục PDF hợp lệ. Hãy in thành công trước.";
                    }
                    break;
            }
        }

        private void PickSample(Document doc, Editor ed, BpConfig cfg, BpState st)
        {
            PromptEntityResult per = ed.GetEntity("\nChọn 1 khung mẫu: ");
            if (per.Status != PromptStatus.OK)
            {
                st.Status = "Đã hủy chọn khung mẫu.";
                return;
            }

            string layer;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                Entity ent = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Entity;
                if (ent == null)
                {
                    st.Status = "Đối tượng đã chọn không hợp lệ.";
                    return;
                }
                layer = ent.Layer;
                tr.Commit();
            }

            PromptKeywordOptions pko = new PromptKeywordOptions("\nGóc chuẩn để sắp xếp");
            foreach (string anchor in FrameService.Anchors)
                pko.Keywords.Add(anchor);
            pko.Keywords.Default = "TopLeft";
            pko.AllowNone = true;

            PromptResult kr = ed.GetKeywords(pko);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None)
            {
                st.Status = "Đã hủy chọn góc chuẩn.";
                return;
            }

            string selectedAnchor = string.IsNullOrEmpty(kr.StringResult)
                ? "TopLeft"
                : kr.StringResult;

            cfg.Samples.RemoveAll(s => string.Equals(s.Layer, layer, StringComparison.OrdinalIgnoreCase));
            cfg.Samples.Add(new SampleDef { Layer = layer, Anchor = selectedAnchor });
            cfg.Save();
            st.Frames.Clear();
            st.Status = "Đã thêm khung mẫu: layer " + layer + " / " + selectedAnchor + ". Hãy quét khung.";
        }

        private void Scan(Document doc, Editor ed, BpConfig cfg, BpState st)
        {
            st.Frames.Clear();

            if (cfg.Samples.Count == 0)
            {
                st.Status = "Chưa có khung mẫu. Bấm 'Chọn khung mẫu' trước.";
                return;
            }

            // Always display exactly these two options, regardless of sort mode.
            PromptKeywordOptions pko = new PromptKeywordOptions("\nChọn phạm vi quét");
            pko.Keywords.Add("ToanBoBanVe", "ToanBoBanVe", "Toan bo ban ve");
            pko.Keywords.Add("QuetVung", "QuetVung", "Quet vung");
            pko.Keywords.Default = "ToanBoBanVe";
            pko.AllowNone = true;

            PromptResult kr = ed.GetKeywords(pko);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None)
            {
                st.Status = "Đã hủy quét khung.";
                return;
            }

            string choice = string.IsNullOrEmpty(kr.StringResult)
                ? "ToanBoBanVe"
                : kr.StringResult;

            SelectionFilter filter = FrameService.MakeFilter(cfg, false);
            PromptSelectionResult sr;

            if (choice == "QuetVung")
            {
                // AutoCAD lets the user specify a selection area/window here.
                sr = ed.GetSelection(filter);
            }
            else
            {
                sr = ed.SelectAll(filter);
            }

            var ids = new List<ObjectId>();
            if (sr.Status == PromptStatus.OK)
                ids.AddRange(sr.Value.GetObjectIds());

            st.Frames = FrameService.ToFrames(doc.Database, ids, cfg);
            st.Status = st.Frames.Count > 0
                ? "Đã quét: " + st.Frames.Count + " khung sẵn sàng."
                : FrameService.Diagnose(doc, cfg);
        }
    }
}
