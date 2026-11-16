using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class ProjectCreationValidationTests
{
    [Theory]
    [InlineData(" ")]
    [InlineData("　")]
    [InlineData("\t\r\n")]
    public async Task ExplicitBlankProjectName_IsRejectedInsteadOfBecomingAGuid(string name)
    {
        using var host = new HandlerTestHost();
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("project.create",
            JsonSerializer.Serialize(new { projectName = name, periodStart = "2025-01-01", periodEnd = "2025-12-31" })));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        Assert.Equal("projectName", error.Field);
        Assert.Contains("案件名稱", error.Message);
    }

    [Fact]
    public async Task ReversedPeriod_IsRejectedBeforeCreatingAProject()
    {
        using var host = new HandlerTestHost();
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("project.create",
            "{\"projectName\":\"Synthetic reversed period\",\"periodStart\":\"2025-12-31\",\"periodEnd\":\"2025-01-01\"}"));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        Assert.Equal("periodStart", error.Field);
        Assert.Contains("起始日", error.Message);
        Assert.Contains("截止日", error.Message);
    }

    [Theory]
    [InlineData("1899-12-31", "2025-12-31", "periodStart", "查核起始日的年份須在 1900 到 2100 之間。請修正年份後再建立案件。")]
    [InlineData("0202-01-01", "2025-12-31", "periodStart", "查核起始日的年份須在 1900 到 2100 之間。請修正年份後再建立案件。")]
    [InlineData("2025-01-01", "2101-01-01", "periodEnd", "查核截止日的年份須在 1900 到 2100 之間。請修正年份後再建立案件。")]
    [InlineData("2025-01-01", "9999-12-31", "periodEnd", "查核截止日的年份須在 1900 到 2100 之間。請修正年份後再建立案件。")]
    [InlineData("1899-01-01", "2101-12-31", "periodStart", "查核起始日的年份須在 1900 到 2100 之間。請修正年份後再建立案件。")]
    [InlineData("2101-01-01", "2101-12-31", "periodStart", "查核起始日的年份須在 1900 到 2100 之間。請修正年份後再建立案件。")]
    public async Task PeriodYearOutside1900To2100_IsRejectedWithTheFieldAndWhatToDoNext(
        string periodStart, string periodEnd, string field, string message)
    {
        using var host = new HandlerTestHost();
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("project.create",
            JsonSerializer.Serialize(new { projectName = "Synthetic period year", periodStart, periodEnd })));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        Assert.Equal(field, error.Field);
        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData("1900-01-01", "1900-12-31")]
    [InlineData("2100-01-01", "2100-12-31")]
    [InlineData("1900-01-01", "2100-12-31")]
    public async Task PeriodYearAtTheEdgesOf1900To2100_StillCreates(string periodStart, string periodEnd)
    {
        using var host = new HandlerTestHost();
        var result = await host.DispatchAsync("project.create",
            JsonSerializer.Serialize(new { projectName = "Synthetic period edge", periodStart, periodEnd }));
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("2024-12-31", true)]
    [InlineData("2025-01-01", false)]
    [InlineData("2026-12-31", false)]
    [InlineData("2027-01-01", true)]
    [InlineData(null, false)]
    public async Task PreparationDate_WarnsOutsidePeriodStartToOneYearAfterPeriodEnd_ButAlwaysCreates(string? preparationDate, bool warn)
    {
        using var host = new HandlerTestHost();
        var result = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectName = "Synthetic date warning", periodStart = "2025-01-01", periodEnd = "2025-12-31",
            prepStartDate = preparationDate
        }));
        Assert.True(result.GetProperty("ok").GetBoolean());
        var warnings = result.GetProperty("warnings").EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Equal(warn ? 1 : 0, warnings.Length);
        if (warn) Assert.Contains("年份", warnings[0]);
    }

    [Theory]
    [InlineData("2025-02-28", false)]
    [InlineData("2025-03-01", true)]
    public async Task PreparationDate_OneYearAfterLeapDay_UsesTheLastDayOfFebruary(string preparationDate, bool warn)
    {
        using var host = new HandlerTestHost();
        var result = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectName = "Synthetic leap year", periodStart = "2024-01-01", periodEnd = "2024-02-29",
            prepStartDate = preparationDate
        }));
        Assert.Equal(warn ? 1 : 0, result.GetProperty("warnings").GetArrayLength());
    }

    [Theory]
    [InlineData("Synthetic&Name", "&")]
    [InlineData("Synthetic、Name", "、")]
    [InlineData("Synthetic.Name", ".")]
    public void InvalidNameMessage_IdentifiesTheActualCharactersAndWhereToPutTheCompanyName(string name, string invalid)
    {
        var message = ProjectNameRules.Validate(name);
        Assert.NotNull(message);
        Assert.Contains("「" + invalid + "」", message);
        Assert.Contains("客戶名稱", message);
    }
}
