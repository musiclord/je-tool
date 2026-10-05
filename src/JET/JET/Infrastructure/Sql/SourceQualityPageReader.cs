using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

internal static class SourceQualityPageReader
{
    internal static async Task<PageResult<SourceQualityFindingRow>> ReadAsync(
        DbConnection connection, DbTransaction? transaction, ISqlDialect dialect,
        string schemaPrefix, PageRequest request, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var paging = KeysetPaging.Plan(dialect, request, ResultPageSorting.SourceQuality);
        foreach (var parameter in paging.Parameters) command.AddWithValue(parameter.Key, parameter.Value);
        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);
        command.CommandText =
            "SELECT s.source_row_number, bs.source_file_name, bs.sheet_name, "
            + "g.document_number, g.account_code, g.post_date, g.document_description, g.entry_id" + paging.SelectSuffix + " "
            + $"FROM {schemaPrefix}target_gl_entry g "
            + $"JOIN {schemaPrefix}staging_gl_raw_row s ON s.batch_id = g.batch_id "
            + "AND s.row_number = g.source_row_number "
            + $"JOIN {schemaPrefix}import_batch_source bs ON bs.batch_id = s.batch_id AND bs.source_no = s.source_no "
            + "WHERE " + ValidationProcedures.NullPostDateSourceQualityPredicateWith("g") + paging.Predicate + " "
            + paging.OrderBy + " " + dialect.LimitClause("@pageSize") + ";";
        var buffer = new KeysetPageBuffer<SourceQualityFindingRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fileName = reader.GetString(1);
            var sourceLabel = reader.IsDBNull(2) ? fileName : $"{fileName} [{reader.GetString(2)}]";
            buffer.Add(new SourceQualityFindingRow("nullPostDate", reader.GetInt32(0), sourceLabel,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7)),
                paging.HasSort ? reader.GetValue(8) : null);
        }
        return buffer.ToPage(request, paging, static row => row.EntryId);
    }
}
