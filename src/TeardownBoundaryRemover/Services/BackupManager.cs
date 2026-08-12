using System.Text.Json;

namespace TeardownBoundaryRemover.Services;

internal sealed class BackupManifest
{
    public int Version { get; set; } = 2;
    public string SessionId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string Status { get; set; } = "BackedUp";
    public List<BackupEntry> Entries { get; set; } = [];
}

internal sealed class BackupEntry
{
    public string ItemName { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string? WorkshopId { get; set; }
    public string OriginalPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public string OriginalSha256 { get; set; } = string.Empty;
    public string? ModifiedSha256 { get; set; }
    public int RemovedBoundaries { get; set; }
}

internal sealed class BackupSession
{
    public required string DirectoryPath { get; init; }
    public required string ManifestPath { get; init; }
    public required BackupManifest Manifest { get; init; }
}

internal static class BackupManager
{
    private static readonly string BackupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Teardown Boundary Remover", "Backups");
    public static string GetBackupRoot() => BackupRoot;

    public static BackupSession CreateSession(IReadOnlyCollection<ModItem> items)
    {
        Directory.CreateDirectory(BackupRoot);
        var sessionId = DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8];
        var sessionDir = Path.Combine(BackupRoot, sessionId);
        Directory.CreateDirectory(sessionDir);
        var manifest = new BackupManifest { SessionId = sessionId, CreatedUtc = DateTime.UtcNow, Status = "BackingUp" };
        var manifestPath = Path.Combine(sessionDir, "manifest.json");
        try
        {
            var index = 0;
            foreach (var pair in TargetFiles(items))
            {
                index++;
                var item = items.First(i => i.XmlFiles.Any(x => x.Path.Equals(pair.Path, StringComparison.OrdinalIgnoreCase)));
                var backupPath = Path.Combine(sessionDir, "files", $"{index:D4}_{SanitizeFileName(item.Name)}", SafeRelativePath(item.RootPath, pair.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(pair.Path, backupPath, overwrite: false);
                if (!HashUtil.Sha256File(backupPath).Equals(pair.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(Loc.T("备份校验失败，文件哈希不一致：", "Backup verification failed; file hash mismatch: ") + pair.Path);
                manifest.Entries.Add(new BackupEntry { ItemName = item.Name, SourceType = item.SourceLabel, WorkshopId = item.WorkshopId, OriginalPath = pair.Path, BackupPath = backupPath, OriginalSha256 = pair.Sha256 });
                SaveManifest(manifestPath, manifest);
            }
            manifest.Status = "BackedUp";
            SaveManifest(manifestPath, manifest);
            return new BackupSession { DirectoryPath = sessionDir, ManifestPath = manifestPath, Manifest = manifest };
        }
        catch
        {
            manifest.Status = "BackupFailed";
            TrySaveManifest(manifestPath, manifest);
            throw;
        }
    }

    public static void SaveManifest(BackupSession session) => SaveManifest(session.ManifestPath, session.Manifest);

    public static BackupSession? GetLatestRestorableSession()
    {
        if (!Directory.Exists(BackupRoot)) return null;
        foreach (var dir in Directory.EnumerateDirectories(BackupRoot).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(dir, "manifest.json");
            if (!File.Exists(path)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path));
                if (manifest is not null && manifest.Entries.Count > 0 &&
                    manifest.Status is ("Completed" or "RolledBack" or "BackedUp" or "CancelledAfterBackup"))
                    return new BackupSession { DirectoryPath = dir, ManifestPath = path, Manifest = manifest };
            }
            catch { }
        }
        return null;
    }

    public static void Restore(BackupSession session)
    {
        foreach (var entry in session.Manifest.Entries)
        {
            if (!File.Exists(entry.BackupPath)) throw new FileNotFoundException(Loc.T("找不到备份文件。", "Backup file not found."), entry.BackupPath);
            if (!HashUtil.Sha256File(entry.BackupPath).Equals(entry.OriginalSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException(Loc.T("备份文件哈希校验失败：", "Backup file hash verification failed: ") + entry.BackupPath);
            if (!File.Exists(entry.OriginalPath)) throw new IOException(Loc.T("原始位置的文件已不存在；为避免创建或覆盖错误目标，已停止恢复：", "The original file location no longer exists. Restore was stopped to avoid creating or overwriting the wrong target: ") + entry.OriginalPath);
            var currentHash = HashUtil.Sha256File(entry.OriginalPath);
            if (!currentHash.Equals(entry.OriginalSha256, StringComparison.OrdinalIgnoreCase) &&
                (entry.ModifiedSha256 is null || !currentHash.Equals(entry.ModifiedSha256, StringComparison.OrdinalIgnoreCase)))
                throw new IOException(Loc.T("当前文件不是此工具写入的版本；为避免覆盖外部修改，已停止恢复：", "The current file is not the version written by this application. Restore was stopped to avoid overwriting an external change: ") + entry.OriginalPath);
        }
        try
        {
            foreach (var entry in session.Manifest.Entries)
            {
                var currentHash = HashUtil.Sha256File(entry.OriginalPath);
                if (currentHash.Equals(entry.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (entry.ModifiedSha256 is null || !currentHash.Equals(entry.ModifiedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(Loc.T("当前文件在恢复前发生变化；为避免覆盖外部修改，已停止恢复：", "The current file changed before restore. Restore was stopped to avoid overwriting an external change: ") + entry.OriginalPath);
                ReplaceFromBackupAtomically(entry);
                if (!HashUtil.Sha256File(entry.OriginalPath).Equals(entry.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(Loc.T("恢复后校验失败：", "Post-restore verification failed: ") + entry.OriginalPath);
            }
            session.Manifest.Status = "Restored";
            SaveManifest(session);
        }
        catch
        {
            session.Manifest.Status = "RestoreFailed";
            TrySaveManifest(session.ManifestPath, session.Manifest);
            throw;
        }
    }

    private static List<XmlFileEntry> TargetFiles(IReadOnlyCollection<ModItem> items) => items
        .SelectMany(x => x.XmlFiles.Where(file => file.BoundaryCount > 0))
        .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToList();

    private static string SafeRelativePath(string root, string file)
    {
        try
        {
            var relative = Path.GetRelativePath(root, file);
            if (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative))
            {
                return relative;
            }
        }
        catch (ArgumentException)
        {
            // Fall back to the file name when the two paths cannot be related safely.
        }

        return Path.GetFileName(file);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return safe.Length switch
        {
            0 => "item",
            > 80 => safe[..80],
            _ => safe
        };
    }
    private static void ReplaceFromBackupAtomically(BackupEntry entry)
    {
        var tempPath = entry.OriginalPath + ".tbr-restore-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(entry.BackupPath, tempPath, overwrite: false);
            File.Replace(tempPath, entry.OriginalPath, null, ignoreMetadataErrors: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (IOException)
            {
                // The restored target is already complete; a stale temporary file is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // The restored target is already complete; a stale temporary file is harmless.
            }
        }
    }

    private static void SaveManifest(string path, BackupManifest manifest)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    private static void TrySaveManifest(string path, BackupManifest manifest)
    {
        try
        {
            SaveManifest(path, manifest);
        }
        catch (IOException)
        {
            // Best effort only: the original operation error remains the actionable failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort only: the original operation error remains the actionable failure.
        }
    }
}
