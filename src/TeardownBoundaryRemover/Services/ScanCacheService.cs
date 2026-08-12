using System.Collections.Concurrent;
using System.Text.Json;

namespace TeardownBoundaryRemover.Services;

// Discovery cache only: every selected file is still parsed and SHA-256 verified again before backup/write.
// This cache exists to avoid repeatedly parsing unchanged, non-target XML files on normal rescans.
internal sealed class ScanCacheService
{
    private const int CacheVersion = 3;
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TeardownBoundaryRemover",
        "scan-cache.json");

    private readonly ConcurrentDictionary<string, ScanCacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public static ScanCacheService Load()
    {
        var service = new ScanCacheService();
        try
        {
            var stored = JsonSerializer.Deserialize<ScanCacheFile>(File.ReadAllText(CachePath));
            if (stored?.Version == CacheVersion)
            {
                foreach (var entry in stored.Entries)
                    service._entries.TryAdd(entry.Path, entry);
            }
        }
        catch { }
        return service;
    }

    public XmlAnalysis Analyze(string path)
    {
        var info = new FileInfo(path);
        if (TryGetFresh(path, info, out var cached))
        {
            return cached.ToAnalysis();
        }

        var analysis = XmlBoundaryService.Analyze(path);
        // Do not persist transient I/O or permissions errors: they are cheap to retry and can change
        // without changing a file's length or timestamp.
        if (analysis.Error is null)
        {
            _entries[path] = ScanCacheEntry.From(path, info, analysis);
        }
        return analysis;
    }

    public XmlAnalysis AnalyzeCandidate(string path, Func<bool> mayBeScene)
    {
        var info = new FileInfo(path);
        if (TryGetFresh(path, info, out var cached))
            return cached.ToAnalysis();

        if (!mayBeScene())
        {
            var nonTarget = new XmlAnalysis(false, 0, 0, string.Empty, null, null, null);
            _entries[path] = ScanCacheEntry.From(path, info, nonTarget);
            return nonTarget;
        }

        var analysis = XmlBoundaryService.Analyze(path);
        if (analysis.Error is null)
            _entries[path] = ScanCacheEntry.From(path, info, analysis);
        return analysis;
    }

    private bool TryGetFresh(string path, FileInfo info, out ScanCacheEntry entry)
    {
        if (_entries.TryGetValue(path, out var cached) &&
            cached.Length == info.Length && cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
        {
            entry = cached;
            return true;
        }

        entry = null!;
        return false;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var temp = CachePath + ".tmp";
            var file = new ScanCacheFile { Version = CacheVersion, Entries = _entries.Values.Take(20_000).ToList() };
            File.WriteAllText(temp, JsonSerializer.Serialize(file));
            File.Move(temp, CachePath, overwrite: true);
        }
        catch { }
    }

    private sealed class ScanCacheFile
    {
        public int Version { get; set; }
        public List<ScanCacheEntry> Entries { get; set; } = [];
    }

    private sealed class ScanCacheEntry
    {
        public string Path { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public bool IsLevelLike { get; set; }
        public int BoundaryCount { get; set; }
        public int IgnoredBoundaryGroupCount { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string? RootName { get; set; }
        public string? Warning { get; set; }

        public static ScanCacheEntry From(string path, FileInfo info, XmlAnalysis analysis) => new()
        {
            Path = path,
            Length = info.Length,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            IsLevelLike = analysis.IsLevelLike,
            BoundaryCount = analysis.BoundaryCount,
            IgnoredBoundaryGroupCount = analysis.IgnoredBoundaryGroupCount,
            Sha256 = analysis.Sha256,
            RootName = analysis.RootName,
            Warning = analysis.Warning
        };

        public XmlAnalysis ToAnalysis() => new(IsLevelLike, BoundaryCount, IgnoredBoundaryGroupCount, Sha256, RootName, Warning, null);
    }
}
