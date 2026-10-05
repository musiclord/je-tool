using System.Text.Json;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 2026-10-05 最後獨立複審 V3：人工/自動分錄選「只列一側」時，清單上留著的代碼如果在來源裡一筆都沒有，
/// 其他非空白的值會全部歸到另一側，用到人工分錄的條件默默變成 0 筆或幾乎全部。
/// 依 Q5「確認配對前也提醒」與 V3 裁定，確認欄位配對後要給不擋的提醒，寫出代碼、來源欄與下一步；
/// 清單代碼有出現在來源時不提醒。預期文字逐字寫在這裡，不由產品程式組出。
/// </summary>
public sealed class ManualAutoListedCodeWarningTests
{
    private const string ListsManualMissing =
        "人工分錄代碼「1」在來源欄「人工傳票」裡找不到。其他非空白的值都算成自動分錄，用到人工分錄的條件可能會是 0 筆。"
        + "如果代碼不對，請修改代碼後再確認一次欄位配對。";

    private const string ListsAutomaticMissing =
        "自動分錄代碼「0、N」在來源欄「人工傳票」裡找不到。其他非空白的值都算成人工分錄，用到人工分錄的條件可能會列出幾乎全部分錄。"
        + "如果代碼不對，請修改代碼後再確認一次欄位配對。";

    private static async Task<JsonElement> CommitAsync(string provider, object manualAutoPolicy)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "V3-2025",
            entityName = "合成只列一側",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            lastPeriodStart = "2025-12-31",
            databaseProvider = provider
        }));
        var gl = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "人工傳票", "金額", "借方旗標")
            .AddRow("JV-1", "2025-03-05", "1101", "現金", "合成", " m ", "100.00", 1)
            .AddRow("JV-1", "2025-03-05", "4101", "收入", "合成", "A", "100.00", 0)
            .AddRow("JV-2", "2025-04-05", "1101", "現金", "合成", null, "50.00", 1)
            .AddRow("JV-2", "2025-04-05", "4101", "收入", "合成", "A", "50.00", 0);
        var path = gl.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }

        return await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = gl.BuildFlagModeMapping(),
            amountMode = "flag",
            manualAutoPolicy
        }));
    }

    private static string[] Warnings(JsonElement committed) =>
        committed.GetProperty("warnings").EnumerateArray().Select(static item => item.GetString()!).ToArray();

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ListsManualOnly_CodeAbsentFromSource_WarnsWithoutBlocking(string provider)
    {
        var committed = await CommitAsync(provider, new
        {
            manualValues = new[] { "1" },
            automaticValues = Array.Empty<string>(),
            unlistedValueKind = "automatic",
            blankValueKind = "automatic"
        });

        Assert.True(committed.GetProperty("ok").GetBoolean());
        Assert.Equal(4, committed.GetProperty("projectedRowCount").GetInt32());
        Assert.Equal([ListsManualMissing], Warnings(committed));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ListsAutomaticOnly_CodesAbsentFromSource_WarnsWithEveryListedCode(string provider)
    {
        var committed = await CommitAsync(provider, new
        {
            manualValues = Array.Empty<string>(),
            automaticValues = new[] { "0", "N" },
            unlistedValueKind = "manual",
            blankValueKind = "manual"
        });

        Assert.True(committed.GetProperty("ok").GetBoolean());
        Assert.Equal([ListsAutomaticMissing], Warnings(committed));
    }

    /// <summary>來源的「 m 」去頭尾空白、不分大小寫後就是清單上的「M」，與投影的比對方式相同，所以不提醒。</summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ListedCodePresentInSource_DoesNotWarn(string provider)
    {
        var listsManual = await CommitAsync(provider, new
        {
            manualValues = new[] { "1", "M" },
            automaticValues = Array.Empty<string>(),
            unlistedValueKind = "automatic",
            blankValueKind = "automatic"
        });
        Assert.Empty(Warnings(listsManual));

        // 逐值指定時每個非空白值都要歸類，清單上多一個沒用到的代碼不影響結果，也不提醒。
        var perValue = await CommitAsync(provider, new
        {
            manualValues = new[] { "M", "1" },
            automaticValues = new[] { "A" },
            unlistedValueKind = "reject",
            blankValueKind = "automatic"
        });
        Assert.Empty(Warnings(perValue));
    }
}
