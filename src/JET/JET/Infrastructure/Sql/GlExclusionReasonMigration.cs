using System.Data.Common;
using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>
/// 本機 schema 第 12 版到第 13 版（SQL Server 第 14 版到第 15 版）：GL(未納入測試) 的日期不符分成總帳日期空白、
/// 早於查核期間與晚於查核期間三種原因（使用者 2026-10-21 裁定）。改版前把三種情況都記成 <c>period</c>，
/// 資料庫也不存查核期間，所以用 GL 的總帳日期判斷：總帳日期空白的列就是總帳日期空白；比最早的 GL 分錄早的是
/// 早於查核期間，其餘是晚於查核期間。確認 GL 欄位配對時沒有任何 GL 分錄會整批退回，所以有 <c>period</c> 列的
/// 資料庫一定有 GL 分錄可以比。GL 的分錄、過帳狀態不符的分錄與控制總數都不動。
/// </summary>
internal static class GlExclusionReasonMigration
{
    internal const string TargetVersion = "13";

    internal static async Task UpgradeLocalAsync(DbConnection connection, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SplitPeriodReasonAsync(connection, transaction, "", ct);
        await using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText = $"UPDATE schema_info SET value = '{TargetVersion}' WHERE key = 'schema_version'";
            await bump.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>prefix 是表名前綴：本機資料庫傳空字串，SQL Server 傳專案 schema 的限定名。重跑沒有副作用。</summary>
    internal static async Task SplitPeriodReasonAsync(
        DbConnection connection, DbTransaction transaction, string prefix, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            UPDATE {prefix}target_gl_entry
            SET exclusion_reason = CASE
                WHEN post_date IS NULL THEN '{GlEffectivePopulation.BlankPostDateStorageReason}'
                WHEN post_date < (SELECT MIN(post_date) FROM {prefix}target_gl_entry WHERE is_effective = 1)
                    THEN '{GlEffectivePopulation.BeforePeriodStorageReason}'
                ELSE '{GlEffectivePopulation.AfterPeriodStorageReason}'
            END
            WHERE exclusion_reason = 'period'
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
}
