using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>第二遍第 3 批：固定的合成名單統計、GL 比對及失敗時保留原清單。</summary>
public sealed class Batch3AuthorizedPreparerTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ImportStatistics_PersistAcrossHostRestart_WithTheSameFixedCounts(string provider)
    {
        using var root = new TempProjectRoot();
        var path = WriteList();
        string projectId;
        try
        {
            using (var first = new HandlerTestHost(projectsRootPath: root.Path))
            {
                projectId = await SetupMappedGlAsync(first, provider);
                var imported = await ImportAsync(first, path);
                AssertCounts(imported);
            }

            using var reopened = new HandlerTestHost(projectsRootPath: root.Path);
            var loaded = await LoadAuthorizedStateAsync(reopened, projectId);
            AssertCounts(loaded);
            Assert.Equal("員工代碼", loaded.GetProperty("sourceColumn").GetString());
            Assert.Equal(2, loaded.GetProperty("matchedPreparerCount").GetInt64());
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task GlMatch_CountsDistinctAuthorizedIdentifiers_NotGlRows_AndIgnoresCaseAndOuterSpaces(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupMappedGlAsync(host, provider);
        var path = WriteList();
        try
        {
            var imported = await ImportAsync(host, path);
            Assert.Equal(3, imported.GetProperty("rowCount").GetInt32());
            // GL 中 E01 出現兩列，e02 出現一列；名單只有兩位人員命中，而不是三列。
            Assert.Equal(2, imported.GetProperty("matchedPreparerCount").GetInt64());
            var reloaded = await LoadAuthorizedStateAsync(host, projectId);
            Assert.Equal(2, reloaded.GetProperty("matchedPreparerCount").GetInt64());
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task GlMatch_ZeroMatchesDoesNotRejectAValidList(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupMappedGlAsync(host, provider);
        var path = WriteSingleIdentifier("NOT-IN-GL");
        try
        {
            var imported = await ImportAsync(host, path);
            Assert.Equal(1, imported.GetProperty("rowCount").GetInt32());
            Assert.Equal(0, imported.GetProperty("matchedPreparerCount").GetInt64());
            var reloaded = await LoadAuthorizedStateAsync(host, projectId);
            Assert.Equal(0, reloaded.GetProperty("matchedPreparerCount").GetInt64());
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    [Theory]
    [InlineData("sqlite", false)]
    [InlineData("duckdb", false)]
    [InlineData("sqlite", true)]
    [InlineData("duckdb", true)]
    public async Task GlMatch_WithoutCommittedCreatorMapping_IsNullNotZero(string provider, bool importGl)
    {
        using var host = new HandlerTestHost();
        string projectId;
        if (importGl)
        {
            projectId = await SetupMappedGlAsync(host, provider, mapCreator: false);
        }
        else
        {
            var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
            {
                caseName = "Synthetic authorization without GL",
                periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
            }));
            projectId = created.GetProperty("projectId").GetString()!;
        }

        var path = WriteList();
        try
        {
            var imported = await ImportAsync(host, path);
            Assert.Equal(JsonValueKind.Null, imported.GetProperty("matchedPreparerCount").ValueKind);
            var loaded = await LoadAuthorizedStateAsync(host, projectId);
            Assert.Equal(JsonValueKind.Null, loaded.GetProperty("matchedPreparerCount").ValueKind);
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task GlMapping_AfterListImportAndRemap_ReturnsFreshAuthorizedState_AndReimportClearsMatch(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupMappedGlAsync(host, provider, mapCreator: false);
        var listPath = WriteList();
        var glPath = CreateGlBuilder().WriteWorkbook();
        try
        {
            await ImportAsync(host, listPath);
            var firstMapping = await CommitMappingAsync(host, "建立人員");
            var firstState = firstMapping.GetProperty("authorizedPreparerState");
            AssertCounts(firstState);
            Assert.Equal(2, firstState.GetProperty("matchedPreparerCount").GetInt64());

            var remapping = await CommitMappingAsync(host, "另一識別欄");
            Assert.Equal(1, remapping.GetProperty("authorizedPreparerState")
                .GetProperty("matchedPreparerCount").GetInt64());
            var remappedState = await LoadAuthorizedStateAsync(host, projectId);
            Assert.Equal(1, remappedState.GetProperty("matchedPreparerCount").GetInt64());

            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath }));
            var reimportedState = await LoadAuthorizedStateAsync(host, projectId);
            AssertCounts(reimportedState);
            Assert.Equal(JsonValueKind.Null, reimportedState.GetProperty("matchedPreparerCount").ValueKind);
        }
        finally
        {
            TestWorkbookBuilder.Delete(listPath);
            TestWorkbookBuilder.Delete(glPath);
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task EmptySelectedIdentifier_RejectsImportAndPreservesPriorListAndStatistics(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupMappedGlAsync(host, provider);
        var validPath = WriteList();
        var emptyPath = WriteSingleIdentifier("   ");
        try
        {
            await ImportAsync(host, validPath);
            var before = await host.DispatchAsync("query.dataPreview", "{\"dataset\":\"authorizedPreparers\"}");
            var error = await Assert.ThrowsAsync<JetActionException>(() => ImportAsync(host, emptyPath));
            Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
            Assert.Equal("sourceColumn", error.Field);
            Assert.Contains("識別欄", error.Message, StringComparison.Ordinal);
            Assert.Contains("重新匯入", error.Message, StringComparison.Ordinal);
            var after = await host.DispatchAsync("query.dataPreview", "{\"dataset\":\"authorizedPreparers\"}");
            Assert.Equal(before.GetProperty("rows").GetRawText(), after.GetProperty("rows").GetRawText());
            var loaded = await LoadAuthorizedStateAsync(host, projectId);
            AssertCounts(loaded);
            Assert.Equal(2, loaded.GetProperty("matchedPreparerCount").GetInt64());
        }
        finally
        {
            TestWorkbookBuilder.Delete(validPath);
            TestWorkbookBuilder.Delete(emptyPath);
        }
    }

    [Theory]
    [InlineData("sqlite", false)]
    [InlineData("duckdb", false)]
    [InlineData("sqlite", true)]
    [InlineData("duckdb", true)]
    public async Task SourceColumn_MissingOrBlank_IsRejectedInsteadOfGuessing(string provider, bool sendBlank)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupMappedGlAsync(host, provider);
        var path = WriteList();
        try
        {
            await ImportAsync(host, path);
            var payload = sendBlank
                ? JsonSerializer.Serialize(new { filePath = path, sourceColumn = "   " })
                : JsonSerializer.Serialize(new { filePath = path });
            var error = await Assert.ThrowsAsync<JetActionException>(() =>
                host.DispatchAsync("import.authorizedPreparer.fromFile", payload));
            Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
            Assert.Equal("sourceColumn", error.Field);
            var loaded = await LoadAuthorizedStateAsync(host, projectId);
            Assert.Equal(3, loaded.GetProperty("rowCount").GetInt32());
            Assert.Equal("員工代碼", loaded.GetProperty("sourceColumn").GetString());
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    private static void AssertCounts(JsonElement state)
    {
        Assert.Equal(3, state.GetProperty("rowCount").GetInt32());
        Assert.Equal(6, state.GetProperty("sourceRowCount").GetInt32());
        Assert.Equal(1, state.GetProperty("blankRowCount").GetInt32());
        Assert.Equal(2, state.GetProperty("duplicateRowCount").GetInt32());
    }

    private static Task<JsonElement> ImportAsync(HandlerTestHost host, string path) =>
        host.DispatchAsync("import.authorizedPreparer.fromFile", JsonSerializer.Serialize(new
        { filePath = path, sourceColumn = "員工代碼" }));

    private static async Task<JsonElement> LoadAuthorizedStateAsync(HandlerTestHost host, string projectId)
    {
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        return loaded.GetProperty("importState").GetProperty("authorizedPreparer");
    }

    private static InlineGlWorkbookBuilder CreateGlBuilder() => new InlineGlWorkbookBuilder()
        .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "金額", "借方旗標", "建立人員", "另一識別欄", "摘要")
        .AddRow("AUTH-1", "2025-03-01", "1101", "合成科目", "1", 1, " e01 ", "E09", "Synthetic 1")
        .AddRow("AUTH-2", "2025-03-01", "1101", "合成科目", "1", 1, "E01", "E09", "Synthetic 2")
        .AddRow("AUTH-3", "2025-03-01", "1101", "合成科目", "1", 1, "E02", "OTHER", "Synthetic 3")
        .AddRow("AUTH-4", "2025-03-01", "1101", "合成科目", "1", 1, "E03", "OTHER", "Synthetic 4");

    private static async Task<string> SetupMappedGlAsync(HandlerTestHost host, string provider, bool mapCreator = true)
    {
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = "Synthetic authorization statistics",
            periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        var path = CreateGlBuilder().WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            await CommitMappingAsync(host, mapCreator ? "建立人員" : null);
        }
        finally { TestWorkbookBuilder.Delete(path); }
        return created.GetProperty("projectId").GetString()!;
    }

    private static Task<JsonElement> CommitMappingAsync(HandlerTestHost host, string? creatorColumn)
    {
        var mapping = CreateGlBuilder().BuildFlagModeMapping();
        mapping.Remove("createBy");
        if (creatorColumn is not null) mapping["createBy"] = creatorColumn;
        return host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new { mapping, amountMode = "flag" }));
    }

    private static string WriteList() => TestWorkbookBuilder.WriteWorkbook(sheet =>
    {
        sheet.Cell(1, 1).Value = "員工代碼";
        sheet.Cell(1, 2).Value = "備註";
        var identifiers = new Dictionary<int, string>
        {
            [2] = "E01", [3] = " e01 ", [4] = " e02 ", [5] = "", [6] = "E09", [8] = "E02"
        };
        foreach (var (row, identifier) in identifiers)
        {
            sheet.Cell(row, 1).Value = identifier;
            sheet.Cell(row, 2).Value = "Synthetic row";
        }
        // 第 7 列完全空白，不計入讀到的資料列；第 5 列只有選取欄空白，必須計入略過空白。
    });

    private static string WriteSingleIdentifier(string identifier) => TestWorkbookBuilder.WriteWorkbook(sheet =>
    {
        sheet.Cell(1, 1).Value = "員工代碼";
        sheet.Cell(1, 2).Value = "備註";
        sheet.Cell(2, 1).Value = identifier;
        sheet.Cell(2, 2).Value = "Synthetic row";
    });
}
