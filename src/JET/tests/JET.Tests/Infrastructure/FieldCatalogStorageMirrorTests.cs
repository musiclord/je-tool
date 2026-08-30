using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Domain 欄位目錄的 persisted semantic targets 必須與三個 provider 真正建出的 schema 對齊。
/// 本守衛只涵蓋 <see cref="JetFieldCatalog"/> 已登錄的 GL／TB semantic fields；
/// batch identity、auto id 與 debit／credit／dr_cr 等衍生欄不屬本目錄，不在此測試範圍。
/// </summary>
public sealed class FieldCatalogStorageMirrorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalProviderSchema_MatchesCatalogTargetsAndNullability(bool useDuckDb)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        ILocalProjectDatabase database = useDuckDb
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);

        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        var glColumns = await ReadPragmaColumnsAsync(connection, "target_gl_entry");
        var tbColumns = await ReadPragmaColumnsAsync(connection, "target_tb_balance");

        AssertCatalogMatchesSchema(JetFieldCatalog.GlFields, glColumns);
        AssertCatalogMatchesSchema(JetFieldCatalog.TbFields, tbColumns);
    }

    [SqlServerFact]
    public async Task SqlServerSchema_MatchesCatalogTargetsAndNullability()
    {
        await using var sql = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(sql);

        await using var connection = sql.Database.CreateConnection(sql.ProjectId);
        await connection.OpenAsync();

        var glColumns = await ReadSqlServerColumnsAsync(
            sql.Database,
            connection,
            sql.ProjectId,
            "target_gl_entry");
        var tbColumns = await ReadSqlServerColumnsAsync(
            sql.Database,
            connection,
            sql.ProjectId,
            "target_tb_balance");

        AssertCatalogMatchesSchema(JetFieldCatalog.GlFields, glColumns);
        AssertCatalogMatchesSchema(JetFieldCatalog.TbFields, tbColumns);
    }

    private static async Task<IReadOnlyDictionary<string, bool>> ReadPragmaColumnsAsync(
        DbConnection connection,
        string table)
    {
        var columns = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(1);
            var isNotNull = Convert.ToBoolean(reader.GetValue(3));
            columns[name] = !isNotNull;
        }

        Assert.NotEmpty(columns);
        return columns;
    }

    private static async Task<IReadOnlyDictionary<string, bool>> ReadSqlServerColumnsAsync(
        SqlServerProjectDatabase database,
        Microsoft.Data.SqlClient.SqlConnection connection,
        string projectId,
        string table)
    {
        var columns = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using var command = database.CreateCommand(
            connection,
            projectId,
            """
            SELECT c.name, c.is_nullable
            FROM sys.columns c
            INNER JOIN sys.tables t ON t.object_id = c.object_id
            WHERE t.schema_id = SCHEMA_ID(@schema) AND t.name = @table
            ORDER BY c.column_id;
            """);
        command.Parameters.AddWithValue("@schema", SqlServerProjectSchema.For(projectId));
        command.Parameters.AddWithValue("@table", table);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns[reader.GetString(0)] = reader.GetBoolean(1);
        }

        Assert.NotEmpty(columns);
        return columns;
    }

    private static void AssertCatalogMatchesSchema(
        IReadOnlyList<JetFieldDefinition> fields,
        IReadOnlyDictionary<string, bool> schemaColumns)
    {
        foreach (var field in fields)
        {
            Assert.True(
                schemaColumns.TryGetValue(field.SemanticSqlTarget, out var actualNullable),
                $"schema 缺少 catalog semantic target：{field.SemanticSqlTarget}");
            Assert.Equal(field.StorageNullable, actualNullable);
        }
    }
}
