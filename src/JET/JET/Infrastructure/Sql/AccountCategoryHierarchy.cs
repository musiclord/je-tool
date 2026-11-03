using System.Data.Common;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>科目分類樹的祖先與子孫對照表 account_category_path；每次儲存分類樹時整份改寫。</summary>
internal static class AccountCategoryHierarchy
{
    internal static async Task SavePathsAsync(DbConnection connection, DbTransaction transaction,
        IReadOnlyList<AccountCategory> categories, string prefix, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {prefix}account_category_path";
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = $"INSERT INTO {prefix}account_category_path (ancestor_id, descendant_id) VALUES (@ancestor, @descendant)";
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
