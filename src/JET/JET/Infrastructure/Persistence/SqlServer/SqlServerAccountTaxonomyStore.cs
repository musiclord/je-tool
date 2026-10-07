using System.Data;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

public sealed class SqlServerAccountTaxonomyStore(SqlServerProjectDatabase database) : IAccountTaxonomyStore
{
    public async Task<AccountTaxonomySnapshot> ReadAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = database.CreateCommand(connection, projectId,
            """
            SELECT category_id, label, ordinal, semantic_role, is_builtin, revision, parent_category_id
            FROM {s}.config_account_taxonomy
            ORDER BY ordinal, category_id;
            """);

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
                reader.GetBoolean(4), reader.IsDBNull(6) ? null : reader.GetString(6)));
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

        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var current = await ReadWithinAsync(projectId, connection, transaction, cancellationToken);
        if (expectedRevision != current.Revision)
        {
            throw new JetActionException(
                JetErrorCodes.TaxonomyRevisionConflict,
                "科目分類剛被其他操作更新，請重新開啟分類設定再儲存一次。");
        }

        await EnsureDeletedCategoriesAreUnusedAsync(
            projectId,
            connection,
            transaction,
            current.Categories.Where(item => !item.IsBuiltIn)
                .Select(item => item.CategoryId)
                .Except(replacement.Select(item => item.CategoryId), StringComparer.Ordinal)
                .ToArray(),
            current.Categories,
            cancellationToken);

        var nextRevision = checked(current.Revision + 1);
        await using (var delete = database.CreateCommand(
                         connection,
                         projectId,
                         "DELETE FROM {s}.config_account_taxonomy;"))
        {
            delete.Transaction = transaction;
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = database.CreateCommand(
                         connection,
                         projectId,
                         """
                         INSERT INTO {s}.config_account_taxonomy
                             (category_id, label, ordinal, semantic_role, is_builtin, revision, parent_category_id)
                         VALUES (@categoryId, @label, @ordinal, @semanticRole, @isBuiltIn, @revision, @parent);
                         """))
        {
            insert.Transaction = transaction;
            var categoryId = insert.Parameters.Add("@categoryId", SqlDbType.NVarChar, 64);
            var label = insert.Parameters.Add("@label", SqlDbType.NVarChar, 400);
            var ordinal = insert.Parameters.Add("@ordinal", SqlDbType.Int);
            var semanticRole = insert.Parameters.Add("@semanticRole", SqlDbType.NVarChar, 64);
            var isBuiltIn = insert.Parameters.Add("@isBuiltIn", SqlDbType.Bit);
            var parent = insert.Parameters.Add("@parent", SqlDbType.NVarChar, 64);
            var revision = insert.Parameters.Add("@revision", SqlDbType.Int);
            foreach (var category in replacement)
            {
                categoryId.Value = category.CategoryId;
                label.Value = category.Label;
                ordinal.Value = category.Ordinal;
                semanticRole.Value = category.SemanticRole;
                isBuiltIn.Value = category.IsBuiltIn;
                revision.Value = nextRevision;
                parent.Value = (object?)category.ParentCategoryId ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await AccountTaxonomyHierarchy.SavePathsAsync(connection, transaction, replacement, SqlServerProjectSchema.QualifierFor(projectId), cancellationToken);
        await RuleRunResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.AccountTaxonomy,
            SqlServerProjectSchema.QualifierFor(projectId));
        await transaction.CommitAsync(cancellationToken);
        return new AccountTaxonomySnapshot(nextRevision, replacement);
    }

    private async Task<AccountTaxonomySnapshot> ReadWithinAsync(
        string projectId,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(
            connection,
            projectId,
            """
            SELECT category_id, label, ordinal, semantic_role, is_builtin, revision, parent_category_id
            FROM {s}.config_account_taxonomy WITH (UPDLOCK, HOLDLOCK)
            ORDER BY ordinal, category_id;
            """);
        command.Transaction = transaction;
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
                reader.GetBoolean(4), reader.IsDBNull(6) ? null : reader.GetString(6)));
            revision = Math.Max(revision, reader.GetInt32(5));
        }
        return new AccountTaxonomySnapshot(revision, categories);
    }

    private async Task EnsureDeletedCategoriesAreUnusedAsync(
        string projectId,
        SqlConnection connection,
        SqlTransaction transaction,
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
            await using var mapping = database.CreateCommand(
                connection,
                projectId,
                "SELECT COUNT_BIG(*) FROM {s}.target_account_mapping WHERE category_id = @categoryId;");
            mapping.Transaction = transaction;
            mapping.Parameters.AddWithValue("@categoryId", deletedId);
            if (Convert.ToInt64(await mapping.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                InUse(deletedId, currentCategories);
            }
        }

        // 已存篩選情境不擋刪除：分類設定的修改會在同一交易內清掉全部情境（使用者 2026-10-07 裁定上游修改清除下游）。
    }

    private static void InUse(string categoryId, IReadOnlyList<AccountTaxonomyCategory> currentCategories) =>
        throw new JetActionException(
            JetErrorCodes.TaxonomyCategoryInUse,
            AccountTaxonomyStoreSupport.InUseMessage(categoryId, currentCategories));
}
