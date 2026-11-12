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

        try
        {
            using var document = JetJsonStorage.Parse(summaryJson);
            var root = document.RootElement;
            ValidationSummaryShapeValidator.RequirePrescreen(root);

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
            // 完整人數。摘要頁照舊版原句不寫人數；新的預篩選結果一定帶這個數字，不適用時是 null，這裡讀成 0。
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
                itemCount: RuleCount(root, "blankDescription", "count")))
        {
            ZerosThreshold = ReadZerosThreshold(root)
        };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new JetActionException(JetErrorCodes.StaleResult,
                "這份報告依據的結果已不是目前資料的結果。請回到「資料驗證與測試」按「重新執行預篩選」，再回來產生報告。",
                innerException: exception);
        }
    }

    /// <summary>預篩選的回應一定寫下這次用的 0 的個數；讀不到或超出範圍就是保存的結果不完整，明確丟出。</summary>
    private static int ReadZerosThreshold(JsonElement root)
    {
        var zeros = RuleCount(root, "trailingZeros", "zerosThreshold");
        return zeros is >= TrailingZeroThreshold.MinCustomDigits and <= TrailingZeroThreshold.MaxCustomDigits
            ? (int)zeros
            : throw new InvalidOperationException("保存的預篩選結果沒有尾數 0 的個數。");
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
