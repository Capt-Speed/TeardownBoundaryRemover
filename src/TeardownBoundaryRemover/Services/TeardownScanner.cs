using System.Buffers;
using System.Text;

namespace TeardownBoundaryRemover.Services;

internal sealed class TeardownScanner
{
    // XML parsing includes disk reads. A small cap improves throughput across many map files
    // without causing destructive random-I/O contention on common game-library HDDs.
    private static readonly int MaxConcurrentXmlReads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
    private static readonly ScanCacheService ScanCache = ScanCacheService.Load();
    private readonly Func<SteamDiscovery> _discoverSteam;
    private readonly Func<string> _getDocumentsPath;
    private static readonly EnumerationOptions RecursiveEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public TeardownScanner() : this(SteamLocator.Discover, () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) { }

    internal TeardownScanner(Func<SteamDiscovery> discoverSteam, Func<string>? getDocumentsPath = null)
    {
        _discoverSteam = discoverSteam;
        _getDocumentsPath = getDocumentsPath ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    public ScanReport ScanAll(
        IEnumerable<string> extraLocations,
        ScanOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new ScanReport();
        try
        {
            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenXml = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (options.LocalMaps)
            {
                progress?.Report(Loc.T("正在定位本地 Teardown 地图…", "Locating local Teardown maps…"));
                var documents = _getDocumentsPath();
                var localMods = Path.Combine(documents, "Teardown", "mods");
                if (Directory.Exists(localMods))
                {
                    report.Locations.Add(Loc.T("本地地图: ", "Local maps: ") + localMods);
                    ScanModContainer(localMods, ContentSourceType.LocalMod, report, seenRoots, seenXml, progress, cancellationToken);
                }

                foreach (var extra in NormalizeDistinctPaths(extraLocations))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(extra))
                        continue;
                    progress?.Report(Loc.T("正在扫描本地自定义位置: ", "Scanning custom local location: ") + extra);
                    report.Locations.Add(Loc.T("本地自定义: ", "Custom local: ") + extra);
                    ScanCustomLocation(extra, report, seenRoots, seenXml, progress, cancellationToken);
                }
            }

            if (options.WorkshopMaps || options.BuiltInMaps)
            {
                progress?.Report(Loc.T("正在定位 Steam 地图库…", "Locating Steam libraries…"));
                var steam = _discoverSteam();
                if (options.WorkshopMaps)
                {
                    foreach (var workshopRoot in NormalizeDistinctPaths(steam.WorkshopRoots))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        report.Locations.Add(Loc.T("创意工坊: ", "Workshop: ") + workshopRoot);
                        ScanWorkshopContainer(workshopRoot, report, seenRoots, seenXml, progress, cancellationToken);
                    }
                }
                if (options.BuiltInMaps)
                {
                    if (string.IsNullOrWhiteSpace(steam.TeardownInstallPath))
                        report.Warnings.Add(Loc.T("未找到 Teardown 安装目录，无法列出游戏原生地图。", "The Teardown installation folder was not found, so built-in maps cannot be listed."));
                    else
                    {
                        report.Locations.Add(Loc.T("游戏原生: ", "Built-in: ") + steam.TeardownInstallPath);
                        ScanBuiltIn(steam.TeardownInstallPath, report, seenRoots, seenXml, progress, cancellationToken);
                    }
                }
            }

            report.Items.Sort((a, b) =>
            {
                var source = a.SourceType.CompareTo(b.SourceType);
                return source != 0 ? source : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
            });

