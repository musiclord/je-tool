using System.Data.Common;

namespace JET.Infrastructure;

internal static class AuthorizedPreparerMetadataSql
{
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction,
        string? sourceColumn, CancellationToken ct, string prefix = "")
    {
        var key = prefix.Length == 0 ? "key" : "[key]";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {prefix}schema_info WHERE {key} = 'authorized_preparer_source_column';";
        if (sourceColumn is not null)
        {
            command.CommandText += $" INSERT INTO {prefix}schema_info ({key}, value) VALUES ('authorized_preparer_source_column', @column);";
            command.AddWithValue("@column", sourceColumn);
        }
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<string?> ReadAsync(DbConnection connection, CancellationToken ct, string prefix = "")
    {
        var key = prefix.Length == 0 ? "key" : "[key]";
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM {prefix}schema_info WHERE {key} = 'authorized_preparer_source_column';";
        return await command.ExecuteScalarAsync(ct) as string;
    }
}
