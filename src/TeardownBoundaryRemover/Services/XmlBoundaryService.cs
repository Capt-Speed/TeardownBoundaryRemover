using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace TeardownBoundaryRemover.Services;

internal sealed record XmlAnalysis(
    bool IsLevelLike,
    int BoundaryCount,
    int IgnoredBoundaryGroupCount,
    string Sha256,
    string? RootName,
    string? Warning,
    string? Error);

internal sealed record XmlRemovalResult(int RemovedCount, string NewSha256);

internal static class XmlBoundaryService
{
    private const long MaxXmlBytes = 256L * 1024L * 1024L;
    private static readonly Regex EncodingDeclarationRegex = new("encoding\\s*=\\s*[\"'](?<enc>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // This is intentionally a narrow rule. There is no verified evidence that a group named
    // "Boundary" is equivalent to the XML <boundary> entity, so it is reported but never edited.
    private static bool IsTargetScene(XmlElement root)
        => root.NamespaceURI.Length == 0 && root.LocalName.Equals("scene", StringComparison.Ordinal);

    public static XmlAnalysis Analyze(string path, bool forceLevelLike = false)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > MaxXmlBytes)
                return new XmlAnalysis(false, 0, 0, string.Empty, null, null, Loc.T("XML 超过 256 MB，安全策略已跳过", "XML exceeds 256 MB and was skipped by the safety policy"));

            // A full SHA-256 requires another complete disk read. During discovery it is only useful
            // for files that are actual write candidates; non-target XML files have no later write path.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = XmlReader.Create(stream, CreateReaderSettings());

            string? rootName = null;
            var isScene = false;
            var boundaryCount = 0;
            var ignoredGroupCount = 0;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                if (rootName is null)
                {
                    rootName = reader.Name;
                    isScene = reader.NamespaceURI.Length == 0 && reader.LocalName.Equals("scene", StringComparison.Ordinal);
                }

