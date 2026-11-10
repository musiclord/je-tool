using System.Text;
using System.Text.Json;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 重新匯入 GL 或 TB 之後、還沒重新確認以前，重開案件要帶回上次確認的配對當草稿；
/// 草稿裡的攸關資料元素欄位身分要能原樣送回，已儲存情境裡指到這些欄位的條件才對得上。資料全是合成的。
/// </summary>
public sealed class MappingReimportDraftTests
{
    private static readonly string[] GlHeader = ["Document", "Date", "Account", "Name", "Description", "Amount", "Dept"];
    private static readonly string[] TbHeader = ["Account", "Name", "Change"];

    [Theory]
    [InlineData("sqlite")]
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
        Assert.Equal("Document", previousGl.GetProperty("mapping").GetProperty("voucherNumber").GetString());
        Assert.Equal("signed", previousGl.GetProperty("amountMode").GetString());
        var previousRde = Assert.Single(previousGl.GetProperty("rdeFields").EnumerateArray());
        Assert.Equal(fieldId, previousRde.GetProperty("fieldId").GetString());
        Assert.Equal("Dept", previousRde.GetProperty("sourceColumn").GetString());
        var previousTb = reopened.GetProperty("previousMapping").GetProperty("tb");
        Assert.Equal("Account", previousTb.GetProperty("mapping").GetProperty("accountNumber").GetString());
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

    /// <summary>
    /// 上次確認的配對只用來帶回草稿，讀不出來時當作沒有（LocalMappingStateStore.FindPreviousAsync 的註解）：
    /// 日後配對格式改版或內容被改壞，開案照常，GL 從空白草稿開始，TB 那一份照常帶回，GL 也照常能確認配對。
    /// 每一列把 GL 那一份的一個欄位改成讀不出來的內容：選項 JSON 語法錯誤、多了認不得的設定、含不成對的代理字元或整格空白，
    /// 配對 JSON 語法錯誤或含不成對的代理字元、確認時間不是日期，以及格式版本是舊版或大到超過整數範圍。這是新增的測試。
    /// </summary>
    [Theory]
    [InlineData("options_json", "{")]
    [InlineData("options_json", """{"approvalDateMode":"unmapped","postingStatusPolicy":null,"manualAutoPolicy":{"manualValues":["1"],"automaticValues":["0"]},"rdeFields":[],"futureOption":true}""")]
    [InlineData("options_json", """{"approvalDateMode":"\ud800","postingStatusPolicy":null,"manualAutoPolicy":{"manualValues":["1"],"automaticValues":["0"]},"rdeFields":[]}""")]
    [InlineData("options_json", null)]
    [InlineData("mapping_json", "{")]
    [InlineData("mapping_json", """{"voucherNumber":"\ud800"}""")]
    [InlineData("committed_utc", "not a date")]
    [InlineData("format_version", 1)]
    [InlineData("format_version", 1099511627776L)]
    public async Task UnreadablePreviousGlMapping_IsTreatedAsMissingWhenOpeningAndConfirming(string column, object? storedValue)
    {
        using var host = new HandlerTestHost();
        var gl = WriteCsv(host, "gl.csv", GlHeader, GlRows("Sales"));
        var tb = WriteCsv(host, "tb.csv", TbHeader, [["1101", "Cash", "0"]]);
        var projectId = await CreateAsync(host, "sqlite");
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = gl }));
        await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tb }));
        await host.DispatchAsync("mapping.commit.gl", GlCommitPayload(fieldId: null));
        await host.DispatchAsync("mapping.commit.tb", TbCommitPayload);
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = gl }));
        await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tb }));

        ILocalProjectDatabase database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // column 只來自上面固定的幾個欄名。
            command.CommandText = $"UPDATE field_mapping_previous SET {column} = @value WHERE dataset_kind = 'gl';";
            command.AddWithValue("@value", storedValue);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var reopened = await LoadAsync(host, projectId);

        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("mapping").GetProperty("gl").ValueKind);
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("previousMapping").GetProperty("gl").ValueKind);
        Assert.Equal(
            "Account",
            reopened.GetProperty("previousMapping").GetProperty("tb").GetProperty("mapping").GetProperty("accountNumber").GetString());

        // 確認 GL 配對時也會讀上次確認的那一份（沿用攸關資料元素欄位身分），讀不出來時照樣發新的身分。
        var recommitted = await host.DispatchAsync("mapping.commit.gl", GlCommitPayload(fieldId: null));
        Assert.False(string.IsNullOrWhiteSpace(recommitted.GetProperty("rdeFields")[0].GetProperty("fieldId").GetString()));
    }

    [Theory]
    [InlineData("sqlite")]
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
                    voucherNumber = "Document", glDate = "Date", accountNumber = "Account", accountName = "Name",
                    description = "Description", amount = "Amount", isManual = "Flag"
                }
            })));

        Assert.Equal(JET.Domain.JetErrorCodes.ProjectionFailed, error.Code);
        // 預期值換成裁定 2026-10-13-28 的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
        Assert.StartsWith("有 3 列的值無法讀取，本次配對未儲存。", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("「」", error.Message, StringComparison.Ordinal);
        Assert.Collection(
            error.Details!,
            unlisted =>
            {
                Assert.Equal("Flag", unlisted.SourceColumn);
                Assert.StartsWith("來源欄「Flag」有 2 列未歸類為人工或自動：「Adjust」在第 2、3 列。", unlisted.Message, StringComparison.Ordinal);
            },
            blank =>
            {
                Assert.Equal("Flag", blank.SourceColumn);
                Assert.StartsWith("來源欄「Flag」有 1 列是空白：第 4 列。", blank.Message, StringComparison.Ordinal);
            });
    }

    private static async Task<string> CreateAsync(HandlerTestHost host, string provider)
    {
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectName = "Synthetic reimport", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
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
            voucherNumber = "Document", glDate = "Date", accountNumber = "Account", accountName = "Name",
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
        mapping = new { accountNumber = "Account", accountName = "Name", changeAmount = "Change" }
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
