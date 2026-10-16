using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;

namespace JET.Tests.Application;

internal sealed class Batch9MappingTestFixture : IDisposable
{
    internal HandlerTestHost Host { get; } = new();
    internal string ProjectId { get; private set; } = "";
    private readonly string provider;

    private Batch9MappingTestFixture(string provider) => this.provider = provider;

    /// <summary>同一組參數在一個測試程序裡只建一次，之後每個測試複製一份（<see cref="CaseTemplates"/>）。</summary>
    internal static async Task<Batch9MappingTestFixture> CreateAsync(string provider, bool manyRows = false)
    {
        var fixture = new Batch9MappingTestFixture(provider);
        if (!CaseTemplates.CanCopy(provider))
        {
            fixture.ProjectId = await BuildAsync(fixture.Host, provider, manyRows);
            return fixture;
        }

        var copied = await CaseTemplates.CopyIntoAsync(fixture.Host, $"batch9-mapping:{manyRows}:{provider}",
            async templateHost => (await BuildAsync(templateHost, provider, manyRows), (object?)null));
        fixture.ProjectId = copied.ProjectId;
        return fixture;
    }

    private static async Task<string> BuildAsync(HandlerTestHost host, string provider, bool manyRows)
    {
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        { caseName = "Batch9 synthetic", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider }));
        var projectId = created.GetProperty("projectId").GetString()!;
        await ImportSourceAsync(host, 0, manyRows ? 60 : 2, "first.xlsx", append: false);
        if (manyRows) await ImportSourceAsync(host, 60, 5, "second.xlsx", append: true);
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        { mapping = Mapping(), amountMode = "signed", rdeFields = Array.Empty<object>() }));
        var tb = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "Account"; sheet.Cell(1, 2).Value = "Name";
            sheet.Cell(1, 3).Value = "Good"; sheet.Cell(1, 4).Value = "Bad";
            sheet.Cell(2, 1).Value = "A"; sheet.Cell(2, 2).Value = "Synthetic";
            sheet.Cell(2, 3).Value = manyRows ? 65 : 2; sheet.Cell(2, 4).Value = "1500,50";
        });
        try
        {
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tb }));
            await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
            { changeMode = "direct", mapping = new { accNum = "Account", accName = "Name", amount = "Good" } }));
        }
        finally { TestWorkbookBuilder.Delete(tb); }
        await host.DispatchAsync("validate.run");
        return projectId;
    }

    private static async Task ImportSourceAsync(HandlerTestHost host, int start, int count, string fileName, bool append)
    {
        string[] columns = ["Document", "Date", "Account", "Name", "Description", "Amount", "BadAmount", "BadDate", "Side", "BadSide"];
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            for (var column = 0; column < columns.Length; column++) sheet.Cell(1, column + 1).Value = columns[column];
            for (var index = 0; index < count; index++)
            {
                var number = start + index;
                string[] values = [$"V{number:000}", "2025-03-05", "A", "Synthetic", "Normal entry", "1.00",
                    number < 60 ? "1500,50" : "12,34", "240115", index % 2 == 0 ? " d " : " c ",
                    number < 60 ? "X" : number < 63 ? "Y" : ""];
                for (var column = 0; column < columns.Length; column++) sheet.Cell(index + 2, column + 1).Value = values[column];
            }
        });
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            { filePath = path, fileName, mode = append ? "append" : "replace" }));
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    internal static Dictionary<string, string> Mapping() => new()
    { ["docNum"] = "Document", ["postDate"] = "Date", ["accNum"] = "Account", ["accName"] = "Name",
        ["description"] = "Description", ["amount"] = "Amount" };

    internal Task<JsonElement> CommitAsync(Dictionary<string, string> mapping, string mode, object[]? rdeFields = null) =>
        Host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new { mapping, amountMode = mode, rdeFields = rdeFields ?? [] }));

    internal Task<JsonElement> LoadAsync() => Host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = ProjectId }));

    internal async Task<long> ScalarAsync(string sql)
    {
        var folder = new JetProjectFolder(Host.ProjectsRoot);
        ILocalProjectDatabase database = provider == "duckdb" ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);
        await using var connection = database.CreateConnection(ProjectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public void Dispose() => Host.Dispose();
}
