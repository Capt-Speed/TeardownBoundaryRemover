namespace TeardownBoundaryRemover.UI;

internal sealed class ConfirmDialog : Form
{
    private readonly CheckBox _reviewed = new() { AutoSize = true, Text = Loc.T("我已核对上面的地图和 XML 数量", "I have reviewed the map and XML counts above") };
    private readonly CheckBox _dangerous = new() { AutoSize = true };
    private readonly Button _continue = new() { AutoSize = true, Text = Loc.T("创建备份", "Create backup") };

    public ConfirmDialog(OperationSummary summary, string detailText)
    {
        Text = Loc.T("确认处理 Boundary", "Confirm Boundary processing");
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;
        MinimizeBox = false;
        MaximizeBox = true;
        ShowInTaskbar = false;
        MinimumSize = new Size(460, 320);
        Size = new Size(760, 560);

        var title = new Label
        {
            AutoSize = true,
            Text = Loc.T($"将处理 {summary.ItemCount} 个项目、{summary.XmlCount} 个 XML，共 {summary.BoundaryCount} 个 Boundary。", $"This will process {summary.ItemCount} items, {summary.XmlCount} XML files, and {summary.BoundaryCount} Boundary elements."),
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        };

        var explanation = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            Text = Loc.T("程序不会删除 XML 文件。它只会从确认的 Teardown 关卡 XML 树中移除 boundary 元素；所有待修改 XML 会先完整备份并校验哈希。", "The application will not delete XML files. It only removes boundary elements from confirmed Teardown level XML trees. Every XML file will be fully backed up and hash-verified first."),
            Margin = new Padding(0, 0, 0, 8)
        };

        var details = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Text = detailText
        };

        var checks = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        checks.Controls.Add(_reviewed);

        if (summary.IncludesWorkshop || summary.IncludesBuiltIn)
        {
            var sourceText = summary.IncludesWorkshop && summary.IncludesBuiltIn
                ? Loc.T("我理解本次包含 Workshop 和游戏自带文件；Steam 更新/验证可能覆盖这些修改", "I understand this includes Workshop and built-in files; Steam updates or verification may overwrite these changes")
                : summary.IncludesBuiltIn
                    ? Loc.T("我理解本次包含游戏自带文件；Steam 更新/验证可能覆盖这些修改", "I understand this includes built-in files; Steam updates or verification may overwrite these changes")
                    : Loc.T("我理解本次包含 Workshop 文件；Workshop 更新可能覆盖这些修改", "I understand this includes Workshop files; Workshop updates may overwrite these changes");
            _dangerous.Text = sourceText;
            checks.Controls.Add(_dangerous);
        }
        else
        {
            _dangerous.Checked = true;
            _dangerous.Visible = false;
        }

        _reviewed.CheckedChanged += (_, _) => UpdateContinueState();
        _dangerous.CheckedChanged += (_, _) => UpdateContinueState();
        _continue.Enabled = false;
        _continue.DialogResult = DialogResult.OK;

        var cancel = new Button { AutoSize = true, Text = Loc.T("取消", "Cancel"), DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        buttons.Controls.Add(_continue);
        buttons.Controls.Add(cancel);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(explanation, 0, 1);
        layout.Controls.Add(details, 0, 2);
        layout.Controls.Add(checks, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);

        AcceptButton = _continue;
        CancelButton = cancel;
        Shown += (_, _) => WindowSizing.FitToWorkingArea(this, 0.72, 0.72, 520, 380);
    }

    private void UpdateContinueState()
        => _continue.Enabled = _reviewed.Checked && _dangerous.Checked;
}
