using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPlotBP
{
    public enum BpAction
    {
        None,
        Pick,
        Clear,
        Scan,
        Number,
        ClearNumber,
        Plot,
        OpenFolder
    }

    public class BpForm : Form
    {
        public BpAction Action = BpAction.None;
        public event Action<BpAction> ActionRequested;

        private readonly BpConfig cfg;
        private readonly BpState st;

        private ComboBox cbPrinter;
        private ComboBox cbPaper;
        private ComboBox cbStyle;
        private ComboBox cbNameMode;
        private ComboBox cbNameObject;
        private ComboBox cbNameLayer;

        private RadioButton rbPortrait;
        private RadioButton rbLandscape;
        private RadioButton rbAuto;
        private RadioButton rbTopDown;
        private RadioButton rbLeftRight;
        private RadioButton rbPick;

        private CheckBox chkMerge;

        private Label lblSamples;
        private Label lblCount;
        private Label lblStatus;

        private Button bOpen;
        private Button bPick;
        private Button bClear;
        private Button bScan;
        private Button bNum;
        private Button bDel;
        private Button bPlot;

        private bool loading;

        public BpForm(BpConfig c, BpState s)
        {
            cfg = c;
            st = s;

            Text = "Batch Plot BP";
            ClientSize = new Size(700, 570);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = true;
            ShowInTaskbar = true;
            Font = new System.Drawing.Font("Segoe UI", 9f);

            Build();
            LoadAll();
        }

        private static T Add<T>(Control parent, T control, int x, int y, int w, int h, string text = null)
            where T : Control
        {
            control.Location = new Point(x, y);
            control.Size = new Size(w, h);
            if (text != null) control.Text = text;
            parent.Controls.Add(control);
            return control;
        }

        private static Label Lbl(Control parent, string text, int x, int y, int w, int h)
        {
            var label = new Label
            {
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };
            return Add(parent, label, x, y, w, h, text);
        }

        private static Button Btn(Control parent, string text, int x, int y, int w, int h)
        {
            return Add(parent, new Button(), x, y, w, h, text);
        }

        private void Build()
        {
            // 1. Printing configuration
            var g1 = Add(this, new GroupBox(), 10, 8, 680, 110, "1. Cấu hình in");
            Lbl(g1, "Máy in:", 15, 26, 70, 24);
            cbPrinter = Add(g1, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, 90, 26, 260, 24);
            Lbl(g1, "Khổ giấy:", 370, 26, 70, 24);
            cbPaper = Add(g1, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, 440, 26, 225, 24);
            Lbl(g1, "Nét in:", 15, 66, 70, 24);
            cbStyle = Add(g1, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, 90, 66, 260, 24);
            Lbl(g1, "Hướng giấy:", 370, 66, 75, 24);
            rbPortrait = Add(g1, new RadioButton(), 448, 66, 55, 24, "Dọc");
            rbLandscape = Add(g1, new RadioButton(), 508, 66, 65, 24, "Ngang");
            rbAuto = Add(g1, new RadioButton(), 578, 66, 90, 24, "Tự động");

            // 2. Drawing frames
            var g2 = Add(this, new GroupBox(), 10, 124, 680, 112, "2. Khung in");
            lblSamples = Lbl(g2, "", 15, 20, 650, 22);
            bPick = Btn(g2, "Chọn khung mẫu", 15, 56, 150, 34);
            bClear = Btn(g2, "Xóa khung mẫu", 175, 56, 140, 34);
            bScan = Btn(g2, "Quét chọn khung", 325, 56, 150, 34);
            lblCount = Lbl(g2, "", 485, 56, 185, 34);
            bPick.Click += (sender, args) => Finish(BpAction.Pick);
            bClear.Click += (sender, args) => Finish(BpAction.Clear);
            bScan.Click += (sender, args) => Finish(BpAction.Scan);

            // 3. Sorting order. This setting controls sorting only, not scan options.
            var g3 = Add(this, new GroupBox(), 10, 242, 680, 58, "3. Thứ tự sắp xếp");
            rbTopDown = Add(g3, new RadioButton(), 15, 24, 230, 24, "Trên → Dưới, Trái → Phải");
            rbLeftRight = Add(g3, new RadioButton(), 255, 24, 230, 24, "Trái → Phải, Trên → Dưới");
            rbPick = Add(g3, new RadioButton(), 495, 24, 170, 24, "Thứ tự chọn");

            // 4. Numbering
            var g4 = Add(this, new GroupBox(), 10, 306, 330, 70, "4. Kiểm tra thứ tự");
            bNum = Btn(g4, "Đánh STT bản vẽ", 15, 26, 150, 34);
            bDel = Btn(g4, "Xóa STT bản vẽ", 175, 26, 140, 34);
            bNum.Click += (sender, args) => Finish(BpAction.Number);
            bDel.Click += (sender, args) => Finish(BpAction.ClearNumber);

            // 5. Printing options
            var g5 = Add(this, new GroupBox(), 350, 306, 340, 70, "5. Tùy chọn in");
            chkMerge = Add(g5, new CheckBox(), 15, 30, 250, 24, "Gộp thành 1 file PDF");

            // 6. PDF naming rules
            var g6 = Add(this, new GroupBox(), 10, 382, 680, 66, "6. Quy tắc đặt tên bản vẽ");
            Lbl(g6, "Đặt tên theo:", 12, 25, 90, 24);
            cbNameMode = Add(g6, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, 105, 25, 180, 24);
            cbNameMode.Items.AddRange(new object[] { "Tên file CAD + STT", "TEXT / MTEXT trong khung", "Tuy chon" });
            Lbl(g6, "Đối tượng:", 300, 25, 70, 24);
            cbNameObject = Add(g6, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, 370, 25, 125, 24);
            cbNameObject.Items.AddRange(new object[] { "TEXT hoặc MTEXT", "Chỉ TEXT", "Chỉ MTEXT" });
            Lbl(g6, "Layer:", 500, 25, 45, 24);
            cbNameLayer = Add(g6, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, 545, 25, 125, 24);
            cbNameMode.SelectedIndexChanged += (sender, args) => UpdateNameControls();
            cbNameObject.SelectedIndex = 0;

            LoadDrawingLayers();
            UpdateNameControls();

            // Status and bottom buttons do not overlap.
            lblStatus = Lbl(this, "", 12, 452, 676, 30);
            bOpen = Btn(this, "Mở Folder PDF", 270, 490, 130, 38);
            var bInfo = Btn(this, "Thông tin", 410, 490, 130, 38);
            bPlot = Btn(this, "Bắt đầu in", 550, 490, 140, 38);
            bOpen.BringToFront();
            bInfo.BringToFront();
            bPlot.BringToFront();

            bOpen.Click += (sender, args) => Finish(BpAction.OpenFolder);
            bInfo.Click += (sender, args) => MessageBox.Show(
                this,
                "Batch Plot BP\n" +
                "In hàng loạt theo khung, xuất PDF vào thư mục đã chọn.\n\n" +
                "Cấu hình: " + BpConfig.CfgPath,
                "Thông tin",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            bPlot.Click += (sender, args) => Finish(BpAction.Plot);

            cbPrinter.SelectedIndexChanged += (sender, args) =>
            {
                if (!loading) LoadPapers(cbPaper.Text);
            };
        }

        private static string AnchorVi(string anchor)
        {
            switch (anchor)
            {
                case "TopLeft": return "Trên-Trái";
                case "TopRight": return "Trên-Phải";
                case "BottomLeft": return "Dưới-Trái";
                case "BottomRight": return "Dưới-Phải";
                default: return anchor;
            }
        }

        private static void Select(ComboBox combo, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (combo.Items.Count > 0) combo.SelectedIndex = 0;
                return;
            }

            int index = combo.FindStringExact(value);
            if (index < 0)
            {
                combo.Items.Insert(0, value);
                index = 0;
            }
            combo.SelectedIndex = index;
        }

        private void LoadDrawingLayers()
        {
            cbNameLayer.Items.Clear();
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            try
            {
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    LayerTable layerTable = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                    foreach (ObjectId id in layerTable)
                    {
                        LayerTableRecord layer = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        cbNameLayer.Items.Add(layer.Name);
                    }
                    tr.Commit();
                }
            }
            catch
            {
                // Keep the interface usable if layer loading fails.
            }

            if (cbNameLayer.Items.Count > 0) cbNameLayer.SelectedIndex = 0;
        }

        private void LoadPapers(string keep)
        {
            loading = true;
            cbPaper.Items.Clear();

            try
            {
                using (var ps = new PlotSettings(true))
                {
                    PlotSettingsValidator validator = PlotSettingsValidator.Current;
                    validator.SetPlotConfigurationName(ps, cbPrinter.Text, null);
                    validator.RefreshLists(ps);
                    foreach (string paper in validator.GetCanonicalMediaNameList(ps))
                        cbPaper.Items.Add(paper);
                }
            }
            catch
            {
                // Printer or paper configuration may be unavailable.
            }

            int index = cbPaper.FindStringExact(keep ?? "");
            if (index >= 0) cbPaper.SelectedIndex = index;
            else if (cbPaper.Items.Count > 0) cbPaper.SelectedIndex = 0;
            loading = false;
        }

        private void LoadAll()
        {
            loading = true;
            cbPrinter.Items.Clear();
            cbStyle.Items.Clear();

            try
            {
                PlotSettingsValidator validator = PlotSettingsValidator.Current;
                foreach (string printer in validator.GetPlotDeviceList()) cbPrinter.Items.Add(printer);
                foreach (string style in validator.GetPlotStyleSheetList()) cbStyle.Items.Add(style);
            }
            catch
            {
                // Preserve saved settings if lists are unavailable.
            }

            Select(cbPrinter, cfg.Printer);
            Select(cbStyle, cfg.PlotStyle);
            loading = false;
            LoadPapers(cfg.Paper);

            rbPortrait.Checked = cfg.Orientation == "Portrait";
            rbAuto.Checked = cfg.Orientation == "Auto";
            rbLandscape.Checked = !rbPortrait.Checked && !rbAuto.Checked;

            rbLeftRight.Checked = cfg.SortMode == "LeftRight";
            rbPick.Checked = cfg.SortMode == "Pick";
            rbTopDown.Checked = !rbLeftRight.Checked && !rbPick.Checked;

            chkMerge.Checked = cfg.MergePdf;
            cbNameMode.SelectedIndex = cfg.NameMode == "Text" ? 1 : cfg.NameMode == "Custom" ? 2 : 0;
            cbNameObject.SelectedIndex = cfg.NameObjectType == "Text" ? 1 : cfg.NameObjectType == "MText" ? 2 : 0;

            if (!string.IsNullOrWhiteSpace(cfg.NameLayer)) Select(cbNameLayer, cfg.NameLayer);
            RefreshFromState();
        }

        private void ReadUI()
        {
            cfg.Printer = cbPrinter.Text;
            cfg.Paper = cbPaper.Text;
            cfg.PlotStyle = cbStyle.Text;
            cfg.Orientation = rbPortrait.Checked ? "Portrait" : rbAuto.Checked ? "Auto" : "Landscape";

            string mode = rbLeftRight.Checked ? "LeftRight" : rbPick.Checked ? "Pick" : "TopDown";
            if (mode != cfg.SortMode)
            {
                cfg.SortMode = mode;
                st.Frames.Clear();
                st.Status = "Đã đổi thứ tự sắp xếp - hãy quét lại khung.";
            }

            cfg.MergePdf = chkMerge.Checked;
            cfg.NameMode = cbNameMode.SelectedIndex == 1 ? "Text" : cbNameMode.SelectedIndex == 2 ? "Custom" : "Cad";
            cfg.NameObjectType = cbNameObject.SelectedIndex == 1 ? "Text" : cbNameObject.SelectedIndex == 2 ? "MText" : "Both";
            cfg.NameLayer = cbNameLayer.Text;
        }

        private void UpdateNameControls()
        {
            bool useText = cbNameMode.SelectedIndex == 1;
            bool custom = cbNameMode.SelectedIndex == 2;
            cbNameObject.Enabled = useText;
            cbNameLayer.Enabled = useText;
            // In custom mode, the user enters each PDF file name in a Save dialog.
            cbNameObject.Enabled = useText && !custom;
            cbNameLayer.Enabled = useText && !custom;
            chkMerge.Enabled = !custom;
        }

        private void Finish(BpAction action)
        {
            if (loading) return;

            ReadUI();
            cfg.Save();
            Action = action;

            // Hide the modeless form before requesting AutoCAD command-context work.
            Hide();
            ActionRequested?.Invoke(action);
        }

        public void RefreshFromState()
        {
            if (IsDisposed) return;

            lblSamples.Text = cfg.Samples.Count == 0
                ? "(chưa có khung mẫu)"
                : string.Join(" | ", cfg.Samples.Select(s => "Layer: " + s.Layer + " [" + AnchorVi(s.Anchor) + "]").ToArray());
            lblSamples.ForeColor = Color.FromArgb(0, 102, 204);
            lblCount.Text = "→ " + st.Frames.Count + " khung sẵn sàng";
            lblStatus.Text = st.Status;
            bOpen.Enabled = !string.IsNullOrWhiteSpace(st.OutputFolder) && System.IO.Directory.Exists(st.OutputFolder);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                ReadUI();
                cfg.Save();
            }
            catch
            {
                // Allow the user to close the window.
            }
            base.OnFormClosing(e);
        }
    }
}
