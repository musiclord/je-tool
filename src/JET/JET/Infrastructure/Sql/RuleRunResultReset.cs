using System.Data.Common;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 規則結果(result_rule_run / result_inf_sampling_test_sample / result_filter_run)失效的共用技術執行點。
/// 失效矩陣的唯一政策來源是 Domain <see cref="AuditDependencyPolicy"/>；本類只把政策結果翻成
/// stale-state UPDATE 與既有 DELETE statements。只有真正存在、即將被清除的結果會把對應
/// stale flag 置 true；「從未執行」維持 false。
/// 這些是衍生資料:任何改寫其上游的交易,必須在
/// 「同一交易」內呼叫本方法清除舊結果,確保不出現「資料已換、舊結果還在」的中間態——
/// 上游若 rollback,stale 標記與清除亦一併回退。清空後 project.load 的 latestRuns 自然回 null,
/// 並以持久化 stale state 區分「要求重跑」與「從未執行」。
/// (DELETE 為 ANSI 共通,SQLite 與 SQL Server 路徑共用同一不變量。)
/// 失效範圍依真實依賴裁切：GL 影響全部；TB 只影響 validation；科目配對、行事曆與授權清單
/// 只影響 prescreen/filter。不得為了方便一律清空，否則會破壞「驗證→填 AccountMapping→預篩選」流程。
///
/// 注意:part(a) 控制總數 <c>gl_control_total</c> **不**在此清除範圍。它的上游只有 GL target,
/// 由 GL 投影(<see cref="LocalGlRepository"/>/<see cref="SqlServerGlRepository"/>)在同一交易內隨
/// target 一起 upsert 覆寫,與 target_gl_entry 恆一致。若併入本共用清除,TB 投影、科目配對／行事曆／
/// 授權清單匯入等與 GL 無關的寫入會把它連帶刪掉,使完整性 part(a) 在常見的「先 commit GL、後 commit TB」
/// 順序下變成全 null（控制總數核對形同沒跑）——2026-06-22 實務稽核發現的失效範圍過廣，已收斂。
/// </summary>
internal static class RuleRunResultReset
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

        var statements = new List<string>(3);
        if (impact.InvalidateValidation && impact.InvalidatePrescreen)
        {
            statements.Add($"DELETE FROM {schemaPrefix}result_rule_run;");
        }
        else if (impact.InvalidateValidation)
        {
            statements.Add($"DELETE FROM {schemaPrefix}result_rule_run WHERE run_kind = 'validate';");
        }
        else if (impact.InvalidatePrescreen)
        {
            statements.Add($"DELETE FROM {schemaPrefix}result_rule_run WHERE run_kind = 'prescreen';");
        }

        if (impact.InvalidateValidation)
        {
            statements.Add($"DELETE FROM {schemaPrefix}result_inf_sampling_test_sample;");
        }

        if (impact.InvalidateFilterHits)
        {
            statements.Add($"DELETE FROM {schemaPrefix}result_filter_run;");
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
