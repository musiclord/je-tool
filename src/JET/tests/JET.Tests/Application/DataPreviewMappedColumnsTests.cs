using System.Text.Json;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 資料預覽的 GL 與 GL(未納入測試) 頁依已確認的配對列欄位（預篩選收斂計畫階段五，使用者 2026-10-22 裁定）：
/// 核心欄位之後接已配對的選填欄位與攸關資料元素欄位，未配對的不列，借方與貸方金額不分欄；兩頁列同一組欄位，
/// GL(未納入測試) 最後多一欄未納入原因。欄名和欄位配對畫面相同，回應的 columnDetails 逐欄寫出名稱與型別。
/// 金額照資料庫存的值換算，保留到小數第四位；來源超過四位時在確認配對時四捨五入。
/// oracle：下列每個合成來源逐列手算的固定答案；四捨五入的兩格另外直接讀資料庫存的整數核對。
/// 第一次失敗：Focused 收據 20261023-112004700-2e000bb010d54098b6c123ef7f06faf0。
/// </summary>
public sealed class DataPreviewMappedColumnsTests
{
    private static readonly (string Key, string Label, string ValueType)[] CoreColumns =
    [
        ("documentNumber", "傳票號碼", "text"),
        ("lineItem", "傳票文件項次", "text"),
        ("postDate", "總帳日期", "date"),
        ("accountCode", "會計科目編號", "text"),
        ("accountName", "會計科目名稱", "text"),
        ("documentDescription", "傳票摘要", "text"),
        ("amount", "金額", "money"),
        ("drCr", "借貸別", "text")
    ];

