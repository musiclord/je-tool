using System.Data;
using JET.Domain;

namespace JET.Infrastructure;

public sealed class LocalAccountTaxonomyStore(ILocalProjectDatabase database) : IAccountTaxonomyStore
{
    public async Task<AccountTaxonomySnapshot> ReadAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT category_id, label, ordinal, semantic_role, is_builtin, revision, parent_category_id
            FROM config_account_taxonomy
            ORDER BY ordinal, category_id;
            """;

        var categories = new List<AccountTaxonomyCategory>();
        var revision = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            categories.Add(new AccountTaxonomyCategory(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                Convert.ToInt32(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture) != 0, reader.IsDBNull(6) ? null : reader.GetString(6)));
            revision = Math.Max(revision, reader.GetInt32(5));
        }

        return new AccountTaxonomySnapshot(revision, categories);
    }

    public async Task<AccountTaxonomySnapshot> SaveAsync(
        string projectId,
        int expectedRevision,
        IReadOnlyList<AccountTaxonomyCategory> categories,
        CancellationToken cancellationToken)
    {
        AccountTaxonomyInvariant.ValidateReplacement(categories);
        var replacement = categories.OrderBy(item => item.Ordinal).ThenBy(item => item.CategoryId).ToArray();

        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await ReadWithinAsync(connection, transaction, cancellationToken);
        EnsureRevision(expectedRevision, current.Revision);
        await EnsureDeletedCategoriesAreUnusedAsync(
            connection,
            transaction,
            current.Categories.Where(item => !item.IsBuiltIn)
                .Select(item => item.CategoryId)
                .Except(replacement.Select(item => item.CategoryId), StringComparer.Ordinal)
                .ToArray(),
            current.Categories,
            cancellationToken);

        var nextRevision = checked(current.Revision + 1);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM config_account_taxonomy;";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO config_account_taxonomy
                    (category_id, label, ordinal, semantic_role, is_builtin, revision, parent_category_id)
                VALUES (@categoryId, @label, @ordinal, @semanticRole, @isBuiltIn, @revision, @parent);
                """;
            var categoryId = insert.AddParameter("@categoryId", DbType.String);
            var label = insert.AddParameter("@label", DbType.String);
            var ordinal = insert.AddParameter("@ordinal", DbType.Int32);
            var semanticRole = insert.AddParameter("@semanticRole", DbType.String);
            var isBuiltIn = insert.AddParameter("@isBuiltIn", DbType.Int32);
            var parent = insert.AddParameter("@parent", DbType.String);
            var revision = insert.AddParameter("@revision", DbType.Int32);

            foreach (var category in replacement)
            {
                categoryId.Value = category.CategoryId;
                label.Value = category.Label;
                ordinal.Value = category.Ordinal;
                semanticRole.Value = category.SemanticRole;
                isBuiltIn.Value = category.IsBuiltIn ? 1 : 0;
                revision.Value = nextRevision;
                parent.Value = (object?)category.ParentCategoryId ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await AccountTaxonomyHierarchy.SavePathsAsync(connection, transaction, replacement, "", cancellationToken);
        await RuleRunResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.AccountTaxonomy);
        await transaction.CommitAsync(cancellationToken);
        return new AccountTaxonomySnapshot(nextRevision, replacement);
    }

    private static async Task<AccountTaxonomySnapshot> ReadWithinAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT category_id, label, ordinal, semantic_role, is_builtin, revision, parent_category_id
            FROM config_account_taxonomy
            ORDER BY ordinal, category_id;
            """;
        var categories = new List<AccountTaxonomyCategory>();
        var revision = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            categories.Add(new AccountTaxonomyCategory(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                Convert.ToBoolean(reader.GetValue(4)), reader.IsDBNull(6) ? null : reader.GetString(6)));
            revision = Math.Max(revision, reader.GetInt32(5));
        }
        return new AccountTaxonomySnapshot(revision, categories);
    }

    private static async Task EnsureDeletedCategoriesAreUnusedAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        IReadOnlyList<string> deletedIds,
        IReadOnlyList<AccountTaxonomyCategory> currentCategories,
        CancellationToken cancellationToken)
    {
        if (deletedIds.Count == 0)
        {
            return;
        }

        foreach (var deletedId in deletedIds)
        {
            await using var mapping = connection.CreateCommand();
            mapping.Transaction = transaction;
            mapping.CommandText = "SELECT COUNT(*) FROM target_account_mapping WHERE category_id = @categoryId;";
            mapping.AddWithValue("@categoryId", deletedId);
            if (Convert.ToInt64(await mapping.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                InUse(deletedId, currentCategories);
            }
        }

        // 已存篩選情境不擋刪除：分類設定的修改會在同一交易內清掉全部情境（使用者 2026-10-07 裁定上游修改清除下游）。
    }

    private static void EnsureRevision(int expected, int actual)
    {
        if (expected != actual)
        {
            throw new JetActionException(
                JetErrorCodes.TaxonomyRevisionConflict,
                "科目分類剛被其他操作更新，請重新開啟分類設定再儲存一次。");
        }
    }

    private static void InUse(string categoryId, IReadOnlyList<AccountTaxonomyCategory> currentCategories) =>
        throw new JetActionException(
            JetErrorCodes.TaxonomyCategoryInUse,
            AccountTaxonomyStoreSupport.InUseMessage(categoryId, currentCategories));
}

internal static class AccountTaxonomyStoreSupport
{
    /// <summary>錯誤訊息顯示分類名稱，不顯示內部分類身分；找不到名稱時改用不含代號的說法。</summary>
    internal static string DisplayLabel(string categoryId, IReadOnlyList<AccountTaxonomyCategory> categories) =>
        categories.FirstOrDefault(item => string.Equals(item.CategoryId, categoryId, StringComparison.Ordinal))?.Label
        ?? "要刪除的分類";

    /// <summary>只有科目配對仍指到的分類會擋下刪除；已存篩選情境會隨分類修改一併清除，不在此列。</summary>
    internal static string InUseMessage(string categoryId, IReadOnlyList<AccountTaxonomyCategory> categories) =>
        $"科目分類「{DisplayLabel(categoryId, categories)}」仍有科目配對使用，無法刪除。請先把這些科目改到其他分類，再刪除這個分類。";
}
