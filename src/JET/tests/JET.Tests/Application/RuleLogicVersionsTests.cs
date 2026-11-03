using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class RuleLogicVersionsTests
{
    [Fact]
    public void BeforeUnicodeCaseFix_SavedFilterAndPrescreenResultsAreStale()
    {
        var saved = new SavedFilterScenario(1, "舊文字條件", "合成測試", """
            {"populationScope":"auditPeriod","logicVersion":"filter-2026-09-07-v12","groups":[]}
            """, DateTimeOffset.UnixEpoch);
        Assert.False(RuleLogicVersions.IsCurrent(saved));
        var summary = """{"resultRef":{"logicVersion":"prescreen-2026-08-14-v6"}}""";
        Assert.False(RuleLogicVersions.IsCurrent(new ResultRecord("old", ResultKind.Prescreen, DateTimeOffset.UnixEpoch, summary)));
    }

    [Fact]
    public void BlankVoucherAndDateReuseDecisions_AdvanceAllAffectedVersions()
    {
        // 整張傳票條件排除空白傳票號碼（裁定 2026-10-04-02），舊預篩選與篩選結果必須重算。
        // Public首敗100911120：空白號碼計數與同號多日期摘要使validation升v5；仍鎖定三個完整字面值。
        // 裁定 2026-10-23-01：傳票核准日不可用時「核准日不在期間」改成無法執行，不再寫 0 筆，validation 因此升 v6，預期值跟著改。
        // 第一次失敗收據 20261023-101120680-815f0e4797004774973c3c5070a61854。
        Assert.Equal("validation-2026-10-23-v6", RuleLogicVersions.Validation);
        // 裁定 2026-10-08-03 移除傳票日期與回溯過帳：預篩選升 v10、篩選升 v19。第一次失敗收據 20261008-042044720-73933024e0f84158a07081513d017018。
        Assert.Equal("prescreen-2026-10-08-v10", RuleLogicVersions.Prescreen);
        // 停用重複寫法，推進 v20；首次失敗：20261014-135805134-9060396119bc4e8f8dae2eecd29f1776。
        Assert.Equal("filter-2026-10-14-v20", RuleLogicVersions.Filter);
    }

    [Fact]
    public void BeforeApprovalDateNaDecision_ValidationResultIsStale()
    {
        // 裁定 2026-10-23-01 之前的驗證結果在傳票核准日沒有配對時寫 0 筆，畫面顯示「通過」，不能當成目前結果。
        // 第一次失敗收據 20261023-101120680-815f0e4797004774973c3c5070a61854。
        var summary = """{"resultRef":{"logicVersion":"validation-2026-10-04-v5"}}""";
        Assert.False(RuleLogicVersions.IsCurrent(new ResultRecord("old", ResultKind.Validation, DateTimeOffset.UnixEpoch, summary)));
    }

    [Fact]
    public void BeforeVoucherDateRemoval_SavedFilterAndPrescreenResultsAreStale()
    {
        // 裁定 2026-10-08-03 移除傳票日期與回溯過帳：含回溯過帳的舊預篩選摘要與舊情境都不能當成目前結果。
        var saved = new SavedFilterScenario(1, "舊回溯條件", "合成測試", """
            {"populationScope":"auditPeriod","logicVersion":"filter-2026-10-06-v18","groups":[]}
            """, DateTimeOffset.UnixEpoch);
        Assert.False(RuleLogicVersions.IsCurrent(saved));
        var summary = """{"resultRef":{"logicVersion":"prescreen-2026-10-04-v9"}}""";
        Assert.False(RuleLogicVersions.IsCurrent(new ResultRecord("old", ResultKind.Prescreen, DateTimeOffset.UnixEpoch, summary)));
    }

    [Fact]
    public void BeforeBlankVoucherDecisions_SavedFilterAndPrescreenResultsAreStale()
    {
        var saved = new SavedFilterScenario(1, "舊傳票條件", "合成測試", """
            {"populationScope":"auditPeriod","logicVersion":"filter-2026-09-18-v16","groups":[]}
            """, DateTimeOffset.UnixEpoch);
        Assert.False(RuleLogicVersions.IsCurrent(saved));
        var summary = """{"resultRef":{"logicVersion":"prescreen-2026-09-17-v8"}}""";
        Assert.False(RuleLogicVersions.IsCurrent(new ResultRecord("old", ResultKind.Prescreen, DateTimeOffset.UnixEpoch, summary)));
    }

    [Fact]
    public void IsCurrent_PreFormalMetadataPrescreenResultVersion_ReturnsFalse()
    {
        var summary = JsonSerializer.Serialize(new
        {
            resultRef = new
            {
                resultId = "pre-metadata-run",
                generatedUtc = "2026-08-14T00:00:00.0000000Z",
                logicVersion = "prescreen-2026-08-14-v5"
            }
        });

        Assert.False(RuleLogicVersions.IsCurrent(new ResultRecord(
            "pre-metadata-run",
            ResultKind.Prescreen,
            DateTimeOffset.UtcNow,
            summary)));
    }

    [Fact]
    public void IsCurrent_PreDynamicResultColumnFilterVersion_ReturnsFalse()
    {
        var definitionJson = JsonSerializer.Serialize(new
        {
            populationScope = GlPopulationScopeValues.AuditPeriod,
            logicVersion = "filter-2026-08-14-v9",
            groups = Array.Empty<object>()
        });
        var saved = new SavedFilterScenario(
            1, "pair 多選情境", "需重新保存", definitionJson, DateTimeOffset.UtcNow);

        Assert.False(RuleLogicVersions.IsCurrent(saved));
    }

    [Fact]
    public void IsCurrent_PreEffectiveStoredTotalsValidationVersion_ReturnsFalse()
    {
        var summary = JsonSerializer.Serialize(new
        {
            resultRef = new
            {
                resultId = "pre-effective-stored-totals",
                generatedUtc = "2026-08-14T00:00:00.0000000Z",
                logicVersion = "validation-2026-08-14-v3"
            }
        });

        Assert.False(RuleLogicVersions.IsCurrent(new ResultRecord(
            "pre-effective-stored-totals",
            ResultKind.Validation,
            DateTimeOffset.UtcNow,
            summary)));
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
    [InlineData(ResultKind.Validation, "validation-2026-07-13-v2")]
    [InlineData(ResultKind.Prescreen, "prescreen-2026-07-11-v2")]
    public void IsCurrent_PreGlRunVersion_ReturnsFalse(
        ResultKind kind,
        string previousLogicVersion)
    {
        var summary = JsonSerializer.Serialize(new
        {
            resultRef = new
            {
                resultId = "old-run",
                generatedUtc = "2026-08-13T00:00:00.0000000Z",
                logicVersion = previousLogicVersion
            }
        });

        Assert.False(RuleLogicVersions.IsCurrent(
            new ResultRecord("old-run", kind, DateTimeOffset.UtcNow, summary)));
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
    public void IsCurrent_PreCardPrerequisiteFilterVersion_ReturnsFalse()
    {
        var definitionJson = JsonSerializer.Serialize(new
        {
            populationScope = GlPopulationScopeValues.AuditPeriod,
            logicVersion = "filter-2026-07-14-v4",
            groups = Array.Empty<object>()
        });
        var saved = new SavedFilterScenario(
            1, "舊版卡片情境", "需重新保存", definitionJson, DateTimeOffset.UtcNow);

        Assert.False(RuleLogicVersions.IsCurrent(saved));
    }

    [Fact]
    public void IsCurrent_PreGlFilterVersion_ReturnsFalse()
    {
        var definitionJson = JsonSerializer.Serialize(new
        {
            populationScope = GlPopulationScopeValues.AuditPeriod,
            logicVersion = "filter-2026-08-04-v6",
            groups = Array.Empty<object>()
        });
        var saved = new SavedFilterScenario(
            1, "舊有效母體情境", "需重新保存", definitionJson, DateTimeOffset.UtcNow);

        Assert.False(RuleLogicVersions.IsCurrent(saved));
    }

    [Fact]
    public void IsCurrent_FilterDefinitionWithLegacyAllProjectedScope_ReturnsFalse()
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
