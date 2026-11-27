using System.Data.Common;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 上游資料修改時，把受影響的結果標成過期並清除。涵蓋的結果是 validation_result、prescreen_result、inf_sample、
/// filter_hit、filter_hit_condition，以及已存篩選情境 filter_scenario。
/// 哪一種修改影響哪些結果，只由 Domain 的 <see cref="AuditDependencyPolicy"/> 決定；
/// 這裡只把它的結論寫成 result_stale_state 的 UPDATE 與各結果表的 DELETE。
/// 過期標記只在結果真的存在、而且即將被清除時才設成 true；從來沒有執行過的維持 false。
/// 這些結果都是從上游資料算出來的。任何改寫上游資料的交易，都要在同一個交易裡呼叫這裡清除舊結果，
/// 才不會出現資料已經換掉、舊結果卻還在的情形。上游的交易 rollback 時，過期標記與清除也一起回復。
/// 清除之後，project.load 回傳的 validationResult 與 prescreenResult 是 null，
/// 再靠保存下來的過期標記分辨是「需要重新執行」還是「從未執行」。
/// 影響範圍：GL 與 TB 的匯入或確認配對影響全部結果；其餘的修改，例如科目配對、行事曆與授權清單，
/// 只影響預篩選與篩選。不能為了省事一律全部清除，否則使用者先驗證、再填科目配對、然後預篩選時，
/// 驗證結果會在中途被清掉。
/// 篩選命中過期時，已存篩選情境 filter_scenario 也在同一個交易裡刪除；
/// 篩選的過期標記同時回到「從未執行」，因為已經沒有可以重新執行的情境。
///
/// 存入筆數與合計核對的數字 <c>gl_control_total</c> 不在清除範圍內。它只取決於 gl 資料表，
/// 由確認 GL 配對的步驟（<see cref="LocalGlRepository"/>）在同一個交易裡和 gl 一起覆寫，所以永遠和 gl 一致。
/// 如果也放進這裡清除，確認 TB 配對、匯入科目配對、行事曆或授權清單這些和 GL 無關的寫入會把它一起刪掉。
/// 常見的順序是先確認 GL 配對、再確認 TB 配對，那樣存入筆數與合計核對的數字會全部變成 null，等於這一項沒有執行。
/// </summary>
internal static class ResultReset
{
    internal static async Task ClearWithinAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken,
        AuditMutation mutation,
        string schemaPrefix = "")
    {
        var impact = AuditDependencyPolicy.For(mutation);
        await ResultStaleStateSql.MarkInvalidatedWithinAsync(
            connection,
            transaction,
            impact,
            cancellationToken,
            schemaPrefix);

        var statements = new List<string>(5);
        if (impact.ClearAccountMapping)
        {
            // GL 或 TB 重新匯入或重新確認配對時，科目配對、匯入的原始配對列與它的匯入批次紀錄一起清除；科目分類的定義保留。
            // 清除和來源資料的寫入在同一個交易裡，失敗或取消時全部回復。
            var kind = DatasetKind.AccountMapping.ToStorageName();
            statements.Add($"DELETE FROM {schemaPrefix}account_mapping;");
            statements.Add($"DELETE FROM {schemaPrefix}pbc_account_mapping;");
            statements.Add($"DELETE FROM {schemaPrefix}import_batch_source WHERE batch_id IN "
                + $"(SELECT batch_id FROM {schemaPrefix}import_batch WHERE dataset_kind = '{kind}');");
            statements.Add($"DELETE FROM {schemaPrefix}import_batch WHERE dataset_kind = '{kind}';");
        }
        if (impact.InvalidateValidation)
        {
            statements.Add($"DELETE FROM {schemaPrefix}validation_result;");
            statements.Add($"DELETE FROM {schemaPrefix}inf_sample;");
        }

        if (impact.InvalidatePrescreen)
        {
            statements.Add($"DELETE FROM {schemaPrefix}prescreen_result;");
        }

        if (impact.InvalidateFilterHits)
        {
            statements.AddRange(FilterHitInsertSql.DeleteAll(schemaPrefix));
            if (impact.InvalidateFilterScenarioDefinitions)
            {
                // 上游修改清除下游：命中先清，再清全部已存情境定義。
                // 兩表之間沒有外鍵，順序只為讓讀者看出命中依附情境。
                statements.Add($"DELETE FROM {schemaPrefix}filter_scenario;");
            }

            var keyColumn = schemaPrefix.Length == 0 ? "key" : "[key]";
            statements.Add($"UPDATE {schemaPrefix}schema_info SET value = CAST(CAST(value AS BIGINT) + 1 AS VARCHAR(40)) "
                + $"WHERE {keyColumn} = 'filter_data_revision';");
        }

        if (statements.Count == 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Join(Environment.NewLine, statements);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
