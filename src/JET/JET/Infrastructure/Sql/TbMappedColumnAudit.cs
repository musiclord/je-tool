using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>TB 必填文字欄整欄空白時提醒，不阻擋確認；欄位清單取自正式欄位目錄。</summary>
internal static class TbMappedColumnAudit
{
    private static readonly (string FieldKey, string TargetColumn, string FriendlyName)[] RequiredTextColumns =
        JetFieldCatalog.TbFields
            .Where(field => field.Kind == JetFieldValueKind.Text)
            .SelectMany(field => field.MappingSlots.Select(slot => (Field: field, Slot: slot)))
            .Where(item => item.Slot.IsAlwaysRequired)
            .OrderBy(item => item.Slot.Order)
            .Select(item => (item.Slot.Key, item.Field.SemanticSqlTarget, item.Slot.Label)).ToArray();

    internal static async Task<IReadOnlyList<string>> ReadAsync(DbConnection connection, DbTransaction transaction,
        TbMappingSpec spec, int rowCount, IProviderSqlDialect dialect, CancellationToken ct, string prefix = "")
    {
        if (rowCount == 0 || RequiredTextColumns.Length == 0) return [];
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + string.Join(", ", RequiredTextColumns.Select(column =>
            $"CAST(SUM(CASE WHEN {SqlBlankText.IsNotBlank(dialect, column.TargetColumn)} THEN CAST(1 AS BIGINT) ELSE CAST(0 AS BIGINT) END) AS BIGINT)"))
            + $" FROM {prefix}target_tb_balance;";
        var warnings = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return warnings;
        for (var index = 0; index < RequiredTextColumns.Length; index++)
        {
            if (Convert.ToInt64(reader.GetValue(index)) != 0) continue;
            var (fieldKey, _, friendly) = RequiredTextColumns[index];
            warnings.Add(GlMappedColumnAudit.BlankColumnWarning(friendly, spec.Mapping.GetValueOrDefault(fieldKey)));
        }
        return warnings;
    }
}
