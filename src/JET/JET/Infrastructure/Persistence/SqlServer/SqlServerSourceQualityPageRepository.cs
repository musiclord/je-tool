using System.Globalization;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed class SqlServerSourceQualityPageRepository(SqlServerProjectDatabase database)
    : ISourceQualityPageRepository
{
    public async Task<PageResult<SourceQualityFindingRow>> GetPageAsync(
        string projectId,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        var paging = KeysetPaging.Plan(SqlServerDialect.Instance, request, ResultPageSorting.SourceQuality);
        await using var command = database.CreateCommand(
            connection,
            projectId,
            "SELECT s.source_row_number, bs.source_file_name, bs.sheet_name, "
            + "g.document_number, g.account_code, g.post_date, g.document_description, g.entry_id" + paging.SelectSuffix + " "
            + "FROM {s}.target_gl_entry g "
            + "JOIN {s}.staging_gl_raw_row s ON s.batch_id = g.batch_id "
            + "AND s.row_number = g.source_row_number "
            + "JOIN {s}.import_batch_source bs ON bs.batch_id = s.batch_id AND bs.source_no = s.source_no "
            + "WHERE " + ValidationProcedures.NullPostDateSourceQualityPredicateWith("g") + paging.Predicate + " "
            + paging.OrderBy + " " + SqlServerDialect.Instance.LimitClause("@pageSize") + ";");
        foreach (var parameter in paging.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        }
        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        var buffer = new KeysetPageBuffer<SourceQualityFindingRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fileName = reader.GetString(1);
            var sourceLabel = reader.IsDBNull(2)
                ? fileName
                : $"{fileName} [{reader.GetString(2)}]";
            buffer.Add(
                new SourceQualityFindingRow(
                    "nullPostDate",
                    reader.GetInt32(0),
                    sourceLabel,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt64(7)),
                paging.HasSort ? reader.GetValue(8) : null);
        }

        return buffer.ToPage(request, paging, static row => row.EntryId);
    }
}
