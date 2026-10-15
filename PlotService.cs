using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPlotBP
{
    public static class PlotService
    {
        public static string OutFolder(Document doc)
        {
            string dir = null;
            try { dir = Path.GetDirectoryName(doc.Name); } catch { }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string f = Path.Combine(dir, "PDF");
            Directory.CreateDirectory(f);
            return f;
        }

        // Giay nam ngang neu ten co dang "(297.00_x_210.00_MM)" voi rong > cao
        static bool PaperLandscape(string paper)
        {
            Match m = Regex.Match(paper ?? "", @"\(([\d\.]+)_x_([\d\.]+)");
            if (!m.Success) return true;
            double w = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            double h = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return w > h;
        }

        static PlotRotation Rotation(Frame f, BpConfig cfg)
        {
            bool paperLand = PaperLandscape(cfg.Paper);
            bool wantLand = cfg.Orientation == "Auto" ? (f.W >= f.H) : cfg.Orientation != "Portrait";
            return wantLand == paperLand ? PlotRotation.Degrees000 : PlotRotation.Degrees090;
        }

        static PlotInfo MakePlotInfo(Layout lo, Frame f, BpConfig cfg)
        {
            var ps = new PlotSettings(lo.ModelType);
            ps.CopyFrom(lo);

            PlotSettingsValidator v = PlotSettingsValidator.Current;
            v.SetPlotConfigurationName(ps, cfg.Printer, cfg.Paper);
            v.RefreshLists(ps);
            v.SetPlotWindowArea(ps, new Extents2d(f.MinX, f.MinY, f.MaxX, f.MaxY));
            v.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
            v.SetUseStandardScale(ps, true);
            v.SetStdScaleType(ps, StdScaleType.ScaleToFit);
            v.SetPlotCentered(ps, true);
            v.SetPlotRotation(ps, Rotation(f, cfg));
            v.SetCurrentStyleSheet(ps, cfg.PlotStyle);

            var pi = new PlotInfo();
            pi.Layout = lo.ObjectId;
            pi.OverrideSettings = ps;

            var piv = new PlotInfoValidator();
            piv.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
            piv.Validate(pi);
            return pi;
        }

        static void DoPage(PlotEngine pe, PlotProgressDialog ppd, PlotInfo pi, bool last, int index, int total, string name)
        {
            ppd.set_PlotMsgString(PlotMessageIndex.Status, "Đang in: " + name);
            ppd.OnBeginSheet();
            ppd.LowerSheetProgressRange = 0;
            ppd.UpperSheetProgressRange = 100;
            ppd.SheetProgressPos = 0;

            var ppi = new PlotPageInfo();
            pe.BeginPage(ppi, pi, last, null);
            pe.BeginGenerateGraphics(null);
            ppd.SheetProgressPos = 50;
            pe.EndGenerateGraphics(null);
            pe.EndPage(null);

            ppd.SheetProgressPos = 100;
            ppd.OnEndSheet();
            ppd.PlotProgressPos = (index + 1) * 100 / total;
        }

        /// <summary>In danh sach khung (da sap xep). Tra ve so trang in thanh cong; loi ghi vao err.</summary>
        public static int Plot(Document doc, List<Frame> frames, BpConfig cfg, out string err)
        {
            err = null;
            int total = frames.Count;
            if (total == 0) { err = "Không có khung để in."; return 0; }
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            {
                err = "AutoCAD đang bận in, thử lại sau.";
                return 0;
            }

            Database db = doc.Database;
            object oldBg = Application.GetSystemVariable("BACKGROUNDPLOT");
            Application.SetSystemVariable("BACKGROUNDPLOT", 0);
            int done = 0;

            try
            {
                string folder = OutFolder(doc);
                string baseName = Path.GetFileNameWithoutExtension(doc.Name);

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                    var lo = (Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead);

                    using (PlotEngine pe = PlotFactory.CreatePublishEngine())
                    using (var ppd = new PlotProgressDialog(false, total, true))
                    {
                        ppd.set_PlotMsgString(PlotMessageIndex.DialogTitle, "Batch Plot");
                        ppd.set_PlotMsgString(PlotMessageIndex.CancelJobButtonMessage, "Hủy toàn bộ");
                        ppd.set_PlotMsgString(PlotMessageIndex.CancelSheetButtonMessage, "Hủy trang này");
                        ppd.set_PlotMsgString(PlotMessageIndex.SheetSetProgressCaption, "Tiến độ cả bộ");
                        ppd.set_PlotMsgString(PlotMessageIndex.SheetProgressCaption, "Tiến độ trang");
                        ppd.LowerPlotProgressRange = 0;
                        ppd.UpperPlotProgressRange = 100;
                        ppd.PlotProgressPos = 0;
                        ppd.OnBeginPlot();
                        ppd.IsVisible = true;
                        pe.BeginPlot(ppd, null);

                        if (cfg.MergePdf)
                        {
                            // 1 tai lieu nhieu trang -> 1 file PDF
                            string file = Path.Combine(folder, baseName + ".pdf");
                            if (File.Exists(file)) File.Delete(file);
                            PlotInfo first = MakePlotInfo(lo, frames[0], cfg);
                            pe.BeginDocument(first, doc.Name, null, 1, true, file);
                            for (int i = 0; i < total; i++)
                            {
                                PlotInfo pi = (i == 0) ? first : MakePlotInfo(lo, frames[i], cfg);
                                DoPage(pe, ppd, pi, i == total - 1, i, total, (i + 1) + "/" + total);
                                done++;
                            }
                            pe.EndDocument(null);
                        }
                        else
                        {
                            // moi khung 1 file PDF
                            for (int i = 0; i < total; i++)
                            {
                                string file = Path.Combine(folder, baseName + "_" + (i + 1).ToString("000") + ".pdf");
                                if (File.Exists(file)) File.Delete(file);
                                PlotInfo pi = MakePlotInfo(lo, frames[i], cfg);
                                pe.BeginDocument(pi, doc.Name, null, 1, true, file);
                                DoPage(pe, ppd, pi, true, i, total, (i + 1) + "/" + total);
                                pe.EndDocument(null);
                                done++;
                            }
                        }

                        ppd.PlotProgressPos = 100;
                        ppd.OnEndPlot();
                        pe.EndPlot(null);
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                err = ex.Message;
            }
            finally
            {
                Application.SetSystemVariable("BACKGROUNDPLOT", oldBg);
            }
            return done;
        }
    }
}
