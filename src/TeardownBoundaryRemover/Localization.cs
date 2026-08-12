using System.Globalization;

namespace TeardownBoundaryRemover;

internal static class Loc
{
    public static bool IsChinese { get; } = IsChineseCulture(CultureInfo.CurrentUICulture);

    public static string T(string chinese, string english) => IsChinese ? chinese : english;

    internal static bool IsChineseCulture(CultureInfo culture)
        => string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);
}
