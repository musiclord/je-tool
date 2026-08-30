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

        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        var keyset = hasCursor ? "AND g.entry_id > @cursor" : string.Empty;
        await using var command = database.CreateCommand(
            connection,
            projectId,
            "SELECT s.source_row_number, bs.source_file_name, bs.sheet_name, "
            + "g.document_number, g.account_code, g.post_date, g.document_description, g.entry_id "
            + "FROM {s}.target_gl_entry g "
            + "JOIN {s}.staging_gl_raw_row s ON s.batch_id = g.batch_id "
            + "AND s.row_number = g.source_row_number "
            + "JOIN {s}.import_batch_source bs ON bs.batch_id = s.batch_id AND bs.source_no = s.source_no "
            + "WHERE " + ValidationProcedures.NullPostDateSourceQualityPredicateWith("g") + " " + keyset + " "
            + "ORDER BY g.entry_id " + SqlServerDialect.Instance.LimitClause("@pageSize") + ";");
        if (hasCursor)
        {
            if (!long.TryParse(cursorKey, NumberStyles.None, CultureInfo.InvariantCulture, out var cursor))
            {
                throw new JetActionException(JetErrorCodes.InvalidPayload, "cursor 不是有效的來源品質排序鍵。");
            }
            command.Parameters.AddWithValue("@cursor", cursor);
        }
        command.Parameters.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        var rows = new List<SourceQualityFindingRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fileName = reader.GetString(1);
            var sourceLabel = reader.IsDBNull(2)
                ? fileName
                : $"{fileName} [{reader.GetString(2)}]";
            rows.Add(new SourceQualityFindingRow(
                "nullPostDate",
                reader.GetInt32(0),
                sourceLabel,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt64(7)));
        }

        var hasMore = rows.Count > request.ClampedPageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var nextCursor = hasMore
            ? PageCursor.Encode(rows[^1].EntryId.ToString(CultureInfo.InvariantCulture))
            : null;
        return new PageResult<SourceQualityFindingRow>(rows, nextCursor);
    }
}