            progress?.Report(Loc.T($"扫描完成：{report.Items.Count} 个项目，{report.Items.Sum(i => i.BoundaryCount)} 个 Boundary。", $"Scan complete: {report.Items.Count} items, {report.Items.Sum(i => i.BoundaryCount)} Boundary elements."));
            return report;
        }
        finally
        {
            ScanCache.Save();
            TdbinBoundaryService.SaveCache();
        }
    }

    private static void ScanModContainer(
        string container,
        ContentSourceType source,
        ScanReport report,
        HashSet<string> seenRoots,
        HashSet<string> seenXml,
        IProgress<string>? progress,
        CancellationToken token)
    {
        foreach (var dir in SafeEnumerateDirectories(container))
        {
            token.ThrowIfCancellationRequested();
            if (!LooksLikeModRoot(dir))
                continue;
            AddModRoot(dir, source, null, report, seenRoots, seenXml, progress, token);
        }
    }

    private static void ScanWorkshopContainer(
        string container,
        ScanReport report,
        HashSet<string> seenRoots,
        HashSet<string> seenXml,
        IProgress<string>? progress,
        CancellationToken token)
    {
        foreach (var dir in SafeEnumerateDirectories(container))
        {
            token.ThrowIfCancellationRequested();
            if (!LooksLikeModRoot(dir))
                continue;
            AddModRoot(dir, ContentSourceType.Workshop, Path.GetFileName(dir), report, seenRoots, seenXml, progress, token);
        }
    }

    private static void ScanCustomLocation(
        string path,
        ScanReport report,
        HashSet<string> seenRoots,
        HashSet<string> seenXml,
        IProgress<string>? progress,
        CancellationToken token)
    {
        if (LooksLikeModRoot(path))
            AddModRoot(path, ContentSourceType.Custom, null, report, seenRoots, seenXml, progress, token);

        ScanModContainer(path, ContentSourceType.Custom, report, seenRoots, seenXml, progress, token);

        // A custom folder may point directly to unpacked level XML files without info.txt.
        foreach (var xml in SafeEnumerateFiles(path, "*.xml", recursive: false))
        {
            token.ThrowIfCancellationRequested();
            var full = SafeFullPath(xml);
            if (full is null || seenXml.Contains(full))
                continue;
            var analysis = ScanCache.Analyze(full);
            if (!analysis.IsLevelLike)
                continue;

            var item = new ModItem
            {
                Name = FriendlyBuiltInName(full),
                RootPath = Path.GetDirectoryName(full)!,
                SourceType = ContentSourceType.Custom,
                DiscoveryNote = Loc.T("自定义位置中的独立关卡 XML", "Standalone level XML in a custom location")
            };
            item.XmlFiles.Add(ToEntry(full, item.RootPath, analysis));
            report.Items.Add(item);
            seenXml.Add(full);
        }
    }

    private static void AddModRoot(
        string root,
        ContentSourceType source,
        string? workshopId,
        ScanReport report,
        HashSet<string> seenRoots,
        HashSet<string> seenXml,
        IProgress<string>? progress,
        CancellationToken token)
    {
        var fullRoot = SafeFullPath(root);
        if (fullRoot is null || !seenRoots.Add(fullRoot))
            return;

        var info = InfoTxtParser.Read(fullRoot);
        var name = string.IsNullOrWhiteSpace(info.Name) ? Path.GetFileName(fullRoot) : info.Name!;
        progress?.Report(Loc.T($"读取 {name}…", $"Reading {name}…"));

        var item = new ModItem
        {
            Name = name,
            Author = info.Author,
            RootPath = fullRoot,
            SourceType = source,
            WorkshopId = workshopId
        };

        var candidates = EnumerateXmlCandidates(fullRoot, token).ToArray();
        var entries = new System.Collections.Concurrent.ConcurrentBag<XmlFileEntry>();
        var completed = 0;
        var lastReported = 0;
        Parallel.ForEach(candidates, new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = MaxConcurrentXmlReads
        }, path =>
        {
            try
            {
                var full = SafeFullPath(path);
                if (full is null)
                    return;

                // The old scanner performed this 64 KiB prefilter sequentially for every XML
                // before starting its parallel work. Large content packs contain thousands of
                // XML files, so that serialized I/O made the UI appear stuck on one mod name.
                // Keep the conservative prefilter, but run it inside the bounded pipeline.
                var isMain = Path.GetFileName(full).Equals("main.xml", StringComparison.OrdinalIgnoreCase) &&
                             Path.GetDirectoryName(full)!.Equals(fullRoot, StringComparison.OrdinalIgnoreCase);
                var analysis = isMain
                    ? ScanCache.Analyze(full)
                    : ScanCache.AnalyzeCandidate(full, () => FastMayContainBoundary(full));
                if (!analysis.IsLevelLike && string.IsNullOrWhiteSpace(analysis.Error))
                    return;

                entries.Add(ToEntry(full, fullRoot, analysis));
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                if (candidates.Length >= 250 && (done == candidates.Length || done - Volatile.Read(ref lastReported) >= 250))
                {
                    var previous = Interlocked.Exchange(ref lastReported, done);
                    if (done > previous)
                        progress?.Report(Loc.T($"读取 {name}… {done}/{candidates.Length}", $"Reading {name}… {done}/{candidates.Length}"));
                }
            }
        });

        foreach (var entry in entries.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            item.XmlFiles.Add(entry);
            seenXml.Add(entry.Path);
        }

        // List real mods even if they are global-only; they are visible but not selectable.
        report.Items.Add(item);
    }

    private static void ScanBuiltIn(
        string installRoot,
        ScanReport report,
        HashSet<string> seenRoots,
        HashSet<string> seenXml,
        IProgress<string>? progress,
        CancellationToken token)
    {
        progress?.Report(Loc.T("正在读取游戏原生地图索引…", "Reading the built-in map index…"));

        // The current game stores campaign/sandbox levels as compiled data/bin/*.bin files.
        // They can be listed accurately but cannot be edited as XML.
        var binRoot = Path.Combine(installRoot, "data", "bin");
        var binPaths = SafeEnumerateFiles(binRoot, "*.bin", recursive: false).Select(SafeFullPath).Where(x => x is not null).Cast<string>().Where(seenRoots.Add).ToArray();
        var binItems = new System.Collections.Concurrent.ConcurrentBag<ModItem>();
        Parallel.ForEach(binPaths, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 4 }, full =>
        {
            var analysis = TdbinBoundaryService.AnalyzeCached(full);
            var identity = BuiltInMapCatalog.Identify(full);
            binItems.Add(new ModItem
            {
                Name = identity.DisplayName,
                RootPath = binRoot,
                SourceType = ContentSourceType.BuiltIn,
                IsCompiledBinaryMap = true,
                BinaryPath = full,
                BinarySha256 = analysis.Sha256,
                BinaryVersion = analysis.Version,
                BinaryBoundaryVertexCount = analysis.VertexCount,
                BinaryError = analysis.Error,
                BuiltInKind = identity.Kind,
                ContentCategory = identity.Category,
                SourceDetail = identity.SourceDetail,
                RecognitionBasis = identity.RecognitionBasis,
                OriginalFileName = identity.OriginalFileName,
                DiscoveryNote = analysis.IsSupported
                    ? Loc.T("游戏原生编译地图（data\\bin）。仅已验证的 TDBIN 2.0.4 边界记录可通过独立危险操作修改。", "Compiled built-in map (data\\bin). Only verified TDBIN 2.0.4 boundary records can be modified through the separate dangerous operation.")
                    : Loc.T("游戏原生编译地图（data\\bin）。格式未通过验证，只读。", "Compiled built-in map (data\\bin). Its format was not verified and remains read-only.")
            });
        });
        foreach (var item in binItems.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)) report.Items.Add(item);

        // Built-in content mods are editable XML-shaped content, separate from compiled campaign BINs.
        foreach (var container in new[] { Path.Combine(installRoot, "mods"), Path.Combine(installRoot, "dlcs") })
        {
            if (Directory.Exists(container))
                ScanModContainer(container, ContentSourceType.BuiltIn, report, seenRoots, seenXml, progress, token);
        }

        if (!Directory.Exists(binRoot))
            report.Warnings.Add(Loc.T("找到 Teardown 安装目录，但没有发现 data\\bin 原生地图目录。", "The Teardown installation was found, but its data\\bin built-in map folder was not."));
    }

    private static IEnumerable<string> EnumerateXmlCandidates(string root, CancellationToken token)
    {
        var main = Path.Combine(root, "main.xml");
        if (File.Exists(main))
            yield return main;

        foreach (var path in SafeEnumerateFiles(root, "*.xml", recursive: true))
        {
            token.ThrowIfCancellationRequested();
            if (path.Equals(main, StringComparison.OrdinalIgnoreCase))
                continue;
            yield return path;
        }
    }

    private static bool FastMayBeScene(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(stream.Length, 64 * 1024);
            var bytes = new byte[length];
            var read = stream.Read(bytes, 0, bytes.Length);
            Encoding encoding = Encoding.UTF8;
            var offset = 0;
            if (read >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = Encoding.UTF8;
                offset = 3;
            }
            else if (read >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = Encoding.Unicode;
                offset = 2;
            }
            else if (read >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encoding = Encoding.BigEndianUnicode;
                offset = 2;
            }
            var text = encoding.GetString(bytes, offset, read - offset);
            return text.Contains("<scene", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("<boundary", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true; // Let the safe XML parser produce a visible error instead of silently hiding it.
        }
    }

    private static bool FastMayContainBoundary(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024 + 32);
            try
            {
                var carry = 0;
                while (true)
                {
                    var read = stream.Read(buffer, carry, buffer.Length - carry);
                    if (read == 0)
                        return false;
                    var length = carry + read;
                    if (ContainsAsciiIgnoreCase(buffer.AsSpan(0, length), "<boundary"u8) ||
                        ContainsUtf16IgnoreCase(buffer.AsSpan(0, length), "<boundary"u8, littleEndian: true) ||
                        ContainsUtf16IgnoreCase(buffer.AsSpan(0, length), "<boundary"u8, littleEndian: false))
                        return true;

                    carry = Math.Min(24, length);
                    buffer.AsSpan(length - carry, carry).CopyTo(buffer);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            // Let the strict parser surface a visible error instead of silently omitting a file.
            return true;
        }
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                var value = haystack[i + j];
                if (value is >= (byte)'A' and <= (byte)'Z')
                    value = (byte)(value + 32);
                if (value != needle[j]) { matched = false; break; }
            }
            if (matched) return true;
        }
        return false;
    }

    private static bool ContainsUtf16IgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, bool littleEndian)
    {
        var byteLength = needle.Length * 2;
        for (var i = 0; i <= haystack.Length - byteLength; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                var lo = haystack[i + j * 2 + (littleEndian ? 0 : 1)];
                var hi = haystack[i + j * 2 + (littleEndian ? 1 : 0)];
                if (hi != 0) { matched = false; break; }
                if (lo is >= (byte)'A' and <= (byte)'Z')
                    lo = (byte)(lo + 32);
                if (lo != needle[j]) { matched = false; break; }
            }
            if (matched) return true;
        }
        return false;
    }

    private static XmlFileEntry ToEntry(string fullPath, string rootPath, XmlAnalysis analysis)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(rootPath, fullPath);
        }
        catch
        {
            relative = Path.GetFileName(fullPath);
        }

        var (canWrite, writeError) = analysis.BoundaryCount > 0
            ? ProbeWritable(fullPath)
            : (true, (string?)null);

        return new XmlFileEntry
        {
            Path = fullPath,
            RelativePath = relative,
            BoundaryCount = analysis.BoundaryCount,
            Sha256 = analysis.Sha256,
            IsLevelLike = analysis.IsLevelLike,
            CanWrite = canWrite,
            WriteError = writeError,
            Warning = analysis.Warning,
            Error = analysis.Error
        };
    }

    private static (bool CanWrite, string? Error) ProbeWritable(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
                return (false, Loc.T("文件具有只读属性", "The file has the read-only attribute"));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, ex.Message);
        }
    }

    private static bool LooksLikeModRoot(string path)
        => File.Exists(Path.Combine(path, "info.txt")) ||
           File.Exists(Path.Combine(path, "main.xml")) ||
           File.Exists(Path.Combine(path, "main.lua"));

    private static string FriendlyBuiltInName(string xmlPath)
    {
        var stem = Path.GetFileNameWithoutExtension(xmlPath);
        if (stem.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(xmlPath));
            if (!string.IsNullOrWhiteSpace(parent))
                stem = parent;
        }

        return stem.Replace('_', ' ').Replace('-', ' ');
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToArray(); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern, bool recursive)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, recursive ? RecursiveEnumeration : new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    private static IEnumerable<string> NormalizeDistinctPaths(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var full = SafeFullPath(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrWhiteSpace(full) && seen.Add(full))
                yield return full;
        }
    }
}
