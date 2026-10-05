using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9ParsingWorkflowTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var provider in new[] { "sqlite", "duckdb" })
        foreach (var kind in new[] { "amount", "postDate", "rdeAmount", "rdeDate" }) yield return [provider, kind];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task InvalidMoneyOrSerialAcrossSources_ReportsAllRowsAndPreservesPriorState(string provider, string kind)
    {
        using var fixture = await Batch9MappingTestFixture.CreateAsync(provider, manyRows: true);
        var before = await fixture.LoadAsync();
        var mapping = Batch9MappingTestFixture.Mapping();
        object[] rde = [];
        var source = kind is "amount" or "rdeAmount" ? "BadAmount" : "BadDate";
        if (kind.StartsWith("rde", StringComparison.Ordinal))
            rde = [new { sourceColumn = source, label = "Synthetic RDE", valueType = kind == "rdeAmount" ? "money" : "date" }];
        else mapping[kind] = source;

        var error = await Assert.ThrowsAsync<JetActionException>(() => fixture.CommitAsync(mapping, "signed", rde));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.StartsWith("65 列無法轉換", error.Message, StringComparison.Ordinal);
        Assert.Contains("以下只列出部分來源與列號，未列出的錯誤仍計入總數。", error.Message, StringComparison.Ordinal);
        var detail = Assert.Single(error.Details!);
        Assert.Equal(source, detail.SourceColumn);
        if (source == "BadAmount") Assert.Contains("second.xlsx 第 2、3、4、5、6 列", detail.Message, StringComparison.Ordinal);
        var after = await fixture.LoadAsync();
        Assert.Equal(before.GetProperty("mapping").GetRawText(), after.GetProperty("mapping").GetRawText());
        Assert.Equal(before.GetProperty("latestRuns").GetRawText(), after.GetProperty("latestRuns").GetRawText());
        Assert.Equal(65, await fixture.ScalarAsync("SELECT COUNT(*) FROM target_gl_entry;"));
        Assert.Equal(650_000, await fixture.ScalarAsync("SELECT CAST(SUM(amount_scaled) AS BIGINT) FROM target_gl_entry;"));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TbMalformedGrouping_ReportsSourceRowAndPreservesExistingProjection(string provider)
    {
        using var fixture = await Batch9MappingTestFixture.CreateAsync(provider);
        var before = await fixture.LoadAsync();
        var error = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync("mapping.commit.tb",
            JsonSerializer.Serialize(new { changeMode = "direct", mapping = new { accNum = "Account", accName = "Name", amount = "Bad" } })));
        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        var detail = Assert.Single(error.Details!);
        Assert.Equal("Bad", detail.SourceColumn);
        Assert.Contains("第 2 列", detail.Message, StringComparison.Ordinal);
        Assert.Equal(20_000, await fixture.ScalarAsync("SELECT change_amount_scaled FROM target_tb_balance;"));
        var after = await fixture.LoadAsync();
        Assert.Equal(before.GetProperty("mapping").GetRawText(), after.GetProperty("mapping").GetRawText());
        Assert.Equal(before.GetProperty("latestRuns").GetRawText(), after.GetProperty("latestRuns").GetRawText());
    }
}
