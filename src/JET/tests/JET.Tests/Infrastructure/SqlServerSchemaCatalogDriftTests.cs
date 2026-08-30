using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SQL Server 端的目錄漂移守門（design §2.4；SQLite 端見 <see cref="SchemaCatalogDriftTests"/>）：
/// <see cref="JetSchemaCatalog"/> 必須與 SQL Server SchemaSql 建出的真實 schema 雙向對齊。
/// 封住的盲點鏈：「新表只進 SQL Server 的 SchemaSql、漏登錄 catalog、連帶逸出隔離掃描」——
/// SQLite 端的守衛看不到只加在 T-SQL 這邊的表。
/// 機制：每測試建全新 prj_xxx schema（跑權威 SchemaSql），查 <c>sys.tables</c> 過濾到該 schema，
/// 與登錄的實體表名雙向 <c>Assert.Equal</c>（漏登錄與幽靈條目同一斷言鎖死）。
/// </summary>
public sealed class SqlServerSchemaCatalogDriftTests
{
    [SqlServerFact]
    public async Task Catalog_PhysicalNames_EqualSqlServerProjectSchemaTables()
    {
        await using var sql = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(sql);

        var physicalTables = new HashSet<string>(StringComparer.Ordinal);
        await using (var connection = sql.Database.CreateConnection(sql.ProjectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID(@schema);";
            command.Parameters.AddWithValue("@schema", SqlServerProjectSchema.For(sql.ProjectId));
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                physicalTables.Add(reader.GetString(0));
            }
        }

        // sanity：確有建出表（防「查到空集合假綠」——例如 schema 名沒對上）。
        Assert.NotEmpty(physicalTables);

        var registered = JetSchemaCatalog.All
            .Select(e => e.PhysicalName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(physicalTables, registered);
    }
}
