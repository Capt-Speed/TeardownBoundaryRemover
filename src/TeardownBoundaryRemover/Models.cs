namespace TeardownBoundaryRemover;

internal enum ContentSourceType
{
    LocalMod,
    Workshop,
    BuiltIn,
    Custom
}

internal enum BuiltInContentKind
{
    None,
    Mission,
    Sandbox,
    Challenge,
    CampaignHub,
    CampaignEnding,
    SystemUi,
    Other
}

internal sealed record ScanOptions(bool BuiltInMaps, bool WorkshopMaps, bool LocalMaps);

internal sealed class XmlFileEntry
{
    public required string Path { get; init; }
    public required string RelativePath { get; init; }
    public required int BoundaryCount { get; init; }
    public required string Sha256 { get; init; }
    public required bool IsLevelLike { get; init; }
    public required bool CanWrite { get; init; }
    public string? WriteError { get; init; }
    public string? Warning { get; init; }
    public string? Error { get; init; }
}

internal sealed class ModItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Name { get; init; }
    public string? Author { get; init; }
    public required string RootPath { get; init; }
    public required ContentSourceType SourceType { get; init; }
    public string? WorkshopId { get; init; }
    public List<XmlFileEntry> XmlFiles { get; } = [];
    public bool Selected { get; set; }
    public string? DiscoveryNote { get; init; }
    public bool IsCompiledBinaryMap { get; init; }
    public string? BinaryPath { get; init; }
    public string? BinarySha256 { get; init; }
    public string? BinaryVersion { get; init; }
    public int BinaryBoundaryVertexCount { get; init; }
    public string? BinaryError { get; init; }
    public BuiltInContentKind BuiltInKind { get; init; }
    public string? ContentCategory { get; init; }
    public string? SourceDetail { get; init; }
    public string? RecognitionBasis { get; init; }
    public string? OriginalFileName { get; init; }

    public int BoundaryCount => XmlFiles.Sum(x => x.BoundaryCount);
    public int LevelXmlCount => XmlFiles.Count(x => x.IsLevelLike);
    public bool HasErrors => XmlFiles.Any(x => !string.IsNullOrWhiteSpace(x.Error));
    public bool HasWriteBlocks => XmlFiles.Any(x => x.BoundaryCount > 0 && !x.CanWrite);
    // A group named "Boundary" is informational only. It is never included in BoundaryCount,
    // so an item containing only such groups stays locked from modification.
    public bool IsXmlSelectable => BoundaryCount > 0 && !HasErrors && !HasWriteBlocks && !IsCompiledBinaryMap;
    public bool IsBinarySelectable => IsCompiledBinaryMap && BinaryVersion == "2.0.4" &&
                                      BinaryBoundaryVertexCount > 0 && string.IsNullOrWhiteSpace(BinaryError) &&
                                      BuiltInKind != BuiltInContentKind.SystemUi &&
                                      !string.IsNullOrWhiteSpace(BinaryPath) && !string.IsNullOrWhiteSpace(BinarySha256);
    public bool IsSelectable => IsXmlSelectable || IsBinarySelectable;
    public int DisplayBoundaryCount => IsCompiledBinaryMap ? BinaryBoundaryVertexCount : BoundaryCount;

    public string SourceLabel => !string.IsNullOrWhiteSpace(SourceDetail) ? SourceDetail : SourceType switch
    {
        ContentSourceType.LocalMod => Loc.T("本地地图 · 文档目录", "Local map · Documents"),
        ContentSourceType.Workshop => Loc.T("创意工坊 · 订阅项目", "Workshop · Subscription"),
        ContentSourceType.BuiltIn => Loc.T("游戏自带 · XML 内容", "Built-in · XML content"),
        ContentSourceType.Custom => Loc.T("本地地图 · 自定义位置", "Local map · Custom location"),
        _ => SourceType.ToString()
    };

    public string Status => HasErrors
        ? Loc.T("XML 异常，已锁定", "Invalid XML; locked")
        : IsCompiledBinaryMap
            ? BuiltInKind == BuiltInContentKind.SystemUi
                ? Loc.T("系统界面，只读", "System UI; read-only")
                : !string.IsNullOrWhiteSpace(BinaryError)
                ? Loc.T("BIN 格式不支持，已锁定", "Unsupported BIN format; locked")
                : BinaryBoundaryVertexCount > 0
                    ? Loc.T($"危险：BIN 边界顶点（{BinaryBoundaryVertexCount}）", $"Danger: BIN boundary vertices ({BinaryBoundaryVertexCount})")
                    : Loc.T("BIN 未发现边界", "No BIN boundary found")
        : HasWriteBlocks
            ? Loc.T("无写权限，已锁定", "No write access; locked")
        : BoundaryCount > 0
            ? Loc.T($"可移除边界（{BoundaryCount}）", $"Ready ({BoundaryCount})")
            : LevelXmlCount > 0
                ? Loc.T("未发现边界", "No Boundary found")
                : Loc.T("未发现关卡 XML", "No level XML found");
}

internal sealed class ScanReport
{
    public List<ModItem> Items { get; } = [];
    public List<string> Locations { get; } = [];
    public List<string> Warnings { get; } = [];
}

internal sealed class PreflightProblem
{
    public required string Path { get; init; }
    public required string Message { get; init; }
}

internal sealed class OperationSummary
{
    public int ItemCount { get; init; }
    public int XmlCount { get; init; }
    public int BoundaryCount { get; init; }
    public bool IncludesWorkshop { get; init; }
    public bool IncludesBuiltIn { get; init; }
}
