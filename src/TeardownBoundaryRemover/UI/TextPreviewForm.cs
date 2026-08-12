namespace TeardownBoundaryRemover.UI;

internal sealed class TextPreviewForm : Form
{
    public TextPreviewForm(ModItem item)
    {
        Text = Loc.T("XML 预览 - ", "XML Preview - ") + item.Name;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;
        MinimumSize = new Size(500, 340);
        Size = new Size(1000, 720);

        var selector = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill
        };
        foreach (var xml in item.XmlFiles.Where(x => x.IsLevelLike))
            selector.Items.Add(xml);
        selector.DisplayMember = nameof(XmlFileEntry.RelativePath);

        var viewer = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            DetectUrls = false,
            Font = new Font(FontFamily.GenericMonospace, Font.Size)
        };

        var info = new Label
        {
            AutoSize = true,
            Text = Loc.T("只读预览。程序实际执行时会再次解析和校验该 XML。", "Read-only preview. The application will parse and validate this XML again before making changes.")
        };

        void LoadSelected()
        {
            if (selector.SelectedItem is not XmlFileEntry xml)
                return;
            try
            {
                viewer.Text = File.ReadAllText(xml.Path);
                HighlightBoundary(viewer);
            }
            catch (Exception ex)
            {
                viewer.Text = Loc.T("无法读取文件：\r\n", "Unable to read file:\r\n") + ex.Message;
            }
        }

        selector.SelectedIndexChanged += (_, _) => LoadSelected();

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.Controls.Add(new Label { AutoSize = true, Text = Loc.T("文件：", "File:"), Anchor = AnchorStyles.Left }, 0, 0);
        top.Controls.Add(selector, 1, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(top, 0, 0);
        layout.Controls.Add(viewer, 0, 1);
        layout.Controls.Add(info, 0, 2);
        Controls.Add(layout);

        if (selector.Items.Count > 0)
            selector.SelectedIndex = 0;
        Shown += (_, _) => WindowSizing.FitToWorkingArea(this, 0.84, 0.84, 560, 400);
    }

    private static void HighlightBoundary(RichTextBox viewer)
    {
        var start = 0;
        while (start < viewer.TextLength)
        {
            var index = viewer.Find("boundary", start, RichTextBoxFinds.None);
            if (index < 0)
                break;
            viewer.Select(index, "boundary".Length);
            viewer.SelectionBackColor = SystemColors.Info;
            viewer.SelectionColor = SystemColors.InfoText;
            start = index + "boundary".Length;
        }
        viewer.Select(0, 0);
    }
}
