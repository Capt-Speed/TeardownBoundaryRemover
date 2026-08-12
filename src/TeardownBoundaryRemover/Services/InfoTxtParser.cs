using System.Text;
using System.Text.RegularExpressions;

namespace TeardownBoundaryRemover.Services;

internal sealed record ModInfo(string? Name, string? Author);

internal static class InfoTxtParser
{
    private static readonly Regex KeyValueRegex = new("^\\s*([A-Za-z0-9_.-]+)\\s*=\\s*(.*?)\\s*$", RegexOptions.Compiled);

    public static ModInfo Read(string rootPath)
    {
        var path = System.IO.Path.Combine(rootPath, "info.txt");
        if (!File.Exists(path))
            return new ModInfo(null, null);

        string? name = null;
        string? author = null;
        var localizedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var rawLine in ReadLinesLenient(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                    continue;

                var match = KeyValueRegex.Match(rawLine);
                if (!match.Success)
                    continue;

                var key = match.Groups[1].Value;
                var value = TrimOptionalQuotes(match.Groups[2].Value.Trim());
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                    name = value;
                else if (key.Equals("author", StringComparison.OrdinalIgnoreCase))
                    author = value;
                else if (key.EndsWith("_name", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                    localizedNames[key] = value;
            }
        }
        catch
        {
            // A broken info.txt must never make the scanner write anything.
            // The caller falls back to the folder name.
        }

        name = PickLocalizedName(name, localizedNames);
        return new ModInfo(name, author);
    }

    private static string? PickLocalizedName(string? neutralName, IReadOnlyDictionary<string, string> localizedNames)
    {
        // Teardown commonly uses sc_name for Simplified Chinese and en_name for English.
        // Follow the same Chinese/English rule as the UI, then fall back deterministically.
        var preferredKeys = Loc.IsChinese
            ? new[] { "sc_name", "zh_name", "tc_name", "en_name" }
            : new[] { "en_name" };
        foreach (var key in preferredKeys)
        {
            if (localizedNames.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        if (!string.IsNullOrWhiteSpace(neutralName))
            return neutralName;

        return localizedNames.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Value)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    private static IEnumerable<string> ReadLinesLenient(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes);
        var text = encoding.GetString(bytes).TrimStart('\uFEFF');
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            yield return line;
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(true, true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;
        return new UTF8Encoding(false, false);
    }

    private static string TrimOptionalQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }
}
