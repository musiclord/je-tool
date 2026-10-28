using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 提交後的「必填文字欄整欄空白」偵測：把投影後 target 中整欄皆空（NULL 或 trim 後空字串）的
/// 必填文字欄，轉成可行動的非阻斷提交警示（疑似配錯欄）。
///
/// 動機：來源活頁簿可能有兩個都叫「摘要」的欄（第一個整欄空、第二個才有
/// 內容，正規化後成「摘要_2」）。description 若配到空的那欄，blank_description 會對全列誤命中，
/// 而症狀要到資料驗證才浮現。此提醒在「欄位配對提交」當下就點出哪個必填欄空、配到哪個來源欄。
/// 金額欄不在此（退化母體守門已擋全 0）、日期欄不在此（期外日期另計）。純呈現，不阻斷投影。
/// 欄位清單與查詢都由欄位目錄產生，本機與 SQL Server 的 GL 倉儲共用，和 TB 的 <see cref="TbMappedColumnAudit"/> 同一個寫法。
/// </summary>
internal static class GlMappedColumnAudit
{
    // (必填文字欄 fieldKey, target 欄名, 使用者可懂的欄位名)。顯示順序即此順序。
    internal static readonly (string FieldKey, string TargetColumn, string FriendlyName)[] RequiredTextColumns =
        JetFieldCatalog.GlFields
            .Where(static field => field.Kind == JetFieldValueKind.Text)
            .SelectMany(static field => field.MappingSlots.Select(slot => (Field: field, Slot: slot)))
            .Where(static item => item.Slot.IsAlwaysRequired)
            .OrderBy(static item => item.Slot.Order)
            .Select(static item => (
                item.Slot.Key,
                item.Field.SemanticSqlTarget,
                item.Slot.Label))
            .ToArray();

    /// <summary>
    /// 在確認配對的同一個交易裡，查投影後整欄空白的必填文字欄，回傳每欄的提醒（含使用者實際所配的來源欄名）。
    /// 空母體不偵測：沒有資料可判，避免把「沒資料」誤報成每一欄都配錯。計數轉成 BIGINT，避免大母體溢位。
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ReadAsync(DbConnection connection, DbTransaction transaction,
        GlMappingSpec spec, long rowCount, IProviderSqlDialect dialect, CancellationToken ct, string prefix = "")
    {
        if (rowCount == 0 || RequiredTextColumns.Length == 0) return [];
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + string.Join(", ", RequiredTextColumns.Select(column =>
            $"CAST(SUM(CASE WHEN {SqlBlankText.IsNotBlank(dialect, column.TargetColumn)} THEN CAST(1 AS BIGINT) ELSE CAST(0 AS BIGINT) END) AS BIGINT)"))
            + $" FROM {prefix}target_gl_entry;";
        var warnings = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return warnings;
        for (var index = 0; index < RequiredTextColumns.Length; index++)
        {
            if (Convert.ToInt64(reader.GetValue(index)) != 0) continue;
            var (fieldKey, _, friendly) = RequiredTextColumns[index];
            warnings.Add(BlankColumnWarning(friendly, spec.Mapping.GetValueOrDefault(fieldKey)));
        }
        return warnings;
    }

    /// <summary>
    /// GL 與 TB 共用的整欄空白提醒句子（使用者 2026-10-13 勾選頁確認）；有來源欄名時一併寫出。
    /// </summary>
    internal static string BlankColumnWarning(string friendlyName, string? sourceColumn) =>
        string.IsNullOrWhiteSpace(sourceColumn)
            ? $"「{friendlyName}」整欄空白，請確認對應的來源欄是否正確。"
            : $"「{friendlyName}」對應的來源欄「{sourceColumn}」整欄空白，請確認對應是否正確。";
}
