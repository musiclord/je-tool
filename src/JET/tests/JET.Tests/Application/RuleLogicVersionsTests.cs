using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class RuleLogicVersionsTests
{
    [Fact]
    public void ValidationAndFormalReportIntegration_AdvancesAllAffectedVersions()
    {
        Assert.Equal("validation-2026-08-14-v4", RuleLogicVersions.Validation);
        Assert.Equal("prescreen-2026-08-14-v6", RuleLogicVersions.Prescreen);
        Assert.Equal("filter-2026-08-14-v10", RuleLogicVersions.Filter);
    }

    [Fact]
    public void IsCurrent_PreFormalMetadataPrescreenRunVersion_ReturnsFalse()
    {
        var summary = JsonSerializer.Serialize(new
        {
            resultRef = new
            {
                runId = "pre-metadata-run",
                generatedUtc = "2026-08-14T00:00:00.0000000Z",
                logicVersion = "prescreen-2026-08-14-v5"
            }
        });

        Assert.False(RuleLogicVersions.IsCurrent(new RuleRunRecord(
            "pre-metadata-run",
            RuleRunKinds.Prescreen,
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
    public void IsCurrent_PreEffectivePartAValidationVersion_ReturnsFalse()
    {
        var summary = JsonSerializer.Serialize(new
        {
            resultRef = new
            {
                runId = "pre-effective-part-a",
                generatedUtc = "2026-08-14T00:00:00.0000000Z",
                logicVersion = "validation-2026-08-14-v3"
            }
        });

        Assert.False(RuleLogicVersions.IsCurrent(new RuleRunRecord(
            "pre-effective-part-a",
            RuleRunKinds.Validate,
            DateTimeOffset.UtcNow,
            summary)));
    }

    [Theory]
    [InlineData(RuleRunKinds.Validate, RuleLogicVersions.Validation)]
    [InlineData(RuleRunKinds.Prescreen, RuleLogicVersions.Prescreen)]
    public void IsCurrent_MatchingPersistedLogicVersion_ReturnsTrue(string runKind, string logicVersion)
    {
        var summary = JsonSerializer.Serialize(new
        {
            resultRef = new { runId = "run", generatedUtc = "2026-07-10T00:00:00.0000000Z", logicVersion }
        });

        Assert.True(RuleLogicVersions.IsCurrent(
            new RuleRunRecord("run", runKind, DateTimeOffset.UtcNow, summary)));
    }

    [Theory]
    [InlineData(RuleRunKinds.Validate, "validation-2026-07-13-v2")]
    [InlineData(RuleRunKinds.Prescreen, "prescreen-2026-07-11-v2")]
    public void IsCurrent_PreEffectivePopulationRunVersion_ReturnsFalse(
        string runKind,
        string previousLogicVersion)
    {
        var summary = JsonSerializer.Serialize(new
        {
            resultRef = new
            {
                runId = "old-run",
                generatedUtc = "2026-08-13T00:00:00.0000000Z",
                logicVersion = previousLogicVersion
            }
        });

        Assert.False(RuleLogicVersions.IsCurrent(
            new RuleRunRecord("old-run", runKind, DateTimeOffset.UtcNow, summary)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"resultRef\":{\"logicVersion\":\"old-version\"}}")]
    [InlineData("not-json")]
    public void IsCurrent_MissingOldOrInvalidVersion_ReturnsFalse(string summaryJson)
    {
        Assert.False(RuleLogicVersions.IsCurrent(
            new RuleRunRecord("run", RuleRunKinds.Validate, DateTimeOffset.UtcNow, summaryJson)));
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
                "rules":[{
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
            group.GetProperty("rules")[0].GetProperty("unknownRule").GetRawText());
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
    public void IsCurrent_PreKctPrerequisiteFilterVersion_ReturnsFalse()
    {
        var definitionJson = JsonSerializer.Serialize(new
        {
            populationScope = GlPopulationScopeValues.AuditPeriod,
            logicVersion = "filter-2026-07-14-v4",
            groups = Array.Empty<object>()
        });
        var saved = new SavedFilterScenario(
            1, "舊版 KCT 情境", "需重新保存", definitionJson, DateTimeOffset.UtcNow);

        Assert.False(RuleLogicVersions.IsCurrent(saved));
    }

    [Fact]
    public void IsCurrent_PreEffectivePopulationFilterVersion_ReturnsFalse()
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
