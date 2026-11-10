using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 把 validation_result 保存的 validation wire summary 重建成 report writer 所需的
/// internal typed projection。Wire key 的解讀留在 Application；projection 不攜帶
/// 完整 GL／TB 列集。
/// </summary>
internal static class ValidationReportProjectionParser
{
    internal static ValidationReportProjection Parse(string summaryJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryJson);

        try
        {
            using var document = JetJsonStorage.Parse(summaryJson);
            var root = document.RootElement;
            ValidationSummaryShapeValidator.Require(root);
            var stats = root.GetProperty("stats");
            var blankRecords = root.GetProperty("blankRecordsTest");
            var voucherBalance = root.GetProperty("voucherBalanceTest");
            var sourceQuality = root.GetProperty("sourceQuality");

            return new ValidationReportProjection(
                Net: GetDecimal(stats, "net"),
                TotalDebit: GetDecimal(stats, "totalDebit"),
                TotalCredit: GetDecimal(stats, "totalCredit"),
                GlRowCount: GetLong(stats, "glRowCount"),
                CompletenessDiffAccountCount: RuleCount(
                    root,
                    "completenessTest",
                    "diffAccountCount"),
                UnbalancedVoucherCount: GetLong(
                    voucherBalance,
                    "unbalancedVoucherCount"),
                BlankAccountNumberCount: GetLong(blankRecords, "blankAccountNumberCount"),
                BlankVoucherNumberCount: GetLong(blankRecords, "blankVoucherNumberCount"),
                BlankDescriptionCount: GetLong(blankRecords, "blankDescriptionCount"),
                // 形狀檢查已確認筆數與原因恰好一個有值；筆數是 null 就是傳票核准日不可用。
                ApprovalDateNotInPeriodCount: blankRecords.GetProperty("approvalDateNotInPeriodCount").ValueKind == JsonValueKind.Null
                    ? null
                    : GetLong(blankRecords, "approvalDateNotInPeriodCount"),
                SourceQualityFindingCount: GetLong(sourceQuality, "findingCount"),
                CompletenessNaNote: JET.AuditCore.AccountComparisonProcedure.ReportNote(CompletenessNaReason(root)));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "資料驗證結果不完整，請重新執行資料驗證後再產生報告。",
                innerException: exception);
        }
    }

    private static string? CompletenessNaReason(JsonElement root) =>
        root.TryGetProperty("completenessTest", out var completeness)
        && completeness.TryGetProperty("naReason", out var reason)
        && reason.ValueKind == JsonValueKind.String
            ? reason.GetString()
            : null;

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

    private static decimal GetDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDecimal(out var number))
        {
            return 0;
        }

        return number;
    }
}
