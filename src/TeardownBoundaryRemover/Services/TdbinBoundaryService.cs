using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace TeardownBoundaryRemover.Services;

internal sealed record TdbinAnalysis(string Version, int VertexCount, long CountOffset, long PaddingOffset, long EndOffset,
    byte[] PaddingAndNextBytes, string Sha256, string? Error)
{
    public bool IsSupported => Error is null && Version == "2.0.4";
}

internal sealed record TdbinOperationResult(string BackupPath, int RemovedVertices);

internal static class TdbinBoundaryService
{
    private const int CacheVersion = 1;
    private static readonly string CachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeardownBoundaryRemover", "tdbin-cache.json");
    private static readonly ConcurrentDictionary<string, TdbinCacheEntry> Cache = LoadCache();

    public static TdbinAnalysis AnalyzeCached(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (Cache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
                return cached.ToAnalysis();
            var analysis = Analyze(path);
            if (analysis.Error is null)
                Cache[path] = TdbinCacheEntry.From(path, info, analysis);
            return analysis;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TdbinAnalysis(string.Empty, 0, 0, 0, 0, [], string.Empty, ex.Message);
        }
    }

    public static void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var temp = CachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new TdbinCacheFile { Version = CacheVersion, Entries = Cache.Values.ToList() }));
            File.Move(temp, CachePath, overwrite: true);
        }
        catch { }
    }
    public static TdbinAnalysis Analyze(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zlib = new ZLibStream(file, CompressionMode.Decompress);
            using var counted = new CountingReadStream(zlib);
            var parsed = Parse(counted);
            return parsed with { Sha256 = HashUtil.Sha256File(path) };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
        {
            return new TdbinAnalysis(string.Empty, 0, 0, 0, 0, [], string.Empty, ex.Message);
        }
    }

    public static TdbinOperationResult BackupAndRemove(ModItem item, string? backupRootOverride = null)
    {
        if (!item.IsBinarySelectable || item.BinaryPath is null || item.BinarySha256 is null)
            throw new InvalidOperationException(Loc.T("所选 BIN 不符合已验证的安全修改条件。", "The selected BIN does not meet the verified safety conditions."));
        var path = item.BinaryPath;
        if (!File.Exists(path)) throw new FileNotFoundException(Loc.T("BIN 文件已不存在。", "The BIN file no longer exists."), path);
        if (!HashUtil.Sha256File(path).Equals(item.BinarySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.T("BIN 在扫描后发生变化，请重新扫描。", "The BIN changed after scanning; please rescan."));

        var before = Analyze(path);
        if (!before.IsSupported || before.VertexCount <= 0)
            throw new InvalidOperationException(Loc.T("BIN 最终检查未通过，未作修改。", "Final BIN validation failed; no change was made."));

        var backupRoot = backupRootOverride ?? Path.Combine(BackupManager.GetBackupRoot(), "BIN");
        Directory.CreateDirectory(backupRoot);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8];
        var backupPath = Path.Combine(backupRoot, stamp + "_" + Path.GetFileName(path));
        File.Copy(path, backupPath, overwrite: false);
        if (!HashUtil.Sha256File(backupPath).Equals(item.BinarySha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException(Loc.T("BIN 备份哈希校验失败。", "BIN backup hash verification failed."));

        var tempRaw = path + ".tbr-bin-raw-" + Guid.NewGuid().ToString("N");
        var tempBin = path + ".tbr-bin-new-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = File.OpenRead(path))
            using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
            using (var output = new FileStream(tempRaw, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                CopyExactly(zlib, output, before.CountOffset);
                using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true)) writer.Write(0u);
                // The input is still positioned at the original UInt32 vertex count.
                // Replace that field with zero and skip both the old count and its vertices.
                SkipExactly(zlib, sizeof(uint) + before.VertexCount * 8L);
                zlib.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            using (var raw = File.OpenRead(tempRaw))
            using (var output = new FileStream(tempBin, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) raw.CopyTo(zlib);
                output.Flush(flushToDisk: true);
            }

            var after = Analyze(tempBin);
            if (!after.IsSupported || after.VertexCount != 0 || !after.PaddingAndNextBytes.SequenceEqual(before.PaddingAndNextBytes))
                throw new InvalidDataException(Loc.T("修改后的 BIN 结构验证失败。", "Modified BIN structure validation failed."));
            if (!DecompressedTailsEqual(path, tempBin, before.EndOffset, after.EndOffset))
                throw new InvalidDataException(Loc.T("修改后的 BIN 尾部数据验证失败。", "Modified BIN tail-data validation failed."));

            try
            {
                File.Replace(tempBin, path, null, ignoreMetadataErrors: true);
            }
            catch
            {
                // If atomic replacement fails, leave the verified backup untouched and do not
                // accept a partially written target.
                if (File.Exists(path) && HashUtil.Sha256File(path).Equals(item.BinarySha256, StringComparison.OrdinalIgnoreCase))
                    throw;
                File.Copy(backupPath, path, overwrite: true);
                throw;
            }

            var installed = Analyze(path);
            if (!installed.IsSupported || installed.VertexCount != 0 || !installed.PaddingAndNextBytes.SequenceEqual(before.PaddingAndNextBytes) ||
                !installed.Sha256.Equals(after.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(backupPath, path, overwrite: true);
                throw new InvalidDataException(Loc.T("写入后的 BIN 验证失败，已从备份回滚。", "Post-write BIN validation failed and the backup was restored."));
            }
            Cache.TryRemove(path, out _);
            SaveCache();
            return new TdbinOperationResult(backupPath, before.VertexCount);
        }
        finally
        {
            TryDelete(tempRaw);
            TryDelete(tempBin);
        }
    }

    private static TdbinAnalysis Parse(Stream input)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        var cursor = new HeaderReader(reader);
        if (Encoding.ASCII.GetString(reader.ReadBytes(5)) != "TDBIN") throw new InvalidDataException("Not a TDBIN stream.");
        var versionBytes = reader.ReadBytes(3); if (versionBytes.Length != 3) throw new EndOfStreamException();
        var version = string.Join('.', versionBytes);
        for (var i = 0; i < 4; i++) _ = cursor.ReadCString();
        _ = reader.ReadUInt32(); if (reader.ReadUInt32() != 0xAAA1) throw new InvalidDataException("Invalid TDBIN marker.");
        cursor.SkipTags(cursor.ReadCount(100_000)); cursor.SkipTags(cursor.ReadCount(100_000));
        cursor.SkipFloats(13); cursor.SkipUInt32s(4); cursor.SkipFloats(8);
        cursor.SkipPlayers(version == "2.0.4" ? 18 : 17); cursor.SkipEnvironment();
        var countOffset = input.Position;
        var count = cursor.ReadCount(1_000_000);
        cursor.SkipFloats(checked(count * 2));
        var paddingOffset = input.Position;
        var signature = reader.ReadBytes(5 * sizeof(float) + 32);
        if (signature.Length != 5 * sizeof(float) + 32) throw new EndOfStreamException();
        return new TdbinAnalysis(version, count, countOffset, paddingOffset, input.Position, signature, string.Empty, version == "2.0.4" ? null : "Unsupported TDBIN version " + version);
    }

    private static void CopyExactly(Stream input, Stream output, long count)
    {
        var buffer = new byte[1024 * 1024];
        while (count > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
                throw new EndOfStreamException();

            output.Write(buffer, 0, read);
            count -= read;
        }
    }

    private static void SkipExactly(Stream input, long count)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
                throw new EndOfStreamException();

            count -= read;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Temporary cleanup is best effort and must not hide the operation result.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary cleanup is best effort and must not hide the operation result.
        }
    }

    private static bool DecompressedTailsEqual(string original, string modified, long originalOffset, long modifiedOffset)
    {
        using var leftFile = File.OpenRead(original);
        using var left = new ZLibStream(leftFile, CompressionMode.Decompress);
        using var rightFile = File.OpenRead(modified);
        using var right = new ZLibStream(rightFile, CompressionMode.Decompress);
        SkipExactly(left, originalOffset);
        SkipExactly(right, modifiedOffset);
        var leftBuffer = new byte[1024 * 1024];
        var rightBuffer = new byte[1024 * 1024];
        while (true)
        {
            var leftRead = ReadBlock(left, leftBuffer);
            var rightRead = ReadBlock(right, rightBuffer);
            if (leftRead != rightRead)
                return false;
            if (leftRead == 0)
                return true;
            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                return false;
        }
    }

    private static int ReadBlock(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    private static ConcurrentDictionary<string, TdbinCacheEntry> LoadCache()
    {
        try
        {
            var file = JsonSerializer.Deserialize<TdbinCacheFile>(File.ReadAllText(CachePath));
            if (file?.Version == CacheVersion)
            {
                return new ConcurrentDictionary<string, TdbinCacheEntry>(
                    file.Entries.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt or inaccessible cache is non-authoritative; rebuild it from source files.
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class TdbinCacheFile
    {
        public int Version { get; set; }
        public List<TdbinCacheEntry> Entries { get; set; } = [];
    }
    private sealed class TdbinCacheEntry
    {
        public string Path { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Version { get; set; } = string.Empty;
        public int VertexCount { get; set; }
        public long CountOffset { get; set; }
        public long PaddingOffset { get; set; }
        public long EndOffset { get; set; }
        public byte[] PaddingAndNextBytes { get; set; } = [];
        public string Sha256 { get; set; } = string.Empty;
        public static TdbinCacheEntry From(string path, FileInfo info, TdbinAnalysis analysis) => new()
        {
            Path = path,
            Length = info.Length,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            Version = analysis.Version,
            VertexCount = analysis.VertexCount,
            CountOffset = analysis.CountOffset,
            PaddingOffset = analysis.PaddingOffset,
            EndOffset = analysis.EndOffset,
            PaddingAndNextBytes = analysis.PaddingAndNextBytes,
            Sha256 = analysis.Sha256
        };
        public TdbinAnalysis ToAnalysis() => new(Version, VertexCount, CountOffset, PaddingOffset, EndOffset, PaddingAndNextBytes, Sha256, null);
    }

    private sealed class HeaderReader(BinaryReader reader)
    {
        public string ReadCString() { var s = new StringBuilder(); while (true) { var b = reader.ReadByte(); if (b == 0) return s.ToString(); if (s.Length >= 1_048_576) throw new InvalidDataException(); s.Append((char)b); } }
        public int ReadCount(int max) { var v = reader.ReadUInt32(); if (v > max) throw new InvalidDataException("Invalid TDBIN count."); return (int)v; }
        public void SkipFloats(int n) => Skip(checked(n * 4)); public void SkipUInt32s(int n) => Skip(checked(n * 4));
        public void SkipTags(int n) { for (var i = 0; i < n; i++) { _ = ReadCString(); _ = ReadCString(); } }
        public void SkipPlayers(int tools) { var n = ReadCount(32); SkipUInt32s(n); for (var i = 0; i < n; i++) { SkipFloats(23); SkipUInt32s(5); SkipFloats(2); SkipUInt32s(3); for (var t = 0; t < tools; t++) SkipTool(); var mods = ReadCount(10_000); for (var t = 0; t < mods; t++) { SkipTool(); _ = ReadCString(); _ = ReadCString(); SkipUInt32s(1); } _ = ReadCString(); } }
        public void SkipEnvironment() { _ = ReadCString(); SkipFloats(21); Skip(1); SkipFloats(6); SkipFloats(3); Skip(1); SkipFloats(9); SkipFloats(4); Skip(1); _ = ReadCString(); SkipFloats(3); SkipFloats(6); Skip(1); SkipFloats(4); _ = ReadCString(); }
        private void SkipTool() { Skip(1); _ = ReadCString(); _ = ReadCString(); SkipFloats(7); if (reader.ReadSingle() > 0) SkipUInt32s(1); }
        private void Skip(int n) { if (n < 0) throw new InvalidDataException(); var b = new byte[Math.Min(n, 64 * 1024)]; while (n > 0) { var r = reader.Read(b, 0, Math.Min(b.Length, n)); if (r == 0) throw new EndOfStreamException(); n -= r; } }
    }

    private sealed class CountingReadStream(Stream inner) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); _position += read; return read; }
        public override int Read(Span<byte> buffer) { var read = inner.Read(buffer); _position += read; return read; }
        public override int ReadByte() { var value = inner.ReadByte(); if (value >= 0) _position++; return value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
