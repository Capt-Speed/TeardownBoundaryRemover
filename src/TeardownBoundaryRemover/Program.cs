using TeardownBoundaryRemover.Services;
using TeardownBoundaryRemover.UI;

namespace TeardownBoundaryRemover;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            return SelfTest.Run();
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Application.Run(new MainForm());
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Loc.T($"程序遇到未处理错误。\r\n\r\n{ex.Message}", $"The application encountered an unhandled error.\r\n\r\n{ex.Message}"),
                "Teardown Boundary Remover",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }
}
