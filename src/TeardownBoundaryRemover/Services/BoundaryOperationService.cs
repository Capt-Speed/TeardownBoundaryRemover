namespace TeardownBoundaryRemover.Services;

internal sealed record OperationResult(int ModifiedFiles, int RemovedBoundaries, BackupSession Session);

internal static class BoundaryOperationService
{
    public static List<PreflightProblem> Preflight(IReadOnlyCollection<ModItem> items)
    {
        var problems = new List<PreflightProblem>();

        foreach (var file in TargetFiles(items))
        {
            try
            {
                if (!File.Exists(file.Path)) { problems.Add(new PreflightProblem { Path = file.Path, Message = Loc.T("文件已不存在", "The file no longer exists") }); continue; }
                if (File.GetAttributes(file.Path).HasFlag(FileAttributes.ReadOnly)) { problems.Add(new PreflightProblem { Path = file.Path, Message = Loc.T("文件是只读属性", "The file has the read-only attribute") }); continue; }
                if (!HashUtil.Sha256File(file.Path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) { problems.Add(new PreflightProblem { Path = file.Path, Message = Loc.T("文件在扫描后发生变化，请重新扫描", "The file changed after scanning; please rescan") }); continue; }
                if (XmlBoundaryService.CountBoundaryNodes(file.Path) != file.BoundaryCount) { problems.Add(new PreflightProblem { Path = file.Path, Message = Loc.T("可处理的 <boundary> 数量在扫描后发生变化，请重新扫描", "The eligible <boundary> count changed after scanning; please rescan") }); continue; }
                using var writeProbe = new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                problems.Add(new PreflightProblem { Path = file.Path, Message = ex.Message });
            }
        }
        return problems;
    }

    public static OperationResult Execute(IReadOnlyCollection<ModItem> items, BackupSession session, IProgress<string>? progress = null)
    {
        var entriesByOriginal = session.Manifest.Entries.ToDictionary(x => x.OriginalPath, StringComparer.OrdinalIgnoreCase);
        var modified = new List<BackupEntry>();
        var removedTotal = 0;
        try
        {
            foreach (var file in TargetFiles(items))
            {
                progress?.Report(Loc.T("正在验证并修改：", "Verifying and modifying: ") + file.Path);
                if (!entriesByOriginal.TryGetValue(file.Path, out var backupEntry))
                    throw new InvalidOperationException(Loc.T("内部安全检查失败：没有找到对应备份。", "Internal safety check failed: the matching backup was not found."));

                var result = XmlBoundaryService.RemoveBoundaryToOriginal(file.Path, file.Sha256);
                backupEntry.ModifiedSha256 = result.NewSha256;
                backupEntry.RemovedBoundaries = result.RemovedCount;
                modified.Add(backupEntry);
                removedTotal += result.RemovedCount;
                BackupManager.SaveManifest(session); // Persist progress so crash recovery can make an informed decision.

                var after = XmlBoundaryService.Analyze(file.Path);
                if (!string.IsNullOrWhiteSpace(after.Error) || after.BoundaryCount != 0)
                    throw new InvalidOperationException(Loc.T("写回后的 XML 验证失败：", "Post-write XML validation failed: ") + file.Path);
            }
            session.Manifest.Status = "Completed";
            BackupManager.SaveManifest(session);
            return new OperationResult(modified.Count, removedTotal, session);
        }
        catch
        {
            foreach (var entry in modified.AsEnumerable().Reverse())
            {
                try
                {
                    if (entry.ModifiedSha256 is not null && File.Exists(entry.OriginalPath) &&
                        HashUtil.Sha256File(entry.OriginalPath).Equals(entry.ModifiedSha256, StringComparison.OrdinalIgnoreCase))
                        File.Copy(entry.BackupPath, entry.OriginalPath, overwrite: true);
                }
                catch (IOException)
                {
                    // Continue restoring the remaining files. The verified backup is retained
                    // for manual recovery when an individual target cannot be restored here.
                }
                catch (UnauthorizedAccessException)
                {
                    // Continue restoring the remaining files. The verified backup is retained
                    // for manual recovery when an individual target cannot be restored here.
                }
            }
            session.Manifest.Status = "RolledBack";
            BackupManager.SaveManifest(session);
            throw;
        }
    }

    private static List<XmlFileEntry> TargetFiles(IReadOnlyCollection<ModItem> items) => items
        .SelectMany(x => x.XmlFiles.Where(f => f.BoundaryCount > 0))
        .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .ToList();
}
