using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;

namespace BatchPlotBP
{
    public enum BpAction
    {
        None, Pick, Clear, Scan, Number, ClearNumber, Plot, OpenFolder
    }

    public class BpForm : Form
    {
        public BpAction Action = BpAction.None;

        readonly BpConfig cfg;
        readonly BpState st;

        ComboBox cbPrinter, cbPaper, cbStyle;
        ComboBox cbNameMode, cbNameObject, cbNameLayer;
        RadioButton rbPortrait, rbLandscape, rbAuto;
        RadioButton rbTopDown, rbLeftRight, rbPick;
        CheckBox chkMerge;

        Label lblSamples, lblCount, lblStatus;
        Label lblObject, lblLayer;
        bool loading;

        public BpForm(BpConfig c, BpState s)
        {
            cfg = c;
            st = s;

            Text = "Batch Plot BP";
            ClientSize = new Size(700, 610);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = true;
            MaximizeBox = false;
            MinimumSize = new Size(716, 649);
            Font = new Font("Segoe UI", 9f);

            Build();
            LoadAll();
        }

        static T Add<T>(
            Control parent, T c,
            int x, int y, int w, int h,
            string text = null) where T : Control
        {
            c.Location = new Point(x, y);
            c.Size = new Size(w, h);

            if (text != null)
                c.Text = text;

            parent.Controls.Add(c);
            return c;
        }

        static Label Lbl(
            Control parent, string text,
            int x, int y, int w, int h)
        {
            return Add(parent,
                new Label
                {
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleLeft
                },
                x, y, w, h, text);
        }

        static Button Btn(
            Control parent, string text,
            int x, int y, int w, int h)
        {
            return Add(parent, new Button(),
                x, y, w, h, text);
        }

        static ComboBox Combo(
            Control parent, int x, int y, int w, int h)
        {
            return Add(parent,
                new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList
                },
                x, y, w, h);
        }

