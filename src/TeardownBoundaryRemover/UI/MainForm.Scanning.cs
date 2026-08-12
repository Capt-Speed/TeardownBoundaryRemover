using TeardownBoundaryRemover.Services;

namespace TeardownBoundaryRemover.UI;

internal sealed partial class MainForm
{
    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        SetBusy(true, Loc.T("正在扫描 Teardown 内容…", "Scanning Teardown content…"));
        try
        {
            var progress = new Progress<string>(QueueStatus);
            var options = new ScanOptions(_scanBuiltInMaps.Checked, _scanWorkshopMaps.Checked, _scanLocalMaps.Checked);
            var report = await Task.Run(
                () => _scanner.ScanAll(_settings.ExtraLocations, options, progress, _scanCts.Token),
                _scanCts.Token);

            _items.Clear();
            _items.AddRange(report.Items);
            RebuildGrid();
            _hasScanned = true;
            _rescanButton.Text = Loc.T("重新扫描", "Rescan");

            var warning = report.Warnings.Count > 0 ? "  ⚠ " + string.Join(" | ", report.Warnings) : string.Empty;
            StopQueuedStatus();
            _status.Text = Loc.T(
                $"扫描完成：{_items.Count} 个项目；{_items.Count(x => x.IsSelectable)} 个可处理；共 {_items.Sum(x => x.DisplayBoundaryCount)} 个边界/顶点。{warning}",
                $"Scan complete: {_items.Count} items; {_items.Count(x => x.IsSelectable)} eligible; {_items.Sum(x => x.DisplayBoundaryCount)} boundary elements/vertices total.{warning}");
        }
        catch (OperationCanceledException)
        {
            StopQueuedStatus();
            _status.Text = Loc.T("扫描已取消。", "Scan cancelled.");
        }
        catch (Exception ex)
        {
            StopQueuedStatus();
            _status.Text = Loc.T("扫描失败：", "Scan failed: ") + ex.Message;
            MessageBox.Show(this, ex.Message, Loc.T("扫描失败", "Scan failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void ScanSourceChanged()
    {
        if (!IsHandleCreated || IsDisposed)
            return;

        _settings.ScanBuiltInMaps = _scanBuiltInMaps.Checked;
        _settings.ScanWorkshopMaps = _scanWorkshopMaps.Checked;
        _settings.ScanLocalMaps = _scanLocalMaps.Checked;
        SettingsService.Save(_settings);
        if (_hasScanned)
        {
            _items.Clear();
            RebuildGrid();
            _status.Text = Loc.T(
                "扫描范围已更改，请点击“重新扫描”刷新结果。",
                "Scan scope changed. Click Rescan to refresh the results.");
        }
    }

    private async Task AddLocationAsync()
    {
        if (!_scanLocalMaps.Checked)
            _scanLocalMaps.Checked = true;

        using var dialog = new FolderBrowserDialog
        {
            Description = Loc.T(
                "选择额外的 Teardown Mod / 地图目录。程序只会读取并识别其中的 Teardown XML。",
                "Select an additional Teardown mod/map folder. The application will only read and identify Teardown XML files."),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var path = Path.GetFullPath(dialog.SelectedPath);
        if (!_settings.ExtraLocations.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            _settings.ExtraLocations.Add(path);
            SettingsService.Save(_settings);
        }

        await ScanAsync();
    }
}
