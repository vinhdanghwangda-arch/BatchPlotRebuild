using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPlotBP
{
    public static class PlotService
    {
        public static string OutFolder(
            Document doc,
            BpConfig cfg = null)
        {
            string dir = cfg == null ? null : cfg.OutputFolder;

            if (string.IsNullOrWhiteSpace(dir) ||
                !Directory.Exists(dir))
            {
                try
                {
                    dir = Path.GetDirectoryName(doc.Name);
                }
                catch
                {
                    dir = null;
                }
            }

            if (string.IsNullOrWhiteSpace(dir) ||
                !Directory.Exists(dir))
            {
                dir = Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments);
            }

            return dir;
        }

        static string SafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "UNNAMED";

            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');

            // Preserve spaces in PDF file names.
            value = value.Trim();
            value = value.Trim('.', ' ');

            return string.IsNullOrWhiteSpace(value)
                ? "UNNAMED"
                : value;
        }

        static string TextName(
            Database db,
            Frame frame,
            BpConfig cfg)
        {
            if (cfg == null ||
                cfg.NameMode != "Text" ||
                string.IsNullOrWhiteSpace(cfg.NameLayer))
            {
                return null;
            }

            var candidates =
                new List<Tuple<double, string, string>>();

            using (var tr =
                db.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(
                    db.CurrentSpaceId,
                    OpenMode.ForRead);

                foreach (ObjectId id in space)
                {
                    var ent =
                        tr.GetObject(
                            id,
                            OpenMode.ForRead) as Entity;

                    if (ent == null ||
                        !string.Equals(
                            ent.Layer,
                            cfg.NameLayer,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string value = null;
                    Point3d pos = Point3d.Origin;

                    if (ent is DBText &&
                        (cfg.NameObjectType == "Text" ||
                         cfg.NameObjectType == "Both"))
                    {
                        var t = (DBText)ent;
                        value = t.TextString;
                        pos = t.Position;
                    }
                    else if (ent is MText &&
                        (cfg.NameObjectType == "MText" ||
                         cfg.NameObjectType == "Both"))
                    {
                        var t = (MText)ent;
                        value = t.Contents;
                        pos = t.Location;
                    }

                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    if (pos.X < frame.MinX ||
                        pos.X > frame.MaxX ||
                        pos.Y < frame.MinY ||
                        pos.Y > frame.MaxY)
                    {
                        continue;
                    }

                    double cx =
                        (frame.MinX + frame.MaxX) / 2.0;

                    double cy =
                        (frame.MinY + frame.MaxY) / 2.0;

                    double d =
                        Math.Pow(pos.X - cx, 2) +
                        Math.Pow(pos.Y - cy, 2);

                    // Remove common MText formatting codes.
                    value = Regex.Replace(
                        value,
                        @"\\[A-Za-z]+[^;]*;",
                        " ");

                    value = value.Replace("{", " ")
                                 .Replace("}", " ")
                                 .Replace("\\P", " ");

                    candidates.Add(
                        Tuple.Create(
                            d,
                            value.Trim(),
                            ent.GetType().Name));
                }

                tr.Commit();
            }

            if (candidates.Count == 0)
                return null;

            // If multiple labels exist, choose the one nearest to frame center.
            return SafeFileName(
                candidates
                    .OrderBy(x => x.Item1)
                    .First()
                    .Item2);
        }

        /// <summary>
        /// Finds duplicate TEXT/MTEXT names among the selected frames.
        /// Comparison ignores letter case.
        /// </summary>
        public static List<string> FindDuplicateTextNames(
            Document doc,
            List<Frame> frames,
            BpConfig cfg)
        {
            var duplicates = new List<string>();

            if (doc == null ||
                cfg == null ||
                cfg.NameMode != "Text" ||
                frames == null ||
                frames.Count == 0)
            {
                return duplicates;
            }

            var names = new List<string>();

            foreach (Frame frame in frames)
            {
                string name = TextName(
                    doc.Database,
                    frame,
                    cfg);

                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }

            return names
                .GroupBy(
                    name => name,
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .OrderBy(
                    name => name,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static string OutputName(
            Database db,
            Document doc,
            Frame frame,
            BpConfig cfg,
            int index)
        {
            string baseName =
                Path.GetFileNameWithoutExtension(doc.Name);

            string drawingName =
                cfg != null && cfg.NameMode == "Text"
                    ? TextName(db, frame, cfg)
                    : null;

            if (string.IsNullOrWhiteSpace(drawingName))
                drawingName = SafeFileName(baseName);

            // Text mode: use the TEXT/MTEXT name without numeric prefix.
            if (cfg != null && cfg.NameMode == "Text")
                return drawingName;

            // CAD mode: retain the existing numeric prefix.
            return index.ToString("000") + "_" + drawingName;
        }

        /// <summary>
        /// Generates a unique PDF path without overwriting existing files.
        /// Spaces in file names are preserved.
        /// </summary>
        static string UniquePdfPath(
            string folder,
            string outputName,
            HashSet<string> reservedPaths)
        {
            string baseName =
                Path.GetFileNameWithoutExtension(outputName);

            baseName = SafeFileName(baseName);

            string candidate = Path.Combine(
                folder,
                baseName + ".pdf");

            int suffix = 1;

            while (File.Exists(candidate) ||
                   reservedPaths.Contains(candidate))
            {
                candidate = Path.Combine(
                    folder,
                    baseName + " (" + suffix + ").pdf");

                suffix++;
            }

            reservedPaths.Add(candidate);

            return candidate;
        }

        static bool PaperLandscape(string paper)
        {
            Match m = Regex.Match(
                paper ?? "",
                @"\(([\d\.]+)_x_([\d\.]+)");

            if (!m.Success)
                return true;

            double w = double.Parse(
                m.Groups[1].Value,
                CultureInfo.InvariantCulture);

            double h = double.Parse(
                m.Groups[2].Value,
                CultureInfo.InvariantCulture);

            return w > h;
        }

        static PlotRotation Rotation(
            Frame f,
            BpConfig cfg)
        {
            bool paperLand = PaperLandscape(cfg.Paper);

            bool wantLand =
                cfg.Orientation == "Auto"
                    ? f.W >= f.H
                    : cfg.Orientation != "Portrait";

            return wantLand == paperLand
                ? PlotRotation.Degrees000
                : PlotRotation.Degrees090;
        }

        static PlotInfo MakePlotInfo(
            Layout lo,
            Frame f,
            BpConfig cfg)
        {
            var ps = new PlotSettings(lo.ModelType);
            ps.CopyFrom(lo);

            PlotSettingsValidator v =
                PlotSettingsValidator.Current;

            v.SetPlotConfigurationName(
                ps,
                cfg.Printer,
                cfg.Paper);

            v.RefreshLists(ps);

            v.SetPlotWindowArea(
                ps,
                new Extents2d(
                    f.MinX,
                    f.MinY,
                    f.MaxX,
                    f.MaxY));

            v.SetPlotType(
                ps,
                Autodesk.AutoCAD.DatabaseServices.PlotType.Window);

            v.SetUseStandardScale(ps, true);
            v.SetStdScaleType(ps, StdScaleType.ScaleToFit);
            v.SetPlotCentered(ps, true);
            v.SetPlotRotation(ps, Rotation(f, cfg));
            v.SetCurrentStyleSheet(ps, cfg.PlotStyle);

            var pi = new PlotInfo
            {
                Layout = lo.ObjectId,
                OverrideSettings = ps
            };

            var piv = new PlotInfoValidator
            {
                MediaMatchingPolicy = MatchingPolicy.MatchEnabled
            };

            piv.Validate(pi);

            return pi;
        }

        static void DoPage(
            PlotEngine pe,
            PlotProgressDialog ppd,
            PlotInfo pi,
            bool last,
            int index,
            int total,
            string name)
        {
            ppd.set_PlotMsgString(
                PlotMessageIndex.Status,
                "Đang in: " + name);

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

            ppd.PlotProgressPos =
                (index + 1) * 100 / total;
        }

        /// <summary>
        /// Plots the ordered list of frames.
        /// Returns the number of completed pages and reports errors in err.
        /// </summary>
        public static int Plot(
            Document doc,
            List<Frame> frames,
            BpConfig cfg,
            out string err)
        {
            err = null;

            int total = frames.Count;

            if (total == 0)
            {
                err = "Không có khung để in.";
                return 0;
            }

            if (PlotFactory.ProcessPlotState !=
                ProcessPlotState.NotPlotting)
            {
                err = "AutoCAD đang bận in, thử lại sau.";
                return 0;
            }

            Database db = doc.Database;

            object oldBg =
                Application.GetSystemVariable("BACKGROUNDPLOT");

            Application.SetSystemVariable(
                "BACKGROUNDPLOT",
                0);

            int done = 0;

            try
            {
                string folder = OutFolder(doc, cfg);
                Directory.CreateDirectory(folder);

                string baseName = SafeFileName(
                    Path.GetFileNameWithoutExtension(doc.Name));

                // Reserve output paths before plotting so duplicate names
                // and existing files cannot cause accidental overwrites.
                var reservedPaths =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);

                var individualPdfPaths =
                    new List<string>();

                if (!cfg.MergePdf)
                {
                    for (int i = 0; i < total; i++)
                    {
                        string outputName = OutputName(
                            db,
                            doc,
                            frames[i],
                            cfg,
                            i + 1);

                        individualPdfPaths.Add(
                            UniquePdfPath(
                                folder,
                                outputName,
                                reservedPaths));
                    }
                }
                else
                {
                    // The merged PDF is also assigned a unique file path.
                    individualPdfPaths.Add(
                        UniquePdfPath(
                            folder,
                            baseName,
                            reservedPaths));
                }

                using (var tr =
                    db.TransactionManager.StartTransaction())
                {
                    var btr = (BlockTableRecord)tr.GetObject(
                        db.CurrentSpaceId,
                        OpenMode.ForRead);

                    var lo = (Layout)tr.GetObject(
                        btr.LayoutId,
                        OpenMode.ForRead);

                    using (PlotEngine pe =
                        PlotFactory.CreatePublishEngine())
                    using (var ppd =
                        new PlotProgressDialog(
                            false,
                            total,
                            true))
                    {
                        ppd.set_PlotMsgString(
                            PlotMessageIndex.DialogTitle,
                            "Batch Plot");

                        ppd.set_PlotMsgString(
                            PlotMessageIndex.CancelJobButtonMessage,
                            "Hủy toàn bộ");

                        ppd.set_PlotMsgString(
                            PlotMessageIndex.CancelSheetButtonMessage,
                            "Hủy trang này");

                        ppd.set_PlotMsgString(
                            PlotMessageIndex.SheetSetProgressCaption,
                            "Tiến độ cả bộ");

                        ppd.set_PlotMsgString(
                            PlotMessageIndex.SheetProgressCaption,
                            "Tiến độ trang");

                        ppd.LowerPlotProgressRange = 0;
                        ppd.UpperPlotProgressRange = 100;
                        ppd.PlotProgressPos = 0;

                        ppd.OnBeginPlot();
                        ppd.IsVisible = true;

                        pe.BeginPlot(ppd, null);

                        if (cfg.MergePdf)
                        {
                            // One PDF document with multiple pages.
                            string file = individualPdfPaths[0];

                            PlotInfo first = MakePlotInfo(
                                lo,
                                frames[0],
                                cfg);

                            pe.BeginDocument(
                                first,
                                doc.Name,
                                null,
                                total,
                                true,
                                file);

                            for (int i = 0; i < total; i++)
                            {
                                PlotInfo pi =
                                    i == 0
                                        ? first
                                        : MakePlotInfo(
                                            lo,
                                            frames[i],
                                            cfg);

                                DoPage(
                                    pe,
                                    ppd,
                                    pi,
                                    i == total - 1,
                                    i,
                                    total,
                                    (i + 1) + "/" + total);

                                done++;
                            }

                            pe.EndDocument(null);
                        }
                        else
                        {
                            // One PDF file for each frame.
                            for (int i = 0; i < total; i++)
                            {
                                string file =
                                    individualPdfPaths[i];

                                PlotInfo pi = MakePlotInfo(
                                    lo,
                                    frames[i],
                                    cfg);

                                pe.BeginDocument(
                                    pi,
                                    doc.Name,
                                    null,
                                    1,
                                    true,
                                    file);

                                DoPage(
                                    pe,
                                    ppd,
                                    pi,
                                    true,
                                    i,
                                    total,
                                    (i + 1) + "/" + total);

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
                Application.SetSystemVariable(
                    "BACKGROUNDPLOT",
                    oldBg);
            }

            return done;
        }
    }
}
