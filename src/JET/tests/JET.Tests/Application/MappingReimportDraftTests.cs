using System.Text;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 2026-10-03 O3：重新匯入 GL 或 TB 之後、還沒重新確認以前，重開案件要帶回上次確認的配對當草稿；
/// 草稿裡的攸關資料元素欄位身分要能原樣送回，已儲存情境裡指到這些欄位的條件才對得上。資料全是合成的。
/// </summary>
public sealed class MappingReimportDraftTests
{
    private static readonly string[] GlHeader = ["Document", "Date", "Account", "Name", "Description", "Amount", "Dept"];
    private static readonly string[] TbHeader = ["Account", "Name", "Change"];

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Reimport_ThenReopen_ReturnsLastConfirmedMappingAsDraftAndKeepsRdeFieldIds(string provider)
    {
        using var host = new HandlerTestHost();
        var gl = WriteCsv(host, "gl.csv", GlHeader, GlRows("Sales"));
        var tb = WriteCsv(host, "tb.csv", TbHeader, [["1101", "Cash", "0"]]);
        var projectId = await CreateAsync(host, provider);

        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = gl }));
        await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tb }));
        var committed = await host.DispatchAsync("mapping.commit.gl", GlCommitPayload(fieldId: null));
        var fieldId = committed.GetProperty("rdeFields")[0].GetProperty("fieldId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fieldId));
        await host.DispatchAsync("mapping.commit.tb", TbCommitPayload);

        // 有效配對存在時不帶上次的配對。
        var before = await LoadAsync(host, projectId);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("previousMapping").GetProperty("gl").ValueKind);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("previousMapping").GetProperty("tb").ValueKind);

        // 取代匯入兩次都不確認：第二次匯入時已沒有有效配對，上次確認的那份不能被清掉。
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = gl }));
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = gl }));
        await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tb }));

        var reopened = await LoadAsync(host, projectId);
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("mapping").GetProperty("gl").ValueKind);
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("mapping").GetProperty("tb").ValueKind);
        var previousGl = reopened.GetProperty("previousMapping").GetProperty("gl");
        Assert.Equal("Document", previousGl.GetProperty("mapping").GetProperty("docNum").GetString());
        Assert.Equal("signed", previousGl.GetProperty("amountMode").GetString());
        var previousRde = Assert.Single(previousGl.GetProperty("rdeFields").EnumerateArray());
        Assert.Equal(fieldId, previousRde.GetProperty("fieldId").GetString());
        Assert.Equal("Dept", previousRde.GetProperty("sourceColumn").GetString());
        var previousTb = reopened.GetProperty("previousMapping").GetProperty("tb");
        Assert.Equal("Account", previousTb.GetProperty("mapping").GetProperty("accNum").GetString());
        Assert.Equal("direct", previousTb.GetProperty("changeMode").GetString());

        // 草稿原樣送回：沿用上次發出的欄位身分，後端接受，身分不變。
        var recommitted = await host.DispatchAsync("mapping.commit.gl", GlCommitPayload(fieldId));
        Assert.Equal(fieldId, recommitted.GetProperty("rdeFields")[0].GetProperty("fieldId").GetString());
        await host.DispatchAsync("mapping.commit.tb", TbCommitPayload);

        var after = await LoadAsync(host, projectId);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("previousMapping").GetProperty("gl").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("previousMapping").GetProperty("tb").ValueKind);
        Assert.Equal(
            fieldId,
            after.GetProperty("mapping").GetProperty("gl").GetProperty("rdeFields")[0].GetProperty("fieldId").GetString());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task FieldIdThatWasNeverIssued_IsStillRejectedAfterReimport(string provider)
    {
        using var host = new HandlerTestHost();
        var gl = WriteCsv(host, "gl.csv", GlHeader, GlRows("Sales"));
        await CreateAsync(host, provider);
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = gl }));
        await host.DispatchAsync("mapping.commit.gl", GlCommitPayload(fieldId: null));
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = gl }));

        // 沿用的只有上次確認時發出的身分；自己編的身分照舊拒絕。
        var error = await Assert.ThrowsAsync<JET.Domain.JetActionException>(() =>
            host.DispatchAsync("mapping.commit.gl", GlCommitPayload("rde.0123456789abcdef0123456789abcdef")));
        Assert.Equal(JET.Domain.JetErrorCodes.InvalidPayload, error.Code);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ManualAutoFailure_GroupsRowsByValueAndNamesTheSourceColumn(string provider)
    {
        using var host = new HandlerTestHost();
        string[] header = ["Document", "Date", "Account", "Name", "Description", "Amount", "Flag"];
        var gl = WriteCsv(host, "gl.csv", header,
        [
            ["JV-1", "2025-03-01", "1101", "Cash", "Synthetic", "10", "Adjust"],
            ["JV-1", "2025-03-01", "4101", "Revenue", "Synthetic", "-10", "Adjust"],
            ["JV-2", "2025-03-02", "1101", "Cash", "Synthetic", "5", ""],
            ["JV-2", "2025-03-02", "4101", "Revenue", "Synthetic", "-5", "1"]
        ]);
        await CreateAsync(host, provider);
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = gl }));

        var error = await Assert.ThrowsAsync<JET.Domain.JetActionException>(() =>
            host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
            {
                amountMode = "signed",
                mapping = new
                {
                    docNum = "Document", postDate = "Date", accNum = "Account", accName = "Name",
                    description = "Description", amount = "Amount", manual = "Flag"
                }
            })));

        Assert.Equal(JET.Domain.JetErrorCodes.ProjectionFailed, error.Code);
        Assert.StartsWith("3 列無法轉換，系統沒有儲存這次配對結果。", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("「」", error.Message, StringComparison.Ordinal);
        Assert.Collection(
            error.Details!,
            unlisted =>
            {
                Assert.Equal("Flag", unlisted.SourceColumn);
                Assert.StartsWith("欄位「Flag」有 2 列未歸類為人工或自動：「Adjust」在第 2、3 列。", unlisted.Message, StringComparison.Ordinal);
            },
            blank =>
            {
                Assert.Equal("Flag", blank.SourceColumn);
                Assert.StartsWith("欄位「Flag」有 1 列是空白：第 4 列。", blank.Message, StringComparison.Ordinal);
            });
    }

    private static async Task<string> CreateAsync(HandlerTestHost host, string provider)
    {
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = "Synthetic reimport", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        return created.GetProperty("projectId").GetString()!;
    }

    private static Task<JsonElement> LoadAsync(HandlerTestHost host, string projectId) =>
        host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));

    private static string GlCommitPayload(string? fieldId) => JsonSerializer.Serialize(new
    {
        amountMode = "signed",
        mapping = new
        {
            docNum = "Document", postDate = "Date", accNum = "Account", accName = "Name",
            description = "Description", amount = "Amount"
        },
        rdeFields = new object[]
        {
            fieldId is null
                ? new { sourceColumn = "Dept", label = "部門", valueType = "text" }
                : new { fieldId, sourceColumn = "Dept", label = "部門", valueType = "text" }
        }
    });

    private static readonly string TbCommitPayload = JsonSerializer.Serialize(new
    {
        changeMode = "direct",
        mapping = new { accNum = "Account", accName = "Name", amount = "Change" }
    });

    private static string[][] GlRows(string dept) =>
    [
        ["JV-1", "2025-03-01", "1101", "Cash", "Synthetic debit", "12.34", dept],
        ["JV-1", "2025-03-01", "4101", "Revenue", "Synthetic credit", "-12.34", dept]
    ];

    private static string WriteCsv(HandlerTestHost host, string fileName, string[] header, string[][] rows)
    {
        var folder = Path.Combine(host.ProjectsRoot, "source");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        var lines = new[] { string.Join(",", header) }.Concat(rows.Select(row => string.Join(",", row)));
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}
