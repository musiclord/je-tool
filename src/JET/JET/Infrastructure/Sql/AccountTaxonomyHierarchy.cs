using System.Data.Common;
using JET.Domain;

namespace JET.Infrastructure;

internal static class AccountTaxonomyHierarchy
{
    internal static async Task UpgradeLocalAsync(DbConnection connection, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM config_account_taxonomy WHERE 1 = 0";
        bool hasParent;
        await using (var reader = await command.ExecuteReaderAsync(ct))
            hasParent = Enumerable.Range(0, reader.FieldCount).Any(i => reader.GetName(i) == "parent_category_id");
        if (!hasParent)
        {
            command.CommandText = "ALTER TABLE config_account_taxonomy ADD COLUMN parent_category_id TEXT NULL";
            await command.ExecuteNonQueryAsync(ct);
        }
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS config_account_taxonomy_path (
                ancestor_id TEXT NOT NULL, descendant_id TEXT NOT NULL,
                PRIMARY KEY (ancestor_id, descendant_id));
            INSERT INTO config_account_taxonomy_path SELECT category_id, category_id FROM config_account_taxonomy t
                WHERE NOT EXISTS (SELECT 1 FROM config_account_taxonomy_path p WHERE p.ancestor_id = t.category_id AND p.descendant_id = t.category_id);
            UPDATE schema_info SET value = '11' WHERE key = 'schema_version';
            """;
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    internal static async Task SavePathsAsync(DbConnection connection, DbTransaction transaction,
        IReadOnlyList<AccountTaxonomyCategory> categories, string prefix, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {prefix}config_account_taxonomy_path";
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = $"INSERT INTO {prefix}config_account_taxonomy_path (ancestor_id, descendant_id) VALUES (@ancestor, @descendant)";
        var ancestor = command.AddParameter("@ancestor", System.Data.DbType.String);
        var descendant = command.AddParameter("@descendant", System.Data.DbType.String);
        var byId = categories.ToDictionary(item => item.CategoryId, StringComparer.Ordinal);
        foreach (var category in categories)
        {
            descendant.Value = category.CategoryId;
            for (var current = category; current is not null; current = current.ParentCategoryId is null ? null : byId[current.ParentCategoryId])
            {
                ancestor.Value = current.CategoryId;
                await command.ExecuteNonQueryAsync(ct);
            }
        }
    }
}
