using System.Diagnostics;
using TeardownBoundaryRemover.Services;

namespace TeardownBoundaryRemover.UI;

internal sealed partial class MainForm : Form
{
    private readonly TeardownScanner _scanner = new();
    private readonly AppSettings _settings = SettingsService.Load();
    private readonly SmoothDataGridView _grid = new();
    private readonly CheckBox _selectAll = new() { AutoSize = true, ThreeState = true, AutoCheck = false, Text = Loc.T("全选可处理项目", "Select all eligible items") };
    private readonly TextBox _search = new() { PlaceholderText = Loc.T("筛选地图 / Mod 名称…", "Filter map / mod name…") };
    private readonly CheckBox _onlyBoundary = new() { AutoSize = true, Text = Loc.T("只显示含边界", "Only show items with boundary") };
    private readonly ComboBox _sourceFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _scanBuiltInMaps = new() { AutoSize = true, Text = Loc.T("游戏原生地图", "Built-in maps") };
    private readonly CheckBox _scanWorkshopMaps = new() { AutoSize = true, Text = Loc.T("创意工坊地图", "Workshop maps") };
    private readonly CheckBox _scanLocalMaps = new() { AutoSize = true, Text = Loc.T("本地地图", "Local maps") };
    private readonly TextBox _details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = false, AutoEllipsis = true, Text = Loc.T("请选择地图类型，然后点击“扫描”。", "Select map types, then click Scan.") };
    private readonly Label _selectionStatus = new() { AutoSize = true, Text = Loc.T("未选择项目", "No items selected") };
    private readonly Button _removeButton = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(230, 48),
        Padding = new Padding(22, 8, 22, 8),
        Text = Loc.T("移除 XML 边界", "Remove XML Boundary"),
        Enabled = false
    };
    private readonly Button _binaryButton = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(190, 48),
        Padding = new Padding(18, 8, 18, 8),
        Text = Loc.T("危险：修改 BIN", "DANGER: Modify BIN"),
        BackColor = Color.Firebrick,
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold),
        Visible = false
    };
    private readonly Button _rescanButton = new() { AutoSize = true, Text = Loc.T("扫描", "Scan") };
    private readonly Button _addLocationButton = new() { AutoSize = true, Text = Loc.T("添加位置…", "Add location…") };
    private readonly Button _openFolderButton = new() { AutoSize = true, Text = Loc.T("打开所在文件夹", "Open containing folder"), Enabled = false };
    private readonly Button _previewButton = new() { AutoSize = true, Text = Loc.T("预览 XML", "Preview XML"), Enabled = false };
    private readonly Button _restoreButton = new() { AutoSize = true, Text = Loc.T("恢复最近一次备份…", "Restore latest backup…") };
    private readonly Button _backupFolderButton = new() { AutoSize = true, Text = Loc.T("打开备份目录", "Open backup folder") };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, MarqueeAnimationSpeed = 25, Width = 120 };
    private readonly ToolTip _toolTip = new() { ShowAlways = true, AutoPopDelay = 30000 };
    private SplitContainer? _contentSplit;
    private TableLayoutPanel? _rootLayout;

    private readonly List<ModItem> _items = [];
    private bool _updatingGrid;
    private bool _hasScanned;
    private CancellationTokenSource? _scanCts;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 100 };
    private string? _pendingStatus;
    private int _dragStartRow = -1;
    private int _dragLastRow = -1;
    private Point _dragStartPoint;
    private bool _dragSelecting;

    public MainForm()
    {
        Text = "Teardown Boundary Remover";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;
        MinimumSize = new Size(480, 320);
        KeyPreview = true;

        ConfigureInitialSize();
        ConfigureGrid();
        BuildLayout();
        WireEvents();

        Shown += (_, _) => { FitToWorkingArea(); AdjustSplitter(); };
        ResizeBegin += (_, _) => BeginInteractiveMoveOrResize();
        ResizeEnd += (_, _) => EndInteractiveMoveOrResize();
        FormClosing += (_, _) => _scanCts?.Cancel();
        _statusTimer.Tick += (_, _) => FlushPendingStatus();
    }

    private void ConfigureInitialSize()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var width = Math.Clamp((int)(area.Width * 0.88), 800, 1380);
        var height = Math.Clamp((int)(area.Height * 0.88), 560, 900);
        Size = new Size(width, height);
    }

    private void FitToWorkingArea()
    {
        WindowSizing.FitToWorkingArea(this, 0.88, 0.88, 640, 480);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.AutoGenerateColumns = false;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.Fixed3D;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;

        var selectColumn = new DataGridViewCheckBoxColumn
        {
            Name = "Selected",
            HeaderText = Loc.T("选择", "Select"),
            Width = 58,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            ThreeState = false
        };
        _grid.Columns.Add(selectColumn);
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name",
            HeaderText = Loc.T("地图 / Mod 名称", "Map / Mod name"),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 42,
            ReadOnly = true
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Source",
            HeaderText = Loc.T("来源", "Source"),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 25,
            MinimumWidth = 130,
            ReadOnly = true
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "XmlCount",
            HeaderText = "XML",
            Width = 58,
            ReadOnly = true
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "BoundaryCount",
            HeaderText = Loc.T("边界", "Boundary"),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Width = Loc.IsChinese ? 66 : 82,
            MinimumWidth = 60,
            ReadOnly = true
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Status",
            HeaderText = Loc.T("状态", "Status"),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 28,
            ReadOnly = true
        });
    }

    private void BuildLayout()
    {
        _sourceFilter.Items.AddRange([
            Loc.T("全部来源", "All sources"),
            Loc.T("本地 Mod", "Local Mod"),
            Loc.T("创意工坊", "Workshop"),
            Loc.T("游戏自带", "Built-in"),
            Loc.T("自定义位置", "Custom location")]);
        _sourceFilter.SelectedIndex = 0;
        _scanBuiltInMaps.Checked = _settings.ScanBuiltInMaps;
        _scanWorkshopMaps.Checked = _settings.ScanWorkshopMaps;
        _scanLocalMaps.Checked = _settings.ScanLocalMaps;

        var mapSelector = new GroupBox
        {
            Text = Loc.T("扫描地图类型（可多选）", "Map types to scan (select any)"),
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 7),
            Margin = new Padding(0, 0, 0, 6)
        };
        var mapSelectorFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
            Padding = new Padding(0, 2, 0, 0)
        };
        _scanBuiltInMaps.Margin = new Padding(0, 3, 24, 3);
        _scanWorkshopMaps.Margin = new Padding(0, 3, 24, 3);
        _scanLocalMaps.Margin = new Padding(0, 3, 8, 3);
        mapSelectorFlow.Controls.Add(_scanBuiltInMaps);
        mapSelectorFlow.Controls.Add(_scanWorkshopMaps);
        mapSelectorFlow.Controls.Add(_scanLocalMaps);
        mapSelector.Controls.Add(mapSelectorFlow);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 0, 0, 4)
        };
        toolbar.Controls.Add(_rescanButton);
        toolbar.Controls.Add(_addLocationButton);
        toolbar.Controls.Add(_openFolderButton);
        toolbar.Controls.Add(_previewButton);
        toolbar.Controls.Add(_restoreButton);
        toolbar.Controls.Add(_backupFolderButton);

        var filters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 5,
            RowCount = 1,
            Margin = new Padding(0)
        };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filters.Controls.Add(_selectAll, 0, 0);
        filters.Controls.Add(_search, 1, 0);
        filters.Controls.Add(new Label { Text = Loc.T("来源：", "Source:"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 5, 2, 0) }, 2, 0);
        filters.Controls.Add(_sourceFilter, 3, 0);
        filters.Controls.Add(_onlyBoundary, 4, 0);
        _search.Dock = DockStyle.Fill;
        _search.Margin = new Padding(10, 0, 10, 0);
        _sourceFilter.Margin = new Padding(0, 0, 10, 0);

        var detailGroup = new GroupBox { Text = Loc.T("项目详情", "Item details"), Dock = DockStyle.Fill, Padding = new Padding(8) };
        detailGroup.Controls.Add(_details);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel2,
            Panel1MinSize = 180,
            Panel2MinSize = 130,
            SplitterWidth = 6
        };
        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(detailGroup);
        _contentSplit = split;

        var operationBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 1
        };
        operationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        operationBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        operationBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        operationBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        operationBar.Controls.Add(_selectionStatus, 0, 0);
        operationBar.Controls.Add(_binaryButton, 1, 0);
        operationBar.Controls.Add(_progress, 2, 0);
        operationBar.Controls.Add(_removeButton, 3, 0);
        _selectionStatus.Anchor = AnchorStyles.Left;
        _binaryButton.Anchor = AnchorStyles.Left;
        _progress.Anchor = AnchorStyles.Right;
        _removeButton.Anchor = AnchorStyles.Right;

        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 1,
            Padding = new Padding(0, 4, 0, 0)
        };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(38, Font.Height * 2 + 8)));
        _status.Dock = DockStyle.Fill;
        _status.Margin = new Padding(0);
        statusPanel.Controls.Add(_status, 0, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(10),
            ColumnCount = 1,
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(mapSelector, 0, 0);
        root.Controls.Add(toolbar, 0, 1);
        root.Controls.Add(filters, 0, 2);
        root.Controls.Add(split, 0, 3);
        root.Controls.Add(operationBar, 0, 4);
        root.Controls.Add(statusPanel, 0, 5);
        _rootLayout = root;
        Controls.Add(root);

        _status.TextChanged += (_, _) =>
        {
            _toolTip.SetToolTip(_status, _status.Text);
        };

        _removeButton.Paint += (_, e) =>
        {
            var inset = Math.Max(3, DeviceDpi / 32);
            var rectangle = new Rectangle(inset, inset, _removeButton.ClientSize.Width - inset * 2 - 1, _removeButton.ClientSize.Height - inset * 2 - 1);
            if (rectangle.Width > 0 && rectangle.Height > 0)
            {
                using var pen = new Pen(_removeButton.Enabled ? Color.Firebrick : Color.FromArgb(170, 120, 120), 1f);
                e.Graphics.DrawRectangle(pen, rectangle);
            }
        };
    }

    private void WireEvents()
    {
        _rescanButton.Click += async (_, _) => await ScanAsync();
        _addLocationButton.Click += async (_, _) => await AddLocationAsync();
        _selectAll.Click += (_, _) => ToggleSelectAll();
        _search.TextChanged += (_, _) => RebuildGrid();
        _onlyBoundary.CheckedChanged += (_, _) => RebuildGrid();
        _sourceFilter.SelectedIndexChanged += (_, _) => RebuildGrid();
        _scanBuiltInMaps.CheckedChanged += (_, _) => ScanSourceChanged();
        _scanWorkshopMaps.CheckedChanged += (_, _) => ScanSourceChanged();
        _scanLocalMaps.CheckedChanged += (_, _) => ScanSourceChanged();
        _grid.SelectionChanged += (_, _) =>
        {
            if (!_dragSelecting)
                UpdateDetailsAndButtons();
        };
        _grid.CellMouseDown += GridCellMouseDown;
        _grid.MouseMove += GridMouseMove;
        _grid.MouseUp += GridMouseUp;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_updatingGrid || e.RowIndex < 0 || e.ColumnIndex != _grid.Columns["Selected"].Index)
                return;
            if (_grid.Rows[e.RowIndex].Tag is not ModItem item || !item.IsSelectable)
                return;
            item.Selected = _grid.Rows[e.RowIndex].Cells["Selected"].Value is true;
            if (item.Selected)
            {
                if (item.IsBinarySelectable)
                {
                    foreach (var other in _items.Where(x => x.Id != item.Id)) other.Selected = false;
                    RebuildGrid();
                    return;
                }
                foreach (var binary in _items.Where(x => x.IsBinarySelectable)) binary.Selected = false;
            }
            UpdateSelectionState();
        };
        _grid.CellBeginEdit += (_, e) =>
        {
            if (e.ColumnIndex != _grid.Columns["Selected"].Index)
                return;
            if (_grid.Rows[e.RowIndex].Tag is not ModItem item || !item.IsSelectable)
                e.Cancel = true;
        };
        _openFolderButton.Click += (_, _) => OpenSelectedFolder();
        _previewButton.Click += (_, _) => PreviewSelected();
        _removeButton.Click += async (_, _) => await RemoveSelectedAsync();
        _binaryButton.Click += async (_, _) => await ModifySelectedBinaryAsync();
        _restoreButton.Click += async (_, _) => await RestoreLatestAsync();
        _backupFolderButton.Click += (_, _) => OpenBackupFolder();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5)
            {
                e.Handled = true;
                _ = ScanAsync();
            }
        };
    }

    private void RebuildGrid()
    {
        if (IsDisposed)
            return;

        var selectedItemId = GetCurrentItem()?.Id;
        var selectedColumnIndex = _grid.CurrentCell?.ColumnIndex ?? 1;
        var previousFirstRowIndex = _grid.FirstDisplayedScrollingRowIndex;
        var firstDisplayedItemId = previousFirstRowIndex >= 0 && previousFirstRowIndex < _grid.RowCount
            ? (_grid.Rows[previousFirstRowIndex].Tag as ModItem)?.Id
            : null;
        _updatingGrid = true;
        _grid.BeginBulkUpdate();
        try
        {
            _grid.Rows.Clear();
            foreach (var item in FilteredItems())
            {
                var rowIndex = _grid.Rows.Add(
                    item.Selected,
                    item.Name,
                    item.SourceLabel,
                    item.LevelXmlCount,
                    item.DisplayBoundaryCount,
                    item.Status);
                var row = _grid.Rows[rowIndex];
                row.Tag = item;
                if (!item.IsSelectable)
                {
                    row.Cells["Selected"].ReadOnly = true;
                    row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
                }
                else if (item.SourceType == ContentSourceType.BuiltIn)
                {
                    row.Cells["Status"].ToolTipText = Loc.T("游戏自带内容。执行前会要求额外确认，并且 Steam 更新/验证可能恢复原文件。", "Built-in content. An extra confirmation is required, and a Steam update or file verification may restore the original file.");
                }

                if (selectedItemId == item.Id)
                {
                    row.Selected = true;
                    _grid.CurrentCell = row.Cells[Math.Clamp(selectedColumnIndex, 0, _grid.ColumnCount - 1)];
                }
            }
        }
        finally
        {
            _updatingGrid = false;
            _grid.EndBulkUpdate();
        }
        RestoreGridScrollPosition(firstDisplayedItemId, previousFirstRowIndex);
        UpdateSelectionState();
        UpdateDetailsAndButtons();
    }

    private void RestoreGridScrollPosition(Guid? firstDisplayedItemId, int previousFirstRowIndex)
    {
        if (_grid.RowCount == 0 || previousFirstRowIndex < 0)
            return;

        var targetRow = firstDisplayedItemId is null
            ? -1
            : _grid.Rows.Cast<DataGridViewRow>()
                .FirstOrDefault(row => (row.Tag as ModItem)?.Id == firstDisplayedItemId.Value)?.Index ?? -1;
        if (targetRow < 0)
            targetRow = Math.Clamp(previousFirstRowIndex, 0, _grid.RowCount - 1);

        try
        {
            _grid.FirstDisplayedScrollingRowIndex = targetRow;
        }
        catch (InvalidOperationException)
        {
            // A filter can remove the former first row while the grid is being rebuilt.
        }
    }

    private IEnumerable<ModItem> FilteredItems()
    {
        var query = _search.Text.Trim();
        foreach (var item in _items)
        {
            if (_onlyBoundary.Checked && item.DisplayBoundaryCount == 0)
                continue;
            if (!MatchesSourceFilter(item))
                continue;
            if (query.Length > 0 &&
                !item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                !(item.Author?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) &&
                !item.RootPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            yield return item;
        }
    }

    private bool MatchesSourceFilter(ModItem item)
        => _sourceFilter.SelectedIndex switch
        {
            1 => item.SourceType == ContentSourceType.LocalMod,
            2 => item.SourceType == ContentSourceType.Workshop,
            3 => item.SourceType == ContentSourceType.BuiltIn,
            4 => item.SourceType == ContentSourceType.Custom,
            _ => true
        };

    private void ToggleSelectAll()
    {
        var visibleEligible = _grid.Rows.Cast<DataGridViewRow>()
            .Select(r => r.Tag as ModItem)
            .Where(i => i is { IsXmlSelectable: true })
            .Cast<ModItem>()
            .ToList();
        if (visibleEligible.Count == 0)
            return;

        var shouldSelect = visibleEligible.Any(i => !i.Selected);
        foreach (var item in visibleEligible)
            item.Selected = shouldSelect;
        RebuildGrid();
    }

    private void GridCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.RowIndex < 0 || e.ColumnIndex == _grid.Columns["Selected"].Index)
        {
            _dragStartRow = -1;
            return;
        }
        _dragStartRow = e.RowIndex;
        _dragLastRow = e.RowIndex;
        _dragStartPoint = new Point(e.X + _grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, cutOverflow: false).Left,
            e.Y + _grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, cutOverflow: false).Top);
        _dragSelecting = false;
    }

    private void GridMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragStartRow < 0 || Control.MouseButtons != MouseButtons.Left)
            return;

        var hit = _grid.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0)
            return;

        if (!_dragSelecting)
        {
            var dragBounds = new Rectangle(
                _dragStartPoint.X - SystemInformation.DragSize.Width / 2,
                _dragStartPoint.Y - SystemInformation.DragSize.Height / 2,
                SystemInformation.DragSize.Width,
                SystemInformation.DragSize.Height);
            if (dragBounds.Contains(e.Location) && hit.RowIndex == _dragStartRow)
                return;
        }
        if (hit.RowIndex == _dragLastRow && _dragSelecting)
            return;

        var startingDrag = !_dragSelecting;
        _dragSelecting = true;
        _grid.BeginBulkUpdate();
        try
        {
            var oldFirst = Math.Min(_dragStartRow, _dragLastRow);
            var oldLast = Math.Max(_dragStartRow, _dragLastRow);
            var newFirst = Math.Min(_dragStartRow, hit.RowIndex);
            var newLast = Math.Max(_dragStartRow, hit.RowIndex);

            if (startingDrag)
            {
                _grid.ClearSelection();
                for (var row = newFirst; row <= newLast; row++)
                    _grid.Rows[row].Selected = true;
            }
            else
            {
                for (var row = oldFirst; row <= oldLast; row++)
                {
                    if (row < newFirst || row > newLast)
                        _grid.Rows[row].Selected = false;
                }
                for (var row = newFirst; row <= newLast; row++)
                {
                    if (row < oldFirst || row > oldLast)
                        _grid.Rows[row].Selected = true;
                }
            }

            _dragLastRow = hit.RowIndex;
        }
        finally
        {
            _grid.EndBulkUpdate();
        }
    }

    private void ResetDragSelection()
    {
        _dragStartRow = -1;
        _dragLastRow = -1;
        _dragSelecting = false;
    }

    private void GridMouseUp(object? sender, MouseEventArgs e)
    {
        try
        {
            if (e.Button != MouseButtons.Left || _dragStartRow < 0)
                return;

            var hit = _grid.HitTest(e.X, e.Y);
            if (hit.RowIndex >= 0 && hit.RowIndex != _dragStartRow)
            {
                _dragLastRow = hit.RowIndex;
                _dragSelecting = true;
            }
            if (!_dragSelecting)
                return;

            var firstRow = Math.Min(_dragStartRow, _dragLastRow);
            var lastRow = Math.Max(_dragStartRow, _dragLastRow);
            var draggedRowCount = lastRow - firstRow + 1;
            var selectedXmlCount = 0;
            _updatingGrid = true;
            try
            {
                for (var rowIndex = firstRow; rowIndex <= lastRow; rowIndex++)
                {
                    var row = _grid.Rows[rowIndex];
                    if (row.Tag is ModItem { IsXmlSelectable: true } item)
                    {
                        item.Selected = true;
                        row.Cells["Selected"].Value = true;
                        selectedXmlCount++;
                    }
                }
            }
            finally
            {
                _updatingGrid = false;
            }
            UpdateSelectionState();
            var skippedCount = draggedRowCount - selectedXmlCount;
            _status.Text = Loc.T(
                $"已框选连续 {draggedRowCount} 行：自动勾选 {selectedXmlCount} 个可处理 XML 地图；{skippedCount} 个无可移除边界、只读、异常或危险 BIN 项目保持未勾选。滚动位置已保留。",
                $"Box-selected {draggedRowCount} consecutive rows: checked {selectedXmlCount} eligible XML maps; {skippedCount} items without removable boundaries, read-only items, invalid items, or dangerous BIN files remain unchecked. Scroll position was preserved.");
        }
        finally
        {
            ResetDragSelection();
            UpdateDetailsAndButtons();
        }
    }

    private void UpdateSelectionState()
    {
        var visibleEligible = _grid.Rows.Cast<DataGridViewRow>()
            .Select(r => r.Tag as ModItem)
            .Where(i => i is { IsXmlSelectable: true })
            .Cast<ModItem>()
            .ToList();
        var selectedVisible = visibleEligible.Count(i => i.Selected);

        _selectAll.CheckState = visibleEligible.Count == 0 || selectedVisible == 0
            ? CheckState.Unchecked
            : selectedVisible == visibleEligible.Count
                ? CheckState.Checked
                : CheckState.Indeterminate;

        var selected = _items.Where(x => x.Selected && x.IsSelectable).ToList();
        var selectedXml = selected.Where(x => x.IsXmlSelectable).ToList();
        var selectedBins = selected.Where(x => x.IsBinarySelectable).ToList();
        var xmlCount = selectedXml.SelectMany(x => x.XmlFiles.Where(f => f.BoundaryCount > 0))
            .Select(x => x.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var boundaryCount = selectedXml.Sum(x => x.BoundaryCount);
        _selectionStatus.Text = selected.Count == 0
            ? Loc.T("未选择项目", "No items selected")
            : selectedBins.Count > 0
                ? Loc.T($"已选择 {selectedBins.Count} 个危险 BIN（每次只能处理一个）", $"Selected: {selectedBins.Count} dangerous BIN files (one at a time)")
                : Loc.T($"已选择 {selectedXml.Count} 个项目 · {xmlCount} 个 XML · {boundaryCount} 个边界", $"Selected: {selectedXml.Count} items · {xmlCount} XML files · {boundaryCount} Boundary elements");
        _binaryButton.Visible = selectedBins.Count > 0;
        _binaryButton.Enabled = selectedBins.Count == 1 && selectedXml.Count == 0 && !_progress.Visible;
        _removeButton.Enabled = selectedXml.Count > 0 && selectedBins.Count == 0 && !_progress.Visible;
    }

    private void UpdateDetailsAndButtons()
    {
        var item = GetCurrentItem();
        _openFolderButton.Enabled = item is not null && Directory.Exists(item.RootPath);
        _previewButton.Enabled = item is not null && item.LevelXmlCount > 0;

        if (item is null)
        {
            _details.Text = Loc.T("选择一个项目查看详情。", "Select an item to view details.");
            return;
        }

        var lines = new List<string>
        {
            Loc.T("名称: ", "Name: ") + item.Name,
            Loc.T("来源: ", "Source: ") + item.SourceLabel,
            Loc.T("作者: ", "Author: ") + (string.IsNullOrWhiteSpace(item.Author) ? Loc.T("（未提供）", "(not provided)") : item.Author),
            Loc.T("路径: ", "Path: ") + item.RootPath
        };
        if (!string.IsNullOrWhiteSpace(item.WorkshopId))
            lines.Add("Workshop ID: " + item.WorkshopId);
        if (!string.IsNullOrWhiteSpace(item.DiscoveryNote))
            lines.Add(Loc.T("说明: ", "Note: ") + item.DiscoveryNote);
        if (!string.IsNullOrWhiteSpace(item.ContentCategory))
            lines.Add(Loc.T("内容类别: ", "Content category: ") + item.ContentCategory);
        if (!string.IsNullOrWhiteSpace(item.OriginalFileName))
            lines.Add(Loc.T("原始文件: ", "Original file: ") + item.OriginalFileName);
        if (!string.IsNullOrWhiteSpace(item.RecognitionBasis))
            lines.Add(Loc.T("识别依据: ", "Recognition basis: ") + item.RecognitionBasis);
        lines.Add(string.Empty);
        lines.Add(item.IsCompiledBinaryMap
            ? Loc.T($"BIN 版本: {item.BinaryVersion}；边界顶点: {item.BinaryBoundaryVertexCount}", $"BIN version: {item.BinaryVersion}; boundary vertices: {item.BinaryBoundaryVertexCount}")
            : Loc.T($"关卡 XML: {item.LevelXmlCount}；边界: {item.BoundaryCount}", $"Level XML: {item.LevelXmlCount}; Boundary: {item.BoundaryCount}"));
        lines.Add(string.Empty);

        foreach (var xml in item.XmlFiles.Where(x => x.IsLevelLike))
        {
            var suffix = !string.IsNullOrWhiteSpace(xml.Error)
                ? Loc.T(" [错误: ", " [Error: ") + xml.Error + "]"
                : xml.BoundaryCount > 0 && !xml.CanWrite
                    ? Loc.T($" [边界: {xml.BoundaryCount}; 无写权限: {xml.WriteError}]", $" [Boundary: {xml.BoundaryCount}; no write access: {xml.WriteError}]")
                    : Loc.T($" [边界: {xml.BoundaryCount}]", $" [Boundary: {xml.BoundaryCount}]") + (string.IsNullOrWhiteSpace(xml.Warning) ? string.Empty : Loc.T(" [提示: ", " [Note: ") + xml.Warning + "]");
            lines.Add("• " + xml.RelativePath + suffix);
        }

        if (item.SourceType == ContentSourceType.BuiltIn)
        {
            lines.Add(string.Empty);
            lines.Add(item.IsCompiledBinaryMap
                ? item.IsBinarySelectable
                    ? Loc.T("⚠ 这是游戏原生 TDBIN 2.0.4。只能通过独立红色按钮修改；程序会先完整备份并验证。", "⚠ This is a built-in TDBIN 2.0.4 file. It can only be modified through the separate red button, with a complete verified backup first.")
                    : Loc.T("说明：该游戏原生 BIN 未通过安全验证，只读。", "Note: This built-in BIN did not pass safety validation and remains read-only.")
                : Loc.T("⚠ 这是随游戏安装的 XML 内容。若选择处理，程序会先备份并要求额外确认；Steam 更新或“验证文件完整性”可能恢复修改。", "⚠ This XML content is installed with the game. The application will back it up and require extra confirmation; a Steam update or file verification may restore it."));
        }
        else if (item.SourceType == ContentSourceType.Workshop)
        {
            lines.Add(string.Empty);
            lines.Add(Loc.T("提示：Workshop 作者更新该项目后，Steam 可能重新下载 XML，从而覆盖本工具的修改。", "Note: When the Workshop author updates this item, Steam may download the XML again and overwrite this application's changes."));
        }

        _details.Text = string.Join(Environment.NewLine, lines);
    }

    private ModItem? GetCurrentItem()
    {
        if (_grid.SelectedRows.Count == 0)
            return null;
        return _grid.SelectedRows[0].Tag as ModItem;
    }

    private void OpenSelectedFolder()
    {
        var item = GetCurrentItem();
        if (item is null || !Directory.Exists(item.RootPath))
            return;
        OpenFolder(item.RootPath);
    }

    private void PreviewSelected()
    {
        var item = GetCurrentItem();
        if (item is null || item.LevelXmlCount == 0)
            return;
        using var preview = new TextPreviewForm(item);
        preview.ShowDialog(this);
    }

    private async Task RemoveSelectedAsync()
    {
        var selected = _items.Where(x => x.Selected && x.IsXmlSelectable).ToList();
        if (selected.Count == 0)
            return;

        SetBusy(true, Loc.T("正在进行执行前安全检查…", "Running preflight safety checks…"));
        try
        {
            var problems = await Task.Run(() => BoundaryOperationService.Preflight(selected));
            if (problems.Count > 0)
            {
                var text = string.Join("\r\n\r\n", problems.Take(12).Select(p => p.Path + "\r\n  " + p.Message));
                if (problems.Count > 12)
                    text += Loc.T($"\r\n\r\n…另有 {problems.Count - 12} 个问题。", $"\r\n\r\n…and {problems.Count - 12} more problems.");
                MessageBox.Show(this,
                    Loc.T("安全检查没有通过，因此没有修改任何文件。\r\n\r\n", "The safety check failed, so no files were modified.\r\n\r\n") + text,
                    Loc.T("已停止操作", "Operation stopped"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var summary = BuildSummary(selected);
            using var confirm = new ConfirmDialog(summary, BuildConfirmationDetail(selected));
            if (confirm.ShowDialog(this) != DialogResult.OK)
                return;

            _status.Text = Loc.T("正在创建并校验完整备份…", "Creating and verifying a complete backup…");
            var session = await Task.Run(() => BackupManager.CreateSession(selected));

            var finalConfirm = MessageBox.Show(this,
                Loc.T($"备份已经成功创建并通过 SHA-256 校验。\r\n\r\n备份位置：\r\n{session.DirectoryPath}\r\n\r\n现在才会真正修改 {summary.XmlCount} 个 XML，并移除 {summary.BoundaryCount} 个 Boundary。\r\n\r\n是否继续？", $"The backup was created and passed SHA-256 verification.\r\n\r\nBackup location:\r\n{session.DirectoryPath}\r\n\r\nThe application will now modify {summary.XmlCount} XML files and remove {summary.BoundaryCount} Boundary elements.\r\n\r\nContinue?"),
                Loc.T("最后确认", "Final confirmation"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (finalConfirm != DialogResult.Yes)
            {
                session.Manifest.Status = "CancelledAfterBackup";
                BackupManager.SaveManifest(session);
                _status.Text = Loc.T("已取消。备份已保留，没有修改原文件。", "Cancelled. The backup was kept and the original files were not modified.");
                return;
            }

            var progress = new Progress<string>(QueueStatus);
            var result = await Task.Run(() => BoundaryOperationService.Execute(selected, session, progress));

            MessageBox.Show(this,
                Loc.T($"处理完成。\r\n\r\n修改 XML：{result.ModifiedFiles}\r\n移除 Boundary：{result.RemovedBoundaries}\r\n\r\n备份保存在：\r\n{result.Session.DirectoryPath}\r\n\r\n如果游戏中出现异常，可以使用“恢复最近一次备份”。", $"Processing complete.\r\n\r\nModified XML files: {result.ModifiedFiles}\r\nRemoved Boundary elements: {result.RemovedBoundaries}\r\n\r\nBackup location:\r\n{result.Session.DirectoryPath}\r\n\r\nIf the game has problems, use Restore latest backup."),
                Loc.T("完成", "Complete"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            await ScanAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                Loc.T("操作失败。程序已尝试回滚本批已经修改的文件；备份目录会保留用于手动恢复。\r\n\r\n", "The operation failed. The application attempted to roll back files modified in this batch; the backup directory remains available for manual recovery.\r\n\r\n") + ex.Message,
                Loc.T("操作失败", "Operation failed"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            _status.Text = Loc.T("操作失败：", "Operation failed: ") + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async Task ModifySelectedBinaryAsync()
    {
        var selected = _items.Where(x => x.Selected && x.IsBinarySelectable).ToList();
        if (selected.Count != 1 || _items.Any(x => x.Selected && x.IsXmlSelectable))
            return;
        var item = selected[0];
        var answer = MessageBox.Show(this,
            Loc.T(
                $"危险操作：将修改游戏原生 BIN。\r\n\r\n地图：{item.Name}\r\n文件：{item.BinaryPath}\r\n将移除 {item.BinaryBoundaryVertexCount} 个边界顶点。\r\n\r\n程序会先创建并校验完整备份，但游戏更新或文件验证可能覆盖修改。是否执行？",
                $"DANGEROUS OPERATION: This will modify a built-in game BIN.\r\n\r\nMap: {item.Name}\r\nFile: {item.BinaryPath}\r\nBoundary vertices to remove: {item.BinaryBoundaryVertexCount}.\r\n\r\nA complete verified backup will be created first, but a game update or file verification may overwrite the change. Execute?"),
            Loc.T("危险：修改 BIN", "DANGER: Modify BIN"), MessageBoxButtons.YesNo, MessageBoxIcon.Error, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        SetBusy(true, Loc.T("正在备份、修改并验证 BIN…", "Backing up, modifying, and verifying BIN…"));
        try
        {
            var result = await Task.Run(() => TdbinBoundaryService.BackupAndRemove(item));
            MessageBox.Show(this,
                Loc.T($"BIN 修改完成。\r\n\r\n移除边界顶点：{result.RemovedVertices}\r\n备份：{result.BackupPath}", $"BIN modification complete.\r\n\r\nRemoved boundary vertices: {result.RemovedVertices}\r\nBackup: {result.BackupPath}"),
                Loc.T("完成", "Complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            await ScanAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("BIN 修改失败；原始备份已保留，程序已尽力确保或恢复原文件。\r\n\r\n", "BIN modification failed. The original backup was preserved, and the application attempted to retain or restore the original file.\r\n\r\n") + ex.Message,
                Loc.T("BIN 修改失败", "BIN modification failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false, null); }
    }

    private async Task RestoreLatestAsync()
    {
        var session = BackupManager.GetLatestRestorableSession();
        if (session is null)
        {
            MessageBox.Show(this, Loc.T("没有找到可恢复的备份。", "No restorable backup was found."), Loc.T("恢复备份", "Restore backup"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var result = MessageBox.Show(this,
            Loc.T($"将恢复最近一次备份：\r\n{session.DirectoryPath}\r\n\r\n包含 {session.Manifest.Entries.Count} 个 XML。恢复会覆盖这些 XML 当前版本。\r\n\r\n确定恢复吗？", $"The latest backup will be restored:\r\n{session.DirectoryPath}\r\n\r\nIt contains {session.Manifest.Entries.Count} XML files. Restoring will overwrite their current versions.\r\n\r\nContinue?"),
            Loc.T("确认恢复", "Confirm restore"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes)
            return;

        SetBusy(true, Loc.T("正在校验并恢复备份…", "Verifying and restoring backup…"));
        try
        {
            await Task.Run(() => BackupManager.Restore(session));
            MessageBox.Show(this, Loc.T("备份恢复完成。", "Backup restore complete."), Loc.T("恢复完成", "Restore complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            await ScanAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("恢复失败", "Restore failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private static OperationSummary BuildSummary(IReadOnlyCollection<ModItem> selected)
    {
        var files = selected.SelectMany(x => x.XmlFiles.Where(f => f.BoundaryCount > 0))
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        return new OperationSummary
        {
            ItemCount = selected.Count,
            XmlCount = files.Count,
            BoundaryCount = files.Sum(x => x.BoundaryCount),
            IncludesWorkshop = selected.Any(x => x.SourceType == ContentSourceType.Workshop),
            IncludesBuiltIn = selected.Any(x => x.SourceType == ContentSourceType.BuiltIn)
        };
    }

    private static string BuildConfirmationDetail(IEnumerable<ModItem> selected)
    {
        var lines = new List<string>();
        foreach (var item in selected)
        {
            lines.Add($"[{item.SourceLabel}] {item.Name}");
            foreach (var xml in item.XmlFiles.Where(x => x.BoundaryCount > 0))
                lines.Add($"    {xml.RelativePath}    Boundary: {xml.BoundaryCount}");
            lines.Add(string.Empty);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void SetBusy(bool busy, string? status)
    {
        _progress.Visible = busy;
        _rescanButton.Enabled = !busy;
        _addLocationButton.Enabled = !busy;
        _restoreButton.Enabled = !busy;
        _removeButton.Enabled = !busy && _items.Any(x => x.Selected && x.IsXmlSelectable) && !_items.Any(x => x.Selected && x.IsBinarySelectable);
        _binaryButton.Enabled = !busy && _items.Count(x => x.Selected && x.IsBinarySelectable) == 1 && !_items.Any(x => x.Selected && x.IsXmlSelectable);
        _grid.Enabled = !busy;
        _selectAll.Enabled = !busy;
        _scanBuiltInMaps.Enabled = !busy;
        _scanWorkshopMaps.Enabled = !busy;
        _scanLocalMaps.Enabled = !busy;
        if (!string.IsNullOrWhiteSpace(status))
        {
            StopQueuedStatus();
            _status.Text = status;
        }
    }

    private void QueueStatus(string message)
    {
        _pendingStatus = message;
        if (!_statusTimer.Enabled)
            _statusTimer.Start();
    }

    private void FlushPendingStatus()
    {
        if (_pendingStatus is null)
        {
            _statusTimer.Stop();
            return;
        }
        _status.Text = _pendingStatus;
        _pendingStatus = null;
    }

    private void StopQueuedStatus()
    {
        _statusTimer.Stop();
        _pendingStatus = null;
    }

    private void AdjustSplitter()
    {
        if (_contentSplit is not { IsDisposed: false } split)
            return;
        var minimumTotal = split.Panel1MinSize + split.Panel2MinSize + split.SplitterWidth;
        if (split.Height <= minimumTotal)
            return;
        var desiredPanel2 = Math.Clamp((int)(split.Height * 0.28), split.Panel2MinSize, 240);
        var desiredDistance = split.Height - desiredPanel2 - split.SplitterWidth;
        var maxDistance = split.Height - split.Panel2MinSize - split.SplitterWidth;
        split.SplitterDistance = Math.Clamp(desiredDistance, split.Panel1MinSize, maxDistance);
    }

    private void BeginInteractiveMoveOrResize()
    {
        _rootLayout?.SuspendLayout();
    }

    private void EndInteractiveMoveOrResize()
    {
        _rootLayout?.ResumeLayout(performLayout: true);
        AdjustSplitter();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _statusTimer.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OpenBackupFolder()
    {
        var path = BackupManager.GetBackupRoot();
        Directory.CreateDirectory(path);
        OpenFolder(path);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.T("无法打开文件夹：\r\n", "Unable to open folder:\r\n") + ex.Message, Loc.T("打开失败", "Open failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
