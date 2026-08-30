using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 把 result_rule_run 保存的 prescreen wire summary 重建成 report writer 所需的
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
            PostPeriodApproval: Rule(
                root,
                "postPeriodApproval",
                itemCount: RuleCount(root, "postPeriodApproval", "count")),
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
            CreatorSummary: Rule(
                root,
                "creatorSummary",
                itemCount: ArrayLength(root, "creatorSummary", "creators")),
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

    private static long ArrayLength(
        JsonElement root,
        string section,
        string property) =>
        root.TryGetProperty(section, out var value)
        && value.TryGetProperty(property, out var array)
        && array.ValueKind == JsonValueKind.Array
            ? array.GetArrayLength()
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
