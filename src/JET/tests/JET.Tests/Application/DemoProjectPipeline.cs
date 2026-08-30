using System.Text.Json;
using JET.Application;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 共用的 demo 專案建置流程（與前端 MockDataLoader 完全相同的正式管線：
/// loadDemo → create → export xlsx → import.*.fromFile → calendar → mapping commit → validate）。
/// Debug-only demo actions 由本測試專用 dispatcher 提供；正式 workflow actions 仍走 production composition。
/// 測試以此為 deterministic 母體，並以 QueryScalarAsync 做獨立 SQL recount。
/// </summary>
internal static class DemoProjectPipeline
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private static readonly ActionDispatcher DemoFixtureDispatcher = CreateDemoFixtureDispatcher();

    public sealed record Context(string ProjectId, JsonElement Demo);

    /// <summary>
    /// 測試專用的 Debug demo action seam。Release 測試可繼續建立 deterministic fixture，
    /// 但 production <c>AppCompositionRoot</c> 的 dispatcher 不會因此重新註冊 demo actions。
    /// </summary>
    internal static async Task<JsonElement> DispatchDemoFixtureAsync(
        string action,
        CancellationToken cancellationToken = default)
    {
        using var payload = JsonDocument.Parse("{}");
        var data = await DemoFixtureDispatcher.DispatchAsync(
            action,
            payload.RootElement,
            cancellationToken);
        return JsonSerializer.SerializeToElement(data, WireJson);
    }

    public static async Task<Context> SetupAsync(
        HandlerTestHost host,
        bool commitGl = true,
        bool commitTb = true,
        bool importCalendar = true,
        bool importAccountMapping = true,
        bool importAuthorizedPreparer = true,
        bool runValidation = true,
        string databaseProvider = "sqlite")
    {
        var demo = await DispatchDemoFixtureAsync("project.loadDemo");
        var project = demo.GetProperty("project");

        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = project.GetProperty("projectCode").GetString(),
            entityName = project.GetProperty("entityName").GetString(),
            operatorId = project.GetProperty("operatorId").GetString(),
            periodStart = project.GetProperty("periodStart").GetString(),
            periodEnd = project.GetProperty("periodEnd").GetString(),
            lastPeriodStart = project.GetProperty("lastPeriodStart").GetString(),
            databaseProvider
        }));
        var projectId = created.GetProperty("projectId").GetString()!;

        var glFile = await DispatchDemoFixtureAsync("demo.exportGlFile");
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
        {
            filePath = glFile.GetProperty("filePath").GetString(),
            fileName = glFile.GetProperty("fileName").GetString()
        }));

        var tbFile = await DispatchDemoFixtureAsync("demo.exportTbFile");
        await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new
        {
            filePath = tbFile.GetProperty("filePath").GetString(),
            fileName = tbFile.GetProperty("fileName").GetString()
        }));

        if (importCalendar)
        {
            var holidays = demo.GetProperty("holidays").EnumerateArray().Select(h => h.GetString()).ToList();
            await host.DispatchAsync("import.holiday", JsonSerializer.Serialize(new { dates = holidays }));

            var makeupDays = demo.GetProperty("makeupDays").EnumerateArray().Select(h => h.GetString()).ToList();
            await host.DispatchAsync("import.makeupDay", JsonSerializer.Serialize(new { dates = makeupDays }));
        }

        if (importAccountMapping)
        {
            var amFile = await DispatchDemoFixtureAsync("demo.exportAccountMappingFile");
            await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
            {
                filePath = amFile.GetProperty("filePath").GetString(),
                fileName = amFile.GetProperty("fileName").GetString()
            }));
        }

        if (importAuthorizedPreparer)
        {
            var apFile = await DispatchDemoFixtureAsync("demo.exportAuthorizedPreparerFile");
            await host.DispatchAsync("import.authorizedPreparer.fromFile", JsonSerializer.Serialize(new
            {
                filePath = apFile.GetProperty("filePath").GetString(),
                fileName = apFile.GetProperty("fileName").GetString()
            }));
        }

        if (commitGl)
        {
            await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
            {
                mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
                amountMode = demo.GetProperty("gl").GetProperty("amountMode").GetString()
            }));
        }

        if (commitTb)
        {
            await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
            {
                mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    demo.GetProperty("tb").GetProperty("mapping").GetRawText()),
                changeMode = demo.GetProperty("tb").GetProperty("changeMode").GetString()
            }));
        }

        if (runValidation && commitGl && commitTb)
        {
            await host.DispatchAsync("validate.run");
        }

        return new Context(projectId, demo);
    }

    private static ActionDispatcher CreateDemoFixtureDispatcher()
    {
        var writer = new DemoWorkbookWriter();
        return new ActionDispatcher(
            [
                new ProjectLoadDemoHandler(),
                new DemoExportGlFileHandler(writer),
                new DemoExportTbFileHandler(writer),
                new DemoExportAccountMappingFileHandler(writer),
                new DemoExportAuthorizedPreparerFileHandler(writer)
            ],
            NullLogger<ActionDispatcher>.Instance,
            new ProjectSession());
    }

    /// <summary>獨立的參數化 SQL recount：不經 handler，直接查專案 DB。</summary>
    public static async Task<long> QueryScalarAsync(
        HandlerTestHost host,
        string projectId,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    /// <summary>獨立的參數化 SQL 單欄列表 recount:不經 handler,直接查專案 DB,回第一欄字串(NULL→null)。</summary>
    public static async Task<List<string?>> QueryStringListAsync(
        HandlerTestHost host,
        string projectId,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.AddWithValue(name, value);
        }

        var rows = new List<string?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.IsDBNull(0) ? null : reader.GetValue(0).ToString());
        }

        return rows;
    }
}

/// <summary>
/// xUnit class fixture：同類別測試共用一個已建置完成的 demo 專案。
/// 約定：共用此 fixture 的測試只能呼叫規則執行 action 並斷言「該次呼叫自身的 response」
/// （或穩定母體事實），不得依賴跨測試的執行順序。
/// </summary>
public sealed class DemoProjectFixture : IAsyncLifetime
{
    internal HandlerTestHost Host { get; private set; } = null!;

    internal string ProjectId { get; private set; } = string.Empty;

    internal JsonElement Demo { get; private set; }

    public async ValueTask InitializeAsync()
    {
        Host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(Host);
        ProjectId = context.ProjectId;
        Demo = context.Demo;
    }

    public ValueTask DisposeAsync()
    {
        Host.Dispose();
        return ValueTask.CompletedTask;
    }
}
