using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Domain 欄位目錄登錄的 GL 與 TB 欄位，要和 SQLite 實際建出的資料表欄位一致。
/// 這個測試只檢查 <see cref="JetFieldCatalog"/> 已登錄的欄位；批次識別、自動編號，
/// 以及 debit、credit、dr_cr 等衍生欄不在欄位目錄裡，不在檢查範圍。
/// </summary>
public sealed class FieldCatalogStorageMirrorTests
{
    [Fact]
    public async Task LocalProviderSchema_MatchesCatalogTargetsAndNullability()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);

        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        var glColumns = await ReadPragmaColumnsAsync(connection, "gl");
        var tbColumns = await ReadPragmaColumnsAsync(connection, "tb");

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
