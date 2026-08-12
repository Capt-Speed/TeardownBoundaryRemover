using System.Globalization;
using System.Text.RegularExpressions;

namespace TeardownBoundaryRemover.Services;

internal sealed record BuiltInMapIdentity(string DisplayName, BuiltInContentKind Kind, string Category, string SourceDetail, string RecognitionBasis, string OriginalFileName);

internal static partial class BuiltInMapCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["carib"] = "Isla Estocastica",
        ["caveisland"] = "Hollowrock Island",
        ["cullington"] = "Cullington",
        ["factory"] = "Quilez Security",
        ["frustrum"] = "Frustrum",
        ["lee"] = "Lee Chemicals",
        ["mall"] = "The Evertides Mall",
        ["mansion"] = "Villa Gordon",
        ["marina"] = "West Point Marina"
    };

    public static BuiltInMapIdentity Identify(string path)
    {
        var file = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (stem == "about") return SystemIdentity(file, Loc.T("关于界面", "About screen"));
        if (stem == "menu") return SystemIdentity(file, Loc.T("主菜单场景", "Main menu scene"));

        var challenge = ChallengeRegex().Match(stem);
        if (challenge.Success && Locations.TryGetValue(challenge.Groups[1].Value, out var challengeLocation))
        {
            var mode = Title(challenge.Groups[2].Value);
            return Make(file, $"{challengeLocation} · {Loc.T("挑战", "Challenge")} · {mode}", BuiltInContentKind.Challenge,
                Loc.T("挑战模式", "Challenge mode"), Loc.T($"挑战 · {challengeLocation}", $"Challenge · {challengeLocation}"),
                Loc.T($"依据原始文件名：ch_ 表示挑战系列，{challenge.Groups[1].Value} 表示地图家族，{challenge.Groups[2].Value} 表示挑战模式。", $"Based on the original filename: ch_ identifies the challenge series, {challenge.Groups[1].Value} the map family, and {challenge.Groups[2].Value} the challenge mode."));
        }

        var hub = HubRegex().Match(stem);
        if (hub.Success)
        {
            var variant = hub.Groups[1].Value;
            return Make(file, $"{Loc.T("战役中枢", "Campaign Hub")} · {variant}", BuiltInContentKind.CampaignHub, Loc.T("战役中枢/阶段变体", "Campaign hub/stage variant"),
                Loc.T("战役中枢", "Campaign hub"),
                Loc.T("依据原始文件名 hub + 数字分类；本地文件未提供可验证的独立正式关卡名，因此保留变体编号。", "Classified from the hub + number filename. The local file provides no verifiable standalone official level name, so the variant number is retained."));
        }

        var ending = EndingRegex().Match(stem);
        if (ending.Success)
            return Make(file, $"{Loc.T("战役结局", "Campaign Ending")} · {ending.Groups[1].Value}", BuiltInContentKind.CampaignEnding, Loc.T("战役结局变体", "Campaign ending variant"),
                Loc.T("战役结局", "Campaign ending"),
                Loc.T("依据原始文件名 ending + 数字分类；保留原始变体编号，避免猜测剧情名称。", "Classified from the ending + number filename; the original variant number is retained rather than guessing a story title."));

        if (stem == "hub_carib_sandbox")
            return Make(file, $"Isla Estocastica · {Loc.T("中枢沙盒", "Hub Sandbox")}", BuiltInContentKind.Sandbox, Loc.T("沙盒", "Sandbox"),
                Loc.T("沙盒 · Isla Estocastica", "Sandbox · Isla Estocastica"), Loc.T("依据原始文件名中的 carib、hub、sandbox 标识。", "Based on the carib, hub, and sandbox identifiers in the original filename."));

        foreach (var pair in Locations.OrderByDescending(x => x.Key.Length))
        {
            var prefix = pair.Key + "_";
            if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var variant = stem[prefix.Length..];
            var isSandbox = variant.Equals("sandbox", StringComparison.OrdinalIgnoreCase);
            var category = isSandbox ? Loc.T("沙盒", "Sandbox") : Loc.T("战役任务", "Campaign mission");
            var displayVariant = isSandbox ? category : Title(variant);
            return Make(file, $"{pair.Value} · {category}" + (isSandbox ? string.Empty : $" · {displayVariant}"), isSandbox ? BuiltInContentKind.Sandbox : BuiltInContentKind.Mission, category,
                isSandbox ? Loc.T($"沙盒 · {pair.Value}", $"Sandbox · {pair.Value}") : Loc.T($"任务 · {pair.Value}", $"Mission · {pair.Value}"),
                Loc.T($"依据原始文件名：{pair.Key} 表示地图家族，{variant} 表示内容变体；未猜测未经本机文件证明的正式任务译名。", $"Based on the original filename: {pair.Key} identifies the map family and {variant} the content variant. No unverified official mission translation is guessed."));
        }

        return Make(file, Title(stem), BuiltInContentKind.Other, Loc.T("未归入已知地图家族", "Unmapped map family"), Loc.T("其他原生内容", "Other built-in content"),
            Loc.T("来自游戏 data\\bin；未匹配已验证的地图家族规则，显示规范化文件名。", "Located in the game's data\\bin folder; no verified map-family rule matched, so a normalized filename is shown."));
    }

    private static BuiltInMapIdentity SystemIdentity(string file, string name) => Make(file, name, BuiltInContentKind.SystemUi, Loc.T("系统界面", "System UI"),
        Loc.T("系统界面", "System UI"), Loc.T("依据 data\\bin 中的系统文件名分类，不作为普通地图名称。", "Classified from the system filename in data\\bin; it is not presented as a normal map."));
    private static BuiltInMapIdentity Make(string file, string display, BuiltInContentKind kind, string category, string source, string basis) => new(display, kind, category, source, basis, file);
    private static string Title(string value) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' ').Replace('-', ' '));

    [GeneratedRegex("^ch_([a-z0-9]+)_([a-z0-9]+)$", RegexOptions.IgnoreCase)] private static partial Regex ChallengeRegex();
    [GeneratedRegex("^hub(\\d+)$", RegexOptions.IgnoreCase)] private static partial Regex HubRegex();
    [GeneratedRegex("^ending(\\d+)$", RegexOptions.IgnoreCase)] private static partial Regex EndingRegex();
}