    private static readonly (string Key, string Label, string ValueType) ExclusionReasonColumn =
        ("exclusionReason", "未納入原因", "text");

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AllOptionalFieldsAndThreeRdeTypes_BothPagesListTheSameMappedColumnsWithStoredValues(string provider)
    {
        using var host = new HandlerTestHost();
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns(
                "傳票號碼", "傳票項次", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "來源模組",
                "建立人員", "核准人員", "人工傳票", "金額", "借方旗標", "過帳狀態", "合成部門", "合成到期日", "合成外幣金額")
            .AddRow("JV-001", "1", "2025-03-01", "2025-03-02", "1101", "現金", "合成摘要甲", "GL",
                "編製甲", "核准甲", "1", "1234.5678", "1", "POSTED", "財務部", "2025-04-30", "12.34")
            .AddRow("JV-001", "2", "2025-03-01", "2025-03-02", "4101", "銷貨收入", "合成摘要甲", "GL",
                "編製甲", "核准甲", "0", "1000", "0", "POSTED", null, null, null)
            .AddRow("JV-001", "3", "2025-03-01", "2025-03-02", "2101", "應付帳款", "合成摘要甲", "GL",
                "編製甲", "核准甲", "0", "234.5678", "0", "POSTED", "業務部", null, "-5")
            // 來源金額有五位小數：確認配對時四捨五入到第四位，0.12345 存成 1235，攸關資料元素的 0.98765 存成 9877。
            .AddRow("JV-002", "1", "2025-06-15", "2025-06-16", "6100", "其他費用", "草稿分錄", "AP",
                "編製乙", "核准乙", "1", "0.12345", "1", "DRAFT", null, "2025-07-31", "0.98765")
            .AddRow("JV-002", "2", "2025-06-15", "2025-06-16", "2101", "應付帳款", "草稿分錄", "AP",
                "編製乙", "核准乙", "1", "0.12345", "0", "DRAFT", null, null, null)
            .AddRow("JV-003", "1", "2026-01-05", "2026-01-06", "1101", "現金", "期後分錄", "GL",
                "編製甲", "核准甲", "0", "500", "1", "POSTED", null, null, null);
        var projectId = await CreateAndImportAsync(host, builder, provider);

        var mapping = builder.BuildFlagModeMapping();
        mapping["postingStatus"] = "過帳狀態";
        var committed = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping,
            amountMode = "flag",
            approvalDateMode = "mapped",
            manualAutoPolicy = new { manualValues = new[] { "1" }, automaticValues = new[] { "0" } },
            postingStatusPolicy = new { acceptedValues = new[] { "POSTED" }, includeBlank = false },
            rdeFields = new object[]
            {
                new { sourceColumn = "合成部門", label = "合成部門", valueType = "text" },
                new { sourceColumn = "合成到期日", label = "合成到期日", valueType = "date" },
                new { sourceColumn = "合成外幣金額", label = "合成外幣金額", valueType = "money" }
            }
        }));
        var rde = RdeFieldIds(committed);

        (string Key, string Label, string ValueType)[] expectedColumns =
        [
            .. CoreColumns,
            ("approvalDate", "傳票核准日", "date"),
            ("sourceModule", "分錄來源模組", "text"),
            ("createdBy", "傳票建立人員", "text"),
            ("approvedBy", "傳票核准人員", "text"),
            ("manualAuto", "人工/自動分錄", "text"),
            ("postingStatus", "過帳狀態", "text"),
            (rde["合成部門"], "合成部門", "text"),
            (rde["合成到期日"], "合成到期日", "date"),
            (rde["合成外幣金額"], "合成外幣金額", "money")
        ];

        var gl = await host.DispatchAsync("query.dataPreview", """{ "dataset": "gl" }""");
        AssertColumns(gl, expectedColumns);
        Assert.Equal(3, gl.GetProperty("totalCount").GetInt64());
        Assert.Equal(
            new[]
            {
                new string?[] { "JV-001", "1", "2025-03-01", "1101", "現金", "合成摘要甲", "1234.5678", "DEBIT", "2025-03-02",
                    "GL", "編製甲", "核准甲", "manual", "POSTED", "財務部", "2025-04-30", "12.34" },
                new string?[] { "JV-001", "2", "2025-03-01", "4101", "銷貨收入", "合成摘要甲", "-1000", "CREDIT", "2025-03-02",
                    "GL", "編製甲", "核准甲", "automatic", "POSTED", null, null, null },
                new string?[] { "JV-001", "3", "2025-03-01", "2101", "應付帳款", "合成摘要甲", "-234.5678", "CREDIT", "2025-03-02",
                    "GL", "編製甲", "核准甲", "automatic", "POSTED", "業務部", null, "-5" }
            },
            Rows(gl));
        // 統計列的金額和明細一樣用字串照存下的值傳，畫面補成四位小數時不經過浮點數。
        var stats = gl.GetProperty("stats");
        Assert.Equal("234.5678", stats.GetProperty("amountAbsMin").GetString());
        Assert.Equal("1234.5678", stats.GetProperty("amountAbsMax").GetString());
        Assert.Equal(1, stats.GetProperty("voucherCount").GetInt64());

        var excluded = await host.DispatchAsync("query.dataPreview", """{ "dataset": "glExcluded" }""");
        AssertColumns(excluded, [.. expectedColumns, ExclusionReasonColumn]);
        Assert.Equal(
            new[]
            {
                new string?[] { "JV-002", "1", "2025-06-15", "6100", "其他費用", "草稿分錄", "0.1235", "DEBIT", "2025-06-16",
                    "AP", "編製乙", "核准乙", "manual", "DRAFT", null, "2025-07-31", "0.9877", "postingStatus" },
                new string?[] { "JV-002", "2", "2025-06-15", "2101", "應付帳款", "草稿分錄", "-0.1235", "CREDIT", "2025-06-16",
                    "AP", "編製乙", "核准乙", "manual", "DRAFT", null, null, null, "postingStatus" },
                new string?[] { "JV-003", "1", "2026-01-05", "1101", "現金", "期後分錄", "500", "DEBIT", "2026-01-06",
                    "GL", "編製甲", "核准甲", "automatic", "POSTED", null, null, null, "afterPeriod" }
            },
            Rows(excluded));

        // 預覽的值就是資料庫存的整數除以 10,000，不在讀出時另外進位。
        var database = DatabaseFor(provider, host);
        Assert.Equal(1235L, await ScalarAsync(database, projectId,
            "SELECT amount_scaled FROM target_gl_entry WHERE document_number = 'JV-002' AND line_item = '1';"));
        Assert.Equal(9877L, await ScalarAsync(database, projectId,
            "SELECT r.amount_scaled FROM target_gl_rde_value r JOIN target_gl_entry e ON e.entry_id = r.entry_id " +
            "WHERE e.document_number = 'JV-002' AND e.line_item = '1' AND r.value_type = 'money';"));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SomeOptionalFieldsMapped_ListsOnlyThoseIncludingDerivedApprovalDateAndAutoNumberedLineItem(string provider)
    {
        using var host = new HandlerTestHost();
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標", "合成到期日")
            .AddRow("JV-101", "2025-02-01", "1101", "現金", "部分配對", "編製丙", "100", "1", "2025-02-28")
            .AddRow("JV-101", "2025-02-01", "4101", "銷貨收入", "部分配對", "編製丙", "100", "0", null)
            .AddRow("JV-102", "2024-12-31", "1101", "現金", "期前分錄", "編製丙", "50", "1", "2025-01-15");
        await CreateAndImportAsync(host, builder, provider);

        // 傳票文件項次沒有來源欄，逐張傳票自動編號；傳票核准日選「與總帳日期相同」。兩者都算已配對。
        var committed = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = builder.BuildFlagModeMapping(),
            amountMode = "flag",
            approvalDateMode = "sameAsPostDate",
            rdeFields = new object[] { new { sourceColumn = "合成到期日", label = "合成到期日", valueType = "date" } }
        }));
        var rde = RdeFieldIds(committed);

        (string Key, string Label, string ValueType)[] expectedColumns =
        [
            .. CoreColumns,
            ("approvalDate", "傳票核准日", "date"),
            ("createdBy", "傳票建立人員", "text"),
            (rde["合成到期日"], "合成到期日", "date")
        ];

        var gl = await host.DispatchAsync("query.dataPreview", """{ "dataset": "gl" }""");
        AssertColumns(gl, expectedColumns);
        Assert.Equal(
            new[]
            {
                new string?[] { "JV-101", "1", "2025-02-01", "1101", "現金", "部分配對", "100", "DEBIT", "2025-02-01", "編製丙", "2025-02-28" },
                new string?[] { "JV-101", "2", "2025-02-01", "4101", "銷貨收入", "部分配對", "-100", "CREDIT", "2025-02-01", "編製丙", null }
            },
            Rows(gl));

        var excluded = await host.DispatchAsync("query.dataPreview", """{ "dataset": "glExcluded" }""");
        AssertColumns(excluded, [.. expectedColumns, ExclusionReasonColumn]);
        Assert.Equal(
            new[]
            {
                new string?[] { "JV-102", "1", "2024-12-31", "1101", "現金", "期前分錄", "50", "DEBIT", "2024-12-31", "編製丙",
                    "2025-01-15", "beforePeriod" }
            },
            Rows(excluded));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task NoOptionalFieldMapped_BothPagesListOnlyTheCoreColumns(string provider)
    {
        using var host = new HandlerTestHost();
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-201", "2025-05-01", "1101", "現金", "未配對選填", "80", "1")
            .AddRow("JV-201", "2025-05-01", "2101", "應付帳款", "未配對選填", "80", "0")
            .AddRow("JV-202", null, "1101", "現金", "日期空白", "10", "1");
        await CreateAndImportAsync(host, builder, provider);
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = builder.BuildFlagModeMapping(),
            amountMode = "flag"
        }));

        // 人工或自動分錄沒有配對：兩頁都不列這一欄。
        var gl = await host.DispatchAsync("query.dataPreview", """{ "dataset": "gl" }""");
        AssertColumns(gl, CoreColumns);
        Assert.Equal(
            new[]
            {
                new string?[] { "JV-201", "1", "2025-05-01", "1101", "現金", "未配對選填", "80", "DEBIT" },
                new string?[] { "JV-201", "2", "2025-05-01", "2101", "應付帳款", "未配對選填", "-80", "CREDIT" }
            },
            Rows(gl));

        var excluded = await host.DispatchAsync("query.dataPreview", """{ "dataset": "glExcluded" }""");
        AssertColumns(excluded, [.. CoreColumns, ExclusionReasonColumn]);
        Assert.Equal(
            new[] { new string?[] { "JV-202", "1", null, "1101", "現金", "日期空白", "10", "DEBIT", "blankPostDate" } },
            Rows(excluded));
    }

    private static async Task<string> CreateAndImportAsync(HandlerTestHost host, InlineGlWorkbookBuilder builder, string provider)
    {
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "PREVIEW-2025-001",
            entityName = "合成案件",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = provider
        }));
        var filePath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(filePath);
        }

        return created.GetProperty("projectId").GetString()!;
    }

    private static Dictionary<string, string> RdeFieldIds(JsonElement committed) =>
        committed.GetProperty("rdeFields").EnumerateArray().ToDictionary(
            field => field.GetProperty("sourceColumn").GetString()!,
            field => field.GetProperty("fieldId").GetString()!,
            StringComparer.Ordinal);

    private static void AssertColumns(JsonElement preview, IReadOnlyList<(string Key, string Label, string ValueType)> expected)
    {
        Assert.Equal(expected.Select(column => column.Key), preview.GetProperty("columns").EnumerateArray().Select(column => column.GetString()));
        var details = preview.GetProperty("columnDetails").EnumerateArray().ToArray();
        Assert.All(details, detail => JsonShape.HasExactKeys(detail, "key", "label", "valueType"));
        Assert.Equal(
            expected,
            details.Select(detail => (
                detail.GetProperty("key").GetString()!,
                detail.GetProperty("label").GetString()!,
                detail.GetProperty("valueType").GetString()!)));
    }

    private static string?[][] Rows(JsonElement preview) =>
        preview.GetProperty("rows").EnumerateArray()
            .Select(row => row.EnumerateArray().Select(cell => cell.ValueKind == JsonValueKind.Null ? null : cell.GetString()).ToArray())
            .ToArray();

    private static ILocalProjectDatabase DatabaseFor(string provider, HandlerTestHost host) => provider switch
    {
        "sqlite" => new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)),
        "duckdb" => new DuckDbProjectDatabase(new JetProjectFolder(host.ProjectsRoot)),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    private static async Task<long> ScalarAsync(ILocalProjectDatabase database, string projectId, string sql)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
