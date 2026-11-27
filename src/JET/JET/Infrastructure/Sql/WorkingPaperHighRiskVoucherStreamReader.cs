using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 用一個查詢讀完正式工作底稿 step4 的資料。查詢先從已保存的 filter_hit 找出不重複的傳票號碼與情境位置，
/// 再只對命中的傳票彙總它在 GL 的全部分錄；傳票號碼是 NULL 的命中分錄各自輸出一列。
/// 結果由一個只能往前讀的 reader 逐列輸出，不像畫面那樣每 200 列一頁、每一頁都重新掃過整個 GL。
/// </summary>
internal static class WorkingPaperHighRiskVoucherStreamReader
{
    internal static async IAsyncEnumerable<WorkingPaperHighRiskVoucherRow> ReadAsync(
        DbConnection connection,
        ISqlDialect dialect,
        GlPopulationContext context,
        IReadOnlyList<int> scenarioPositions,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string schemaPrefix = "")
    {
        var scenarioScope = HighRiskSummaryScenarioSqlScope.Create(scenarioPositions);
        if (scenarioScope.IsEmpty)
        {
            yield break;
        }

        await using var command = connection.CreateCommand();
        GlPopulationScopeSql.Plan(dialect, context, "g").BindParametersTo(command);
        scenarioScope.AddParameters(command);
        command.CommandText =
            "WITH hit_position AS (" +
            "    SELECT DISTINCT hit.voucher_number, fs.position AS scenario_position, " +
            $"           CASE WHEN {HitVoucherRows.HasNoVoucherNumber("hit")} THEN hit.entry_id ELSE 0 END AS blank_entry_id " +
            $"    FROM {schemaPrefix}filter_hit r JOIN {schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id " +
            $"    JOIN {schemaPrefix}gl hit ON hit.entry_id = r.entry_id " +
            $"    WHERE {GlPopulationScopeSql.Predicate(context, "hit")} " +
            scenarioScope.Predicate("fs.position") +
            "), hit_voucher AS (" +
            "    SELECT voucher_number, blank_entry_id, " +
            $"           {ScenarioTagMask.SumSql("scenario_position")} AS tag_mask " +
            "    FROM hit_position " +
            "    GROUP BY voucher_number, blank_entry_id" +
            "), voucher_rows AS (" +
            "SELECT g.voucher_number, MIN(g.gl_date) AS gl_date, MIN(g.created_by) AS created_by, " +
            "       COALESCE(SUM(g.debit_amount_scaled), 0) AS debit_s, h.tag_mask, CAST(0 AS BIGINT) AS blank_entry_id " +
            $"FROM {schemaPrefix}gl g " +
            "JOIN hit_voucher h ON h.voucher_number = g.voucher_number " +
            $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} " +
            "GROUP BY g.voucher_number, h.tag_mask " +
            // 有傳票號碼的和傳票號碼是 NULL 的各用一個等值 JOIN，再用 UNION ALL 合併。
            // 如果合成一個 JOIN、用 OR 同時比對傳票號碼與 entry_id，資料量大時資料庫可能改用巢狀迴圈，匯出會很慢。
            "UNION ALL SELECT g.voucher_number, g.gl_date, g.created_by, " +
            "       g.debit_amount_scaled, h.tag_mask, h.blank_entry_id " +
            $"FROM {schemaPrefix}gl g JOIN hit_voucher h ON h.blank_entry_id = g.entry_id " +
            $"WHERE {HitVoucherRows.HasNoVoucherNumber("h")} AND {HitVoucherRows.HasNoVoucherNumber("g")} AND {GlPopulationScopeSql.Predicate(context, "g")}" +
            ") SELECT voucher_number, gl_date, created_by, debit_s, tag_mask FROM voucher_rows " +
            "ORDER BY COALESCE(voucher_number, ''), blank_entry_id;";
        command.CommandTimeout = 0;

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var voucher = new VoucherTagRow(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3));
            var tagMask = reader.IsDBNull(4)
                ? 0L
                : reader.GetValue(4) switch
                {
                    BigInteger value => checked((long)value),
                    var value => Convert.ToInt64(
                        value,
                        CultureInfo.InvariantCulture)
                };
            yield return new WorkingPaperHighRiskVoucherRow(
                voucher,
                ScenarioTagMask.DecodePositions(tagMask));
        }
    }
}
