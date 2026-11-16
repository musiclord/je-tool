using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 確認 GL 配對後，找出 gl 資料表裡整欄空白的必填文字欄，回傳提醒文字。整欄空白是指每一列都是 NULL，
/// 或去掉前後空白後是空字串，多半是配錯了來源欄。提醒只是顯示給審計員看，不會擋下確認配對。
///
/// 需要這個提醒的原因：來源活頁簿可能有兩個都叫「摘要」的欄，第一個整欄空白，第二個才有內容，
/// 欄名正規化後第二個叫「摘要_2」。description 如果配到空白的那一欄，blank_description 會把每一列都算成空白，
/// 而這個問題要到資料驗證才看得出來。這個提醒在確認欄位配對的當下就指出哪個必填欄是空白的、配到了哪個來源欄。
/// 金額欄不在這裡檢查：借方與貸方總額都是 0 時，確認配對會直接失敗。日期欄也不在這裡檢查：期外日期另外計算。
/// 欄位清單與查詢都由欄位目錄產生，GL 倉儲直接使用，和 TB 的 <see cref="TbMappedColumnAudit"/> 同一個寫法。
/// </summary>
internal static class GlMappedColumnAudit
{
    // 每一項是必填文字欄的 fieldKey、它在 gl 資料表的欄名，以及畫面上的欄位名稱。提醒依這個順序列出。
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
    /// 在確認配對的同一個交易裡，查 gl 中整欄空白的必填文字欄，回傳每一欄的提醒，提醒裡寫出實際配到的來源欄名。
    /// gl 沒有資料時不檢查，否則會把沒有資料誤報成每一欄都配錯。計數轉成 BIGINT，資料量大時才不會溢位。
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ReadAsync(DbConnection connection, DbTransaction transaction,
        GlMappingSpec spec, long rowCount, IProviderSqlDialect dialect, CancellationToken ct, string prefix = "")
    {
        if (rowCount == 0 || RequiredTextColumns.Length == 0) return [];
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + string.Join(", ", RequiredTextColumns.Select(column =>
            $"CAST(SUM(CASE WHEN {SqlBlankText.IsNotBlank(dialect, column.TargetColumn)} THEN CAST(1 AS BIGINT) ELSE CAST(0 AS BIGINT) END) AS BIGINT)"))
            + $" FROM {prefix}gl;";
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
    /// GL 與 TB 共用的整欄空白提醒句子；有來源欄名時一併寫出。
    /// </summary>
    internal static string BlankColumnWarning(string friendlyName, string? sourceColumn) =>
        string.IsNullOrWhiteSpace(sourceColumn)
            ? $"「{friendlyName}」整欄空白，請確認對應的來源欄是否正確。"
            : $"「{friendlyName}」對應的來源欄「{sourceColumn}」整欄空白，請確認對應是否正確。";
}
