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
internal static class WorkpaperStep4StreamReader
{
    internal static async IAsyncEnumerable<WorkpaperStep4VoucherRow> ReadAsync(
        DbConnection connection,
        ISqlDialect dialect,
        GlPopulationContext context,
        IReadOnlyList<int> scenarioPositions,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string schemaPrefix = "")
    {
        var scenarioScope = TagMatrixScenarioSqlScope.Create(scenarioPositions);
        if (scenarioScope.IsEmpty)
        {
            yield break;
        }

        await using var command = connection.CreateCommand();
        GlPopulationScopeSql.Plan(dialect, context, "g").BindParametersTo(command);
        scenarioScope.AddParameters(command);
        command.CommandText =
            "WITH hit_position AS (" +
            "    SELECT DISTINCT hit.document_number, r.scenario_position " +
            $"    FROM {schemaPrefix}result_filter_run r " +
            $"    JOIN {schemaPrefix}target_gl_entry hit ON hit.entry_id = r.entry_id " +
            "    WHERE hit.document_number IS NOT NULL " +
            $"      AND {GlPopulationScopeSql.Predicate(context, "hit")} " +
            scenarioScope.Predicate("r.scenario_position") +
            "), hit_voucher AS (" +
            "    SELECT document_number, " +
            "           SUM(CASE scenario_position " +
            "               WHEN 1 THEN 1 WHEN 2 THEN 2 WHEN 3 THEN 4 " +
            "               WHEN 4 THEN 8 WHEN 5 THEN 16 WHEN 6 THEN 32 " +
            "               WHEN 7 THEN 64 WHEN 8 THEN 128 WHEN 9 THEN 256 " +
            "               WHEN 10 THEN 512 ELSE 0 END) AS tag_mask " +
            "    FROM hit_position " +
            "    GROUP BY document_number" +
            ") " +
            "SELECT g.document_number, MIN(g.post_date), MIN(g.created_by), " +
            "       COALESCE(SUM(g.debit_amount_scaled), 0), h.tag_mask " +
            $"FROM {schemaPrefix}target_gl_entry g " +
            "JOIN hit_voucher h ON h.document_number = g.document_number " +
            $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} " +
            "GROUP BY g.document_number, h.tag_mask " +
            "ORDER BY g.document_number;";
        command.CommandTimeout = 0;

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var voucher = new VoucherTagRow(
                reader.GetString(0),
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
            yield return new WorkpaperStep4VoucherRow(
                voucher,
                DecodePositions(tagMask));
        }
    }

    private static IReadOnlyList<int> DecodePositions(long tagMask)
    {
        if (tagMask == 0)
        {
            return [];
        }

        var positions = new List<int>(10);
        for (var position = 1; position <= 10; position++)
        {
            if ((tagMask & (1L << (position - 1))) != 0)
            {
                positions.Add(position);
            }
        }
        return positions;
    }
}