                if (isScene && reader.NamespaceURI.Length == 0 && reader.LocalName.Equals("boundary", StringComparison.Ordinal))
                    boundaryCount++;
                else if (isScene && reader.NamespaceURI.Length == 0 && reader.LocalName.Equals("group", StringComparison.Ordinal) &&
                         reader.GetAttribute("name") == "Boundary")
                    ignoredGroupCount++;
            }

            var warning = ignoredGroupCount > 0
                ? Loc.T($"发现 {ignoredGroupCount} 个 <group name=\"Boundary\">；其语义未经验证，已保留且不会修改。", $"Found {ignoredGroupCount} <group name=\"Boundary\"> elements. Their semantics are unverified, so they are preserved and will not be modified.")
                : null;
            var hash = boundaryCount > 0 ? HashUtil.Sha256File(path) : string.Empty;
            return new XmlAnalysis(isScene, boundaryCount, ignoredGroupCount, hash, rootName, warning, null);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return new XmlAnalysis(false, 0, 0, string.Empty, null, null, ex.Message);
        }
    }

    public static XmlRemovalResult RemoveBoundaryToOriginal(string path, string expectedSha256)
    {
        var currentHash = HashUtil.Sha256File(path);
        if (!currentHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.T("文件在扫描后发生变化，已停止修改。请重新扫描。\r\n", "The file changed after scanning. Modification was stopped; please rescan.\r\n") + path);

        var document = LoadDocument(path);
        var root = document.DocumentElement ?? throw new XmlException(Loc.T("XML 没有根节点。", "The XML has no root element."));
        if (!IsTargetScene(root))
            throw new InvalidOperationException(Loc.T("该 XML 的根节点不是无命名空间、精确小写的 <scene>，安全策略拒绝修改。\r\n", "The XML root is not an exact lowercase <scene> without a namespace. The safety policy refuses to modify it.\r\n") + path);

        var nodes = FindBoundaryNodes(document);
        if (nodes.Count == 0)
            throw new InvalidOperationException(Loc.T("执行前重新检查时已找不到可处理的 <boundary>。请重新扫描。\r\n", "No eligible <boundary> was found during the final check. Please rescan.\r\n") + path);

        foreach (var node in nodes)
            node.ParentNode?.RemoveChild(node);

        var tempPath = path + ".tbr-temp-" + Guid.NewGuid().ToString("N");
        try
        {
            SavePreservingStyle(document, path, tempPath);
            var written = LoadDocument(tempPath);
            if (FindBoundaryNodes(written).Count != 0)
                throw new InvalidOperationException(Loc.T("临时文件验证失败：仍然包含可处理的 <boundary>。", "Temporary file validation failed: eligible <boundary> elements remain."));
            if (!SemanticallyEqual(document, written))
                throw new InvalidOperationException(Loc.T("临时文件验证失败：除 <boundary> 外检测到 XML 结构差异。", "Temporary file validation failed: an XML structural difference other than <boundary> removal was detected."));

            // Recheck immediately before the atomic replacement so a concurrent editor cannot be overwritten.
            if (!HashUtil.Sha256File(path).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Loc.T("文件在写入前发生变化，已停止修改。\r\n", "The file changed before writing, so modification was stopped.\r\n") + path);
            ReplaceFileSafely(tempPath, path);
            return new XmlRemovalResult(nodes.Count, HashUtil.Sha256File(path));
        }
        finally
        {
            TryDeleteTemp(tempPath);
        }
    }

    public static int CountBoundaryNodes(string path)
    {
        var document = LoadDocument(path);
        return document.DocumentElement is { } root && IsTargetScene(root) ? FindBoundaryNodes(document).Count : 0;
    }

    private static XmlDocument LoadDocument(string path)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = XmlReader.Create(stream, CreateReaderSettings());
        document.Load(reader);
        return document;
    }

    private static XmlReaderSettings CreateReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = false,
        IgnoreProcessingInstructions = false,
        IgnoreWhitespace = false,
        CloseInput = false
    };

    private static List<XmlElement> FindBoundaryNodes(XmlDocument document)
    {
        var result = new List<XmlElement>();
        if (document.DocumentElement is not { } root || !IsTargetScene(root))
            return result;
        Walk(root, result);
        return result;

        static void Walk(XmlNode node, List<XmlElement> result)
        {
            if (node is XmlElement element && element.NamespaceURI.Length == 0 && element.LocalName.Equals("boundary", StringComparison.Ordinal))
                result.Add(element);
            foreach (XmlNode child in node.ChildNodes)
                Walk(child, result);
        }
    }

    private static void SavePreservingStyle(XmlDocument document, string originalPath, string outputPath)
    {
        var (encoding, hasDeclaration) = DetectEncodingAndDeclaration(File.ReadAllBytes(originalPath));
        var settings = new XmlWriterSettings
        {
            Encoding = encoding,
            OmitXmlDeclaration = !hasDeclaration,
            Indent = false,
            NewLineHandling = NewLineHandling.None,
            CloseOutput = true,
            CheckCharacters = true
        };
        using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = XmlWriter.Create(stream, settings);
        document.Save(writer);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static (Encoding Encoding, bool HasDeclaration) DetectEncodingAndDeclaration(byte[] bytes)
    {
        var (detectedEncoding, bomLength, locked) = DetectBaseEncoding(bytes);
        var prefixLength = Math.Min(bytes.Length - bomLength, 2048);
        var prefix = prefixLength > 0 ? detectedEncoding.GetString(bytes, bomLength, prefixLength).TrimStart('\uFEFF') : string.Empty;
        var trimmed = prefix.TrimStart(' ', '\t', '\r', '\n');
        var hasDeclaration = trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
        if (hasDeclaration && !locked)
        {
            var match = EncodingDeclarationRegex.Match(trimmed);
            if (match.Success)
            {
                try
                {
                    var declared = Encoding.GetEncoding(match.Groups["enc"].Value, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                    detectedEncoding = declared is UTF8Encoding ? new UTF8Encoding(false, true) : declared;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(Loc.T("XML 声明的编码无法被安全保留，因此已停止写入：", "The XML-declared encoding cannot be safely preserved, so writing was stopped: ") + match.Groups["enc"].Value, ex);
                }
            }
        }
        return (detectedEncoding, hasDeclaration);
    }

    private static (Encoding Encoding, int BomLength, bool Locked) DetectBaseEncoding(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF) return (new UTF32Encoding(true, true, true), 4, true);
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) return (new UTF32Encoding(false, true, true), 4, true);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return (new UTF8Encoding(true, true), 3, true);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return (new UnicodeEncoding(true, true, true), 2, true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return (new UnicodeEncoding(false, true, true), 2, true);
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0x3C) return (new UTF32Encoding(true, false, true), 0, true);
        if (bytes.Length >= 4 && bytes[0] == 0x3C && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0) return (new UTF32Encoding(false, false, true), 0, true);
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0x3C && bytes[2] == 0 && bytes[3] == 0x3F) return (new UnicodeEncoding(true, false, true), 0, true);
        if (bytes.Length >= 4 && bytes[0] == 0x3C && bytes[1] == 0 && bytes[2] == 0x3F && bytes[3] == 0) return (new UnicodeEncoding(false, false, true), 0, true);
        return (new UTF8Encoding(false, true), 0, false);
    }

    private static bool SemanticallyEqual(XmlDocument left, XmlDocument right) => CompareNodes(left, right);
    private static bool CompareNodes(XmlNode left, XmlNode right)
    {
        if (left.NodeType != right.NodeType) return false;
        if (left is XmlElement le && right is XmlElement re)
        {
            if (!string.Equals(le.LocalName, re.LocalName, StringComparison.Ordinal) || !string.Equals(le.NamespaceURI, re.NamespaceURI, StringComparison.Ordinal)) return false;
            var la = le.Attributes.Cast<XmlAttribute>().Select(a => (a.NamespaceURI, a.LocalName, a.Value)).OrderBy(a => a.NamespaceURI, StringComparer.Ordinal).ThenBy(a => a.LocalName, StringComparer.Ordinal).ToArray();
            var ra = re.Attributes.Cast<XmlAttribute>().Select(a => (a.NamespaceURI, a.LocalName, a.Value)).OrderBy(a => a.NamespaceURI, StringComparer.Ordinal).ThenBy(a => a.LocalName, StringComparer.Ordinal).ToArray();
            if (!la.SequenceEqual(ra)) return false;
        }
        else if (left is XmlText or XmlCDataSection or XmlComment or XmlProcessingInstruction)
        {
            if (!string.Equals(left.Value, right.Value, StringComparison.Ordinal)) return false;
        }
        var lc = MeaningfulChildren(left).ToArray();
        var rc = MeaningfulChildren(right).ToArray();
        return lc.Length == rc.Length && lc.Zip(rc).All(pair => CompareNodes(pair.First, pair.Second));
    }

    private static IEnumerable<XmlNode> MeaningfulChildren(XmlNode node)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child is XmlWhitespace or XmlSignificantWhitespace || child is XmlText text && string.IsNullOrWhiteSpace(text.Value) || child is XmlDeclaration) continue;
            yield return child;
        }
    }

    private static void ReplaceFileSafely(string tempPath, string destinationPath)
    {
        // This tool only supports the atomic Windows replacement path. Falling back to a copy-overwrite
        // could leave a truncated live map after a crash, which is less safe than refusing the operation.
        File.Replace(tempPath, destinationPath, null, ignoreMetadataErrors: true);
    }

    private static void TryDeleteTemp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