        void Build()
        {
            var g1 = Add(this, new GroupBox(),
                10, 8, 680, 110, "1. Cấu hình in");

            Lbl(g1, "Máy in:", 15, 26, 70, 24);
            cbPrinter = Combo(g1, 90, 26, 260, 24);

            Lbl(g1, "Khổ giấy:", 370, 26, 70, 24);
            cbPaper = Combo(g1, 440, 26, 225, 24);

            Lbl(g1, "Nét in:", 15, 66, 70, 24);
            cbStyle = Combo(g1, 90, 66, 260, 24);

            Lbl(g1, "Hướng giấy:", 370, 66, 75, 24);

            rbPortrait = Add(g1, new RadioButton(),
                448, 66, 55, 24, "Dọc");

            rbLandscape = Add(g1, new RadioButton(),
                508, 66, 65, 24, "Ngang");

            rbAuto = Add(g1, new RadioButton(),
                578, 66, 90, 24, "Tự động");

            var g2 = Add(this, new GroupBox(),
                10, 124, 680, 112, "2. Khung in");

            lblSamples = Lbl(g2, "", 15, 20, 650, 22);

            var bPick = Btn(g2, "Chọn khung mẫu",
                15, 56, 150, 34);

            var bClear = Btn(g2, "Xóa khung mẫu",
                175, 56, 140, 34);

            var bScan = Btn(g2, "Quét chọn khung",
                325, 56, 150, 34);

            lblCount = Lbl(g2, "", 485, 56, 185, 34);

            bPick.Click += (o, e) => Finish(BpAction.Pick);
            bClear.Click += (o, e) => Finish(BpAction.Clear);
            bScan.Click += (o, e) => Finish(BpAction.Scan);

            var g3 = Add(this, new GroupBox(),
                10, 242, 680, 58, "3. Thứ tự sắp xếp");

            rbTopDown = Add(g3, new RadioButton(),
                15, 24, 230, 24, "Trên → Dưới, Trái → Phải");

            rbLeftRight = Add(g3, new RadioButton(),
                255, 24, 230, 24, "Trái → Phải, Trên → Dưới");

            rbPick = Add(g3, new RadioButton(),
                495, 24, 170, 24, "Thứ tự chọn");

            var g4 = Add(this, new GroupBox(),
                10, 306, 330, 70, "4. Kiểm tra thứ tự");

            var bNum = Btn(g4, "Đánh STT bản vẽ",
                15, 26, 150, 34);

            var bDel = Btn(g4, "Xóa STT bản vẽ",
                175, 26, 140, 34);

            bNum.Click += (o, e) => Finish(BpAction.Number);
            bDel.Click += (o, e) => Finish(BpAction.ClearNumber);

            var g5 = Add(this, new GroupBox(),
                350, 306, 340, 70, "5. Tùy chọn in");

            chkMerge = Add(g5, new CheckBox(),
                15, 30, 280, 24, "Gộp thành 1 file PDF");

            var g6 = Add(this, new GroupBox(),
                10, 384, 680, 116, "6. Quy tắc đặt tên bản vẽ");

            Lbl(g6, "Đặt tên theo:", 15, 24, 120, 24);

            cbNameMode = Combo(g6, 140, 24, 250, 24);
            cbNameMode.Items.Add("Tên file CAD + STT");
            cbNameMode.Items.Add("TEXT / MTEXT trong khung");

            lblObject = Lbl(g6, "Đối tượng lấy tên:", 15, 61, 120, 24);
            cbNameObject = Combo(g6, 140, 61, 160, 24);
            cbNameObject.Items.Add("TEXT hoặc MTEXT");
            cbNameObject.Items.Add("Chỉ TEXT");
            cbNameObject.Items.Add("Chỉ MTEXT");

            lblLayer = Lbl(g6, "Layer tên bản vẽ:", 320, 61, 115, 24);
            cbNameLayer = Combo(g6, 435, 61, 230, 24);

            cbNameMode.SelectedIndexChanged += (o, e) =>
                UpdateNameControls();

            lblStatus = Lbl(this, "",
                12, 506, 676, 42);

            var bOpen = Btn(this, "Mở Folder PDF",
                270, 558, 130, 38);

            var bInfo = Btn(this, "Thông tin",
                410, 558, 130, 38);

            var bPlot = Btn(this, "Bắt đầu in",
                550, 558, 140, 38);

            bOpen.Click += (o, e) => Finish(BpAction.OpenFolder);

            bInfo.Click += (o, e) => MessageBox.Show(
                "Batch Plot BP\n" +
                "In hàng loạt theo khung và xuất PDF.\n" +
                "Cấu hình: " + BpConfig.CfgPath,
                "Thông tin");

            bPlot.Click += (o, e) => Finish(BpAction.Plot);

            cbPrinter.SelectedIndexChanged += (o, e) =>
            {
                if (!loading)
                    LoadPapers(cbPaper.Text);
            };
        }

        void UpdateNameControls()
        {
            bool useText =
                cbNameMode.SelectedIndex == 1;

            lblObject.Visible = useText;
            cbNameObject.Visible = useText;
            lblLayer.Visible = useText;
            cbNameLayer.Visible = useText;
        }

        static void Select(ComboBox cb, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (cb.Items.Count > 0)
                    cb.SelectedIndex = 0;
                return;
            }

            int i = cb.FindStringExact(value);

            if (i < 0)
            {
                cb.Items.Insert(0, value);
                i = 0;
            }

