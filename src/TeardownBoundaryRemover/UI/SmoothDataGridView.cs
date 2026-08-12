namespace TeardownBoundaryRemover.UI;

internal sealed class SmoothDataGridView : DataGridView
{
    private const int WmSetRedraw = 0x000B;
    private double _pendingWheelRows;
    private int _bulkUpdateDepth;

    public SmoothDataGridView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        UpdateStyles();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (RowCount == 0 || e.Delta == 0)
        {
            base.OnMouseWheel(e);
            return;
        }

        var configuredLines = SystemInformation.MouseWheelScrollLines;
        var linesPerNotch = configuredLines < 0 ? Math.Max(1, DisplayedRowCount(includePartialRow: false) - 1) : Math.Max(1, configuredLines);
        _pendingWheelRows += -(double)e.Delta / SystemInformation.MouseWheelScrollDelta * linesPerNotch;
        var wholeRows = (int)Math.Truncate(_pendingWheelRows);
        if (wholeRows == 0)
            return;

        _pendingWheelRows -= wholeRows;
        var current = FirstDisplayedScrollingRowIndex < 0 ? 0 : FirstDisplayedScrollingRowIndex;
        var target = Math.Clamp(current + wholeRows, 0, Math.Max(0, RowCount - 1));
        if (target != current)
        {
            try
            {
                FirstDisplayedScrollingRowIndex = target;
            }
            catch (InvalidOperationException)
            {
                // Rows can disappear between a wheel message and a filtered-grid rebuild.
                _pendingWheelRows = 0;
            }
        }
    }

    public void BeginBulkUpdate()
    {
        _bulkUpdateDepth++;
        if (_bulkUpdateDepth != 1)
            return;
        if (IsHandleCreated)
            SendMessage(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        SuspendLayout();
    }

    public void EndBulkUpdate()
    {
        if (_bulkUpdateDepth == 0)
            return;
        _bulkUpdateDepth--;
        if (_bulkUpdateDepth != 0)
            return;
        ResumeLayout(performLayout: false);
        if (IsHandleCreated)
            SendMessage(Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
        Invalidate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
