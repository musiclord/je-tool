using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SqlServerDevDatabaseInspector 的開發檢視測試。SQL Server 以 LocalDB/env 閘控；無可用實例時由
/// <see cref="SqlServerFactAttribute"/> 標示為 skipped。
/// </summary>
public sealed class SqlServerDevDatabaseInspectorTests
{
    [SqlServerFact]
    public async Task GetOverviewAsync_ProjectWithSeededTables_ReturnsDatabaseNameFileSizeVersionAndRowCounts()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedInspectorTablesAsync(project.Database, project.ProjectId);
        var inspector = new SqlServerDevDatabaseInspector(project.Database);

        var overview = await inspector.GetOverviewAsync(project.ProjectId, CancellationToken.None);

        // schema-per-project:「位置」即該專案 schema 名(取代退役中的 per-database 庫名)。
        Assert.Equal(SqlServerProjectSchema.For(project.ProjectId), overview.DatabasePath);
        Assert.True(overview.FileSizeBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(overview.EngineVersion));
        Assert.Contains(overview.Tables, table => table.Name == "dev_plain" && table.RowCount == 2);
        Assert.Contains(overview.Tables, table => table.Name == "dev]quoted" && table.RowCount == 1);
    }

    [SqlServerFact]
    public async Task GetTablePageAsync_UnknownTable_ReturnsNull()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedInspectorTablesAsync(project.Database, project.ProjectId);
        var inspector = new SqlServerDevDatabaseInspector(project.Database);

        var page = await inspector.GetTablePageAsync(
            project.ProjectId, "missing_table", limit: 10, offset: 0, CancellationToken.None);

        Assert.Null(page);
    }

    [SqlServerFact]
    public async Task GetTablePageAsync_ExistingTable_ReturnsColumnsRowsTotalAndPagingMetadata()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedInspectorTablesAsync(project.Database, project.ProjectId);
        var inspector = new SqlServerDevDatabaseInspector(project.Database);

        var page = await inspector.GetTablePageAsync(
            project.ProjectId, "dev_plain", limit: 10, offset: 0, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal("dev_plain", page.TableName);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(10, page.Limit);
        Assert.Equal(0, page.Offset);
        Assert.Equal(["id", "name", "amount"], page.Columns);
        Assert.Equal(2, page.Rows.Count);
        Assert.Contains(page.Rows, row => row.SequenceEqual(["1", "Alpha", "12.50"]));
        Assert.Contains(page.Rows, row => row.SequenceEqual(["2", null, null]));
    }

    private static async Task SeedInspectorTablesAsync(
        SqlServerProjectDatabase database, string projectId)
    {
        // schema-per-project:檢視只列該專案 schema 內的表,故 seed 也建在該 schema(EnsureCreated 已建 schema)。
        var s = SqlServerProjectSchema.QualifierFor(projectId); // 例 [prj_xxx].
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(
            connection,
            $"""
            CREATE TABLE {s}dev_plain (
                id INT NOT NULL,
                name NVARCHAR(100) NULL,
                amount DECIMAL(10, 2) NULL
            );
            INSERT INTO {s}dev_plain (id, name, amount) VALUES (1, N'Alpha', 12.50), (2, NULL, NULL);
            CREATE TABLE {s}[dev]]quoted] (id INT NOT NULL);
            INSERT INTO {s}[dev]]quoted] (id) VALUES (7);
            """);
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }
}