            cb.SelectedIndex = i;
        }

        void LoadPapers(string keep)
        {
            loading = true;
            cbPaper.Items.Clear();

            try
            {
                using (var ps =
                    new PlotSettings(true))
                {
                    var v = PlotSettingsValidator.Current;
                    v.SetPlotConfigurationName(
                        ps, cbPrinter.Text, null);
                    v.RefreshLists(ps);

                    foreach (string s in
                        v.GetCanonicalMediaNameList(ps))
                    {
                        cbPaper.Items.Add(s);
                    }
                }
            }
            catch { }

            Select(cbPaper, keep);
            loading = false;
        }

        void LoadAll()
        {
            loading = true;

            try
            {
                var v = PlotSettingsValidator.Current;

                foreach (string s in v.GetPlotDeviceList())
                    cbPrinter.Items.Add(s);

                foreach (string s in v.GetPlotStyleSheetList())
                    cbStyle.Items.Add(s);
            }
            catch { }

            Select(cbPrinter, cfg.Printer);
            Select(cbStyle, cfg.PlotStyle);

            loading = false;
            LoadPapers(cfg.Paper);

            rbPortrait.Checked = cfg.Orientation == "Portrait";
            rbAuto.Checked = cfg.Orientation == "Auto";
            rbLandscape.Checked =
                !rbPortrait.Checked && !rbAuto.Checked;

            rbLeftRight.Checked = cfg.SortMode == "LeftRight";
            rbPick.Checked = cfg.SortMode == "Pick";
            rbTopDown.Checked =
                !rbLeftRight.Checked && !rbPick.Checked;

            chkMerge.Checked = cfg.MergePdf;

            Select(cbNameMode,
                cfg.NameMode == "Text"
                    ? "TEXT / MTEXT trong khung"
                    : "Tên file CAD + STT");

            Select(cbNameObject,
                cfg.NameObjectType == "Text"
                    ? "Chỉ TEXT"
                    : cfg.NameObjectType == "MText"
                        ? "Chỉ MTEXT"
                        : "TEXT hoặc MTEXT");

            LoadDrawingLayers();

            Select(cbNameLayer, cfg.NameLayer);

            UpdateNameControls();

            lblSamples.Text = cfg.Samples.Count == 0
                ? "(chưa có khung mẫu)"
                : string.Join(" | ",
                    cfg.Samples.Select(s =>
                        "Layer: " + s.Layer +
                        " [" + s.Anchor + "]").ToArray());

            lblSamples.ForeColor =
                Color.FromArgb(0, 102, 204);

            lblCount.Text =
                "→ " + st.Frames.Count + " khung sẵn sàng";

            lblStatus.Text = st.Status;
        }

        void LoadDrawingLayers()
        {
            cbNameLayer.Items.Clear();

            try
            {
                var doc =
                    Autodesk.AutoCAD.ApplicationServices.Application
                        .DocumentManager.MdiActiveDocument;

                if (doc == null)
                    return;

                using (var tr =
                    doc.Database.TransactionManager
                        .StartTransaction())
                {
                    var table = (LayerTable)tr.GetObject(
                        doc.Database.LayerTableId,
                        OpenMode.ForRead);

                    foreach (ObjectId id in table)
                    {
                        var layer = (LayerTableRecord)tr.GetObject(
                            id, OpenMode.ForRead);

                        cbNameLayer.Items.Add(layer.Name);
                    }

                    tr.Commit();
                }
            }
            catch { }
        }

        void ReadUI()
        {
            cfg.Printer = cbPrinter.Text;
            cfg.Paper = cbPaper.Text;
            cfg.PlotStyle = cbStyle.Text;

            cfg.Orientation =
                rbPortrait.Checked ? "Portrait" :
                rbAuto.Checked ? "Auto" : "Landscape";

            string mode =
                rbLeftRight.Checked ? "LeftRight" :
                rbPick.Checked ? "Pick" : "TopDown";

            if (mode != cfg.SortMode)
            {
                cfg.SortMode = mode;
                st.Frames.Clear();
                st.Status =
                    "Đã đổi thứ tự sắp xếp - hãy quét lại khung.";
            }

            cfg.MergePdf = chkMerge.Checked;

            cfg.NameMode =
                cbNameMode.SelectedIndex == 1
                    ? "Text" : "Cad";

            cfg.NameObjectType =
                cbNameObject.SelectedIndex == 1
                    ? "Text" :
                cbNameObject.SelectedIndex == 2
                    ? "MText" : "Both";

            cfg.NameLayer = cbNameLayer.Text;
        }

        void Finish(BpAction a)
        {
            ReadUI();
            cfg.Save();
            Action = a;
            Close();
        }

        protected override void OnFormClosing(
            FormClosingEventArgs e)
        {
            ReadUI();
            cfg.Save();
            base.OnFormClosing(e);
        }
    }
}
