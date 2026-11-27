using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class RuleLogicVersionsTests
{
    [Fact]
    public void CurrentVersions_MatchPinnedLiterals()
    {
        // 鎖定驗證、預篩選與篩選三個規則邏輯版本的完整字面值；規則的判斷改變時版本跟著推進，版本較舊的結果與情境就會判成過期。
        Assert.Equal("validation-2026-10-23-v6", RuleLogicVersions.Validation);
        Assert.Equal("prescreen-2026-10-08-v10", RuleLogicVersions.Prescreen);
        Assert.Equal("filter-2026-10-14-v20", RuleLogicVersions.Filter);
    }

    [Theory]
    [InlineData(ResultKind.Validation, "validation-2026-10-04-v5")]
    [InlineData(ResultKind.Validation, "validation-2026-08-14-v3")]
    [InlineData(ResultKind.Validation, "validation-2026-07-13-v2")]
    [InlineData(ResultKind.Prescreen, "prescreen-2026-10-04-v9")]
    [InlineData(ResultKind.Prescreen, "prescreen-2026-09-17-v8")]
    [InlineData(ResultKind.Prescreen, "prescreen-2026-08-14-v6")]
    [InlineData(ResultKind.Prescreen, "prescreen-2026-08-14-v5")]
    [InlineData(ResultKind.Prescreen, "prescreen-2026-07-11-v2")]
    public void IsCurrent_OlderResultLogicVersion_ReturnsFalse(ResultKind kind, string logicVersion)
    {
        // 規則邏輯版本較舊的驗證與預篩選結果不能當成目前結果，要重新執行。
        var summary = JsonSerializer.Serialize(new
        {
            resultRef = new { resultId = "old-run", generatedUtc = "2026-08-13T00:00:00.0000000Z", logicVersion }
        });

        Assert.False(RuleLogicVersions.IsCurrent(
            new ResultRecord("old-run", kind, DateTimeOffset.UtcNow, summary)));
    }

    [Theory]
    [InlineData("filter-2026-07-14-v4")]
    [InlineData("filter-2026-08-04-v6")]
    [InlineData("filter-2026-08-14-v7")]
    [InlineData("filter-2026-08-14-v8")]
    [InlineData("filter-2026-08-14-v9")]
    [InlineData("filter-2026-09-07-v12")]
    [InlineData("filter-2026-09-08-v13")]
    [InlineData("filter-2026-09-17-v14")]
    [InlineData("filter-2026-09-18-v15")]
    [InlineData("filter-2026-09-18-v16")]
    [InlineData("filter-2026-10-04-v17")]
    [InlineData("filter-2026-10-06-v18")]
    [InlineData("filter-2026-10-08-v19")]
    public void IsCurrent_OlderFilterLogicVersion_ReturnsFalse(string logicVersion)
    {
        // 較舊版本保存的情境不是目前版本，不得沿用舊的篩選結果。開案時整批仍符合目前規則的情境會改成目前版本再重算；
        // 不符合時整批維持舊版本，查詢與匯出仍會擋下。
        var definitionJson = JsonSerializer.Serialize(new
        {
            populationScope = GlPopulationScopeValues.AuditPeriod,
            logicVersion,
            groups = Array.Empty<object>()
        });
        var saved = new SavedFilterScenario(
            1, "舊版情境", "需重新保存", definitionJson, DateTimeOffset.UtcNow);

        Assert.False(RuleLogicVersions.IsCurrent(saved));
    }

    [Theory]
    [InlineData(ResultKind.Validation, RuleLogicVersions.Validation)]
    [InlineData(ResultKind.Prescreen, RuleLogicVersions.Prescreen)]
    public void IsCurrent_MatchingPersistedLogicVersion_ReturnsTrue(ResultKind kind, string logicVersion)
    {
        var summary = JsonSerializer.Serialize(new
        {
            resultRef = new { resultId = "run", generatedUtc = "2026-07-10T00:00:00.0000000Z", logicVersion }
        });

        Assert.True(RuleLogicVersions.IsCurrent(
            new ResultRecord("run", kind, DateTimeOffset.UtcNow, summary)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"resultRef\":{\"logicVersion\":\"old-version\"}}")]
    [InlineData("not-json")]
    public void IsCurrent_MissingOldOrInvalidVersion_ReturnsFalse(string summaryJson)
    {
        Assert.False(RuleLogicVersions.IsCurrent(
            new ResultRecord("run", ResultKind.Validation, DateTimeOffset.UtcNow, summaryJson)));
    }

    [Fact]
    public void StampFilterDefinition_OverridesCallerVersion_AndPreservesDefinition()
    {
        using var source = JsonDocument.Parse(
            """
            {"name":"scenario","groups":[],"logicVersion":"caller-value"}
            """);

        var stamped = RuleLogicVersions.StampFilterDefinition(source.RootElement);
        var saved = new SavedFilterScenario(
            1, "scenario", "reason", stamped, DateTimeOffset.UtcNow);

        Assert.True(RuleLogicVersions.IsCurrent(saved));
        using var persisted = JsonDocument.Parse(stamped);
        Assert.Equal("scenario", persisted.RootElement.GetProperty("name").GetString());
        Assert.Equal(RuleLogicVersions.Filter, persisted.RootElement.GetProperty("logicVersion").GetString());
    }

    [Fact]
    public void StampFilterDefinition_PreservesUnknownPropertiesAndOriginalNumberLexemes()
    {
        using var source = JsonDocument.Parse(
            """
            {
              "name":"scenario",
              "unknownTop":1e2,
              "populationScope":"caller-scope",
              "groups":[{
                "join":"AND",
                "unknownGroup":1.2300,
                "conditions":[{
                  "join":"AND",
                  "type":"drCrOnly",
                  "drCr":"debit",
                  "unknownRule":1E+02
                }]
              }],
              "logicVersion":"caller-value"
            }
            """);

        var stamped = RuleLogicVersions.StampFilterDefinition(source.RootElement);

        using var persisted = JsonDocument.Parse(stamped);
        var root = persisted.RootElement;
        Assert.Equal("1e2", root.GetProperty("unknownTop").GetRawText());
        var group = root.GetProperty("groups")[0];
        Assert.Equal("1.2300", group.GetProperty("unknownGroup").GetRawText());
        Assert.Equal(
            "1E+02",
            group.GetProperty("conditions")[0].GetProperty("unknownRule").GetRawText());
        Assert.Equal(
            GlPopulationScopeValues.AuditPeriod,
            root.GetProperty("populationScope").GetString());
        Assert.Equal(
            RuleLogicVersions.Filter,
            root.GetProperty("logicVersion").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"logicVersion\":\"old-version\"}")]
    [InlineData("not-json")]
    public void IsCurrent_FilterDefinitionMissingOldOrInvalidVersion_ReturnsFalse(string definitionJson)
    {
        var saved = new SavedFilterScenario(
            1, "scenario", "reason", definitionJson, DateTimeOffset.UtcNow);

        Assert.False(RuleLogicVersions.IsCurrent(saved));
    }

    [Fact]
    public void IsCurrent_FilterDefinitionWithUnsupportedAllProjectedScope_ReturnsFalse()
    {
        var definitionJson = JsonSerializer.Serialize(new
        {
            populationScope = "allProjected",
            logicVersion = RuleLogicVersions.Filter,
            groups = Array.Empty<object>()
        });
        var saved = new SavedFilterScenario(
            1, "scenario", "reason", definitionJson, DateTimeOffset.UtcNow);

        Assert.False(RuleLogicVersions.IsCurrent(saved));
    }
}
