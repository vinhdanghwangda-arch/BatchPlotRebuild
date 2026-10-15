using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace BatchPlotBP
{
    public class SampleDef
    {
        public string Layer;
        public string Anchor; // TopLeft | TopRight | BottomLeft | BottomRight
    }

    /// <summary>Doc/ghi BatchPlot.cfg cung dinh dang voi ban goc (nam canh file DLL).</summary>
    public class BpConfig
    {
        public string Printer = "AutoCAD PDF (High Quality Print).pc3";
        public string Paper = "ISO_expand_A4_(297.00_x_210.00_MM)";
        public string PlotStyle = "acad.ctb";
        public string Orientation = "Landscape"; // Landscape | Portrait | Auto
        public bool MergePdf = false;
        public bool Collate = false;
        public string SortMode = "TopDown";      // TopDown | LeftRight | Pick
        public List<SampleDef> Samples = new List<SampleDef>();

        public static string CfgPath
        {
            get
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(dir, "BatchPlot.cfg");
            }
        }

        static bool B(string v) { return string.Equals(v, "True", StringComparison.OrdinalIgnoreCase); }

        public static BpConfig Load()
        {
            var c = new BpConfig();
            try
            {
                if (!File.Exists(CfgPath)) return c;
                foreach (string raw in File.ReadAllLines(CfgPath))
                {
                    int p = raw.IndexOf('=');
                    if (p <= 0) continue;
                    string k = raw.Substring(0, p).Trim();
                    string v = raw.Substring(p + 1).Trim();
                    switch (k)
                    {
                        case "Printer": c.Printer = v; break;
                        case "Paper": c.Paper = v; break;
                        case "PlotStyle": c.PlotStyle = v; break;
                        case "Orientation": c.Orientation = v; break;
                        case "MergePdf": c.MergePdf = B(v); break;
                        case "Collate": c.Collate = B(v); break;
                        case "SortMode": c.SortMode = v; break;
                        case "Sample":
                            string[] a = v.Split('|');
                            if (a.Length == 3) c.Samples.Add(new SampleDef { Layer = a[1], Anchor = a[2] });
                            break;
                    }
                }
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                var lines = new List<string>
                {
                    "Printer=" + Printer,
                    "Paper=" + Paper,
                    "PlotStyle=" + PlotStyle,
                    "Orientation=" + Orientation,
                    "MergePdf=" + (MergePdf ? "True" : "False"),
                    "Collate=" + (Collate ? "True" : "False"),
                    "SortMode=" + SortMode
                };
                foreach (var s in Samples) lines.Add("Sample=LAYER|" + s.Layer + "|" + s.Anchor);
                File.WriteAllLines(CfgPath, lines.ToArray());
            }
            catch { }
        }
    }

    public class BpState
    {
        public List<Frame> Frames = new List<Frame>();
        public string Status = "";
    }
}
