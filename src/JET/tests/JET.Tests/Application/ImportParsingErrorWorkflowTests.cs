using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class ImportParsingErrorWorkflowTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var provider in new[] { "sqlite", "duckdb" })
        foreach (var kind in new[] { "amount", "glDate", "rdeAmount", "rdeDate" }) yield return [provider, kind];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task InvalidMoneyOrSerialAcrossSources_ReportsAllRowsAndPreservesPriorState(string provider, string kind)
    {
        using var fixture = await MappingCaseTemplateFixture.CreateAsync(provider, manyRows: true);
        var before = await fixture.LoadAsync();
        var mapping = MappingCaseTemplateFixture.Mapping();
        object[] rde = [];
        var source = kind is "amount" or "rdeAmount" ? "BadAmount" : "BadDate";
        if (kind.StartsWith("rde", StringComparison.Ordinal))
            rde = [new { sourceColumn = source, label = "Synthetic RDE", valueType = kind == "rdeAmount" ? "amount" : "date" }];
        else mapping[kind] = source;

        var error = await Assert.ThrowsAsync<JetActionException>(() => fixture.CommitAsync(mapping, "signed", rde));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        // 預期值換成裁定 2026-10-13-28 確認的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
        Assert.StartsWith("有 65 列的值無法讀取，本次配對未儲存。", error.Message, StringComparison.Ordinal);
        Assert.Contains("以下僅列出部分的值與列號。", error.Message, StringComparison.Ordinal);
        var detail = Assert.Single(error.Details!);
        Assert.Equal(source, detail.SourceColumn);
        if (source == "BadAmount") Assert.Contains("second.xlsx 第 2、3、4、5、6 列", detail.Message, StringComparison.Ordinal);
        var after = await fixture.LoadAsync();
        Assert.Equal(before.GetProperty("mapping").GetRawText(), after.GetProperty("mapping").GetRawText());
        Assert.Equal(before.GetProperty("validationResult").GetRawText(), after.GetProperty("validationResult").GetRawText());
        Assert.Equal(before.GetProperty("prescreenResult").GetRawText(), after.GetProperty("prescreenResult").GetRawText());
        Assert.Equal(65, await fixture.ScalarAsync("SELECT COUNT(*) FROM gl;"));
        Assert.Equal(650_000, await fixture.ScalarAsync("SELECT CAST(SUM(amount_scaled) AS BIGINT) FROM gl;"));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TbMalformedGrouping_ReportsSourceRowAndPreservesExistingProjection(string provider)
    {
        using var fixture = await MappingCaseTemplateFixture.CreateAsync(provider);
        var before = await fixture.LoadAsync();
        var error = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync("mapping.commit.tb",
            JsonSerializer.Serialize(new { changeMode = "direct", mapping = new { accountNumber = "Account", accountName = "Name", changeAmount = "Bad" } })));
        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        var detail = Assert.Single(error.Details!);
        Assert.Equal("Bad", detail.SourceColumn);
        Assert.Contains("第 2 列", detail.Message, StringComparison.Ordinal);
        Assert.Equal(20_000, await fixture.ScalarAsync("SELECT change_amount_scaled FROM tb;"));
        var after = await fixture.LoadAsync();
        Assert.Equal(before.GetProperty("mapping").GetRawText(), after.GetProperty("mapping").GetRawText());
        Assert.Equal(before.GetProperty("validationResult").GetRawText(), after.GetProperty("validationResult").GetRawText());
        Assert.Equal(before.GetProperty("prescreenResult").GetRawText(), after.GetProperty("prescreenResult").GetRawText());
    }
}
