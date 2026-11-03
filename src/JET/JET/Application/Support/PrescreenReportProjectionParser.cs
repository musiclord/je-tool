using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 把 prescreen_result 保存的 prescreen wire summary 重建成 report writer 所需的
/// internal typed projection。Wire key 的解讀留在 Application；projection 不攜帶
/// 完整 GL 列集。
/// </summary>
internal static class PrescreenReportProjectionParser
{
    internal static PrescreenReportProjection Parse(string summaryJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryJson);

        using var document = JsonDocument.Parse(summaryJson);
        var root = document.RootElement;

        return new PrescreenReportProjection(
            PrepPeriodApproval: Rule(
                root,
                "prepPeriodApproval",
                itemCount: RuleCount(root, "prepPeriodApproval", "count")),
            SuspiciousKeywords: Rule(
                root,
                "suspiciousKeywords",
                itemCount: RuleCount(root, "suspiciousKeywords", "count")),
            UnexpectedAccountPair: Rule(
                root,
                "unexpectedAccountPair",
                itemCount: RuleCount(root, "unexpectedAccountPair", "count")),
            TrailingZeros: Rule(
                root,
                "trailingZeros",
                itemCount: RuleCount(root, "trailingZeros", "count")),
            // 摘要列寫完整人數，和第四步、[R5] 工作表一致。新的預篩選結果一定帶這個數字，
            // 只為舊結果留下的「改用清單列數」已拿掉（裁定 2026-11-02-06）；這項不適用時人數是 null，摘要列照舊寫 0。
            PreparerSummary: Rule(
                root,
                "preparerSummary",
                itemCount: RuleCount(root, "preparerSummary", "totalPreparerCount")),
            RareAccounts: Rule(
                root,
                "rareAccounts",
                itemCount: RuleCount(root, "rareAccounts", "distinctAccountCount")),
            BlankDescription: Rule(
                root,
                "blankDescription",
                itemCount: RuleCount(root, "blankDescription", "count")));
    }

    private static PrescreenReportRuleProjection Rule(
        JsonElement root,
        string section,
        long itemCount = 0)
    {
        if (!root.TryGetProperty(section, out var value))
        {
            return new PrescreenReportRuleProjection("N/A", null, itemCount);
        }

        return new PrescreenReportRuleProjection(
            Status: GetString(value, "status") ?? "N/A",
            NaReason: GetString(value, "naReason"),
            ItemCount: itemCount);
    }

    private static long RuleCount(
        JsonElement root,
        string section,
        string property) =>
        root.TryGetProperty(section, out var value)
            ? GetLong(value, property)
            : 0;

    private static long GetLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            ? number
            : 0;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
