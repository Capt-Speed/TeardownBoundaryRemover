namespace TeardownBoundaryRemover.UI;

internal static class WindowSizing
{
    public static void FitToWorkingArea(Form form, double widthRatio, double heightRatio, int minWidth = 420, int minHeight = 300)
    {
        var area = Screen.FromControl(form).WorkingArea;
        var width = Math.Clamp((int)(area.Width * widthRatio), Math.Min(minWidth, area.Width), area.Width);
        var height = Math.Clamp((int)(area.Height * heightRatio), Math.Min(minHeight, area.Height), area.Height);
        form.Bounds = new Rectangle(
            area.Left + (area.Width - width) / 2,
            area.Top + (area.Height - height) / 2,
            width,
            height);
    }
}
