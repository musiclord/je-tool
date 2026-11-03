using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// Finalized WorkingPaper step4 的單一 hit-first statement。先由已保存的 filter hits
/// 收斂 distinct (voucher, position)，再只對命中傳票聚合完整 GL；結果以一個
/// forward-only reader 輸出，不依 200-row UI page size 重算全母體。
/// </summary>
internal static class WorkingPaperStep4StreamReader
{
    internal static async IAsyncEnumerable<WorkingPaperStep4VoucherRow> ReadAsync(
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
            "           CASE WHEN hit.voucher_number IS NULL THEN hit.entry_id ELSE 0 END AS blank_entry_id " +
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
            // Keep both joins equijoins. An OR across voucher and entry keys can turn large exports into a nested-loop join.
            "UNION ALL SELECT g.voucher_number, g.gl_date, g.created_by, " +
            "       g.debit_amount_scaled, h.tag_mask, h.blank_entry_id " +
            $"FROM {schemaPrefix}gl g JOIN hit_voucher h ON h.blank_entry_id = g.entry_id " +
            $"WHERE h.voucher_number IS NULL AND g.voucher_number IS NULL AND {GlPopulationScopeSql.Predicate(context, "g")}" +
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
            yield return new WorkingPaperStep4VoucherRow(
                voucher,
                ScenarioTagMask.DecodePositions(tagMask));
        }
    }
}
