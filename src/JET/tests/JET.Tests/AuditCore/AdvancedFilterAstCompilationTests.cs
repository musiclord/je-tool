using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>Stage 5 provider-neutral SQL plan tests over synthetic conditions.</summary>
public sealed class AdvancedFilterAstCompilationTests
{
    private static readonly FilterRuleContext Context =
        new(100, null, "2025-01-01", "2025-12-31", PopulationScope: GlPopulationScope.AuditPeriod);

    [Fact]
    public void SameVoucher_BuildsAnchorAndOrderedEffectiveVoucherSets()
    {
        var scenario = Parse(
            """
            {"name":"synthetic","rationale":"compile","groups":[{"matchScope":"sameVoucher","rules":[
              {"join":"AND","type":"textSet","field":"accNum","values":["AN CHOR"],"mode":"exact","normalization":"removeAsciiSpaces"},
              {"join":"AND","type":"textSet","field":"description","values":["EVI DENCE"],"mode":"contains","normalization":"removeAsciiSpaces"},
              {"join":"AND","type":"numRange","field":"amount","from":"5"}
            ]}]}
            """);

        var plan = Builder().BuildPlan(
            scenario,
            Context,
            zeroModulus: 1_000_000,
            schemaPrefix: "prj_synthetic.");

        Assert.Equal(2, Count(plan.Sql, "g.document_number IN ("));
        Assert.Equal(2, Count(plan.Sql, "FROM prj_synthetic.target_gl_entry g"));
        Assert.Equal(2, Count(plan.Sql, "g.is_effective = 1"));
        Assert.DoesNotContain("JOIN prj_synthetic.target_gl_entry", plan.Sql, StringComparison.Ordinal);
        Assert.Collection(
            plan.Parameters,
            parameter => AssertParameter(parameter, "@p0", "ANCHOR"),
            parameter => AssertParameter(parameter, "@p1", "EVIDENCE"),
            parameter => AssertParameter(parameter, "@p2", 500L));
    }

    [Fact]
    public void ExplicitRowScope_ProducesExactlyTheExistingOmittedScopePlan()
    {
        const string group =
            """
            "rules":[
              {"join":"AND","type":"text","field":"description","keywords":"alpha","mode":"contains"},
              {"join":"OR","type":"manualAuto","isManual":true}
            ]
            """;
        var omitted = Parse("{\"name\":\"synthetic\",\"rationale\":\"row\",\"groups\":[{" + group + "}]}");
        var explicitRow = Parse("{\"name\":\"synthetic\",\"rationale\":\"row\",\"groups\":[{\"matchScope\":\"row\"," + group + "}]}");

        var omittedPlan = Builder().BuildPlan(omitted, Context, zeroModulus: 1_000_000);
        var explicitPlan = Builder().BuildPlan(explicitRow, Context, zeroModulus: 1_000_000);

        Assert.Equal(omittedPlan.Sql, explicitPlan.Sql);
        Assert.Equal(omittedPlan.Parameters, explicitPlan.Parameters);
    }

    [Fact]
    public void CompiledParameterBudget_AllowsLimitAndRejectsNextValueBeforeScenarioCommandBinding()
    {
        var allowedCounts = Enumerable.Repeat(
                FilterScenarioLimits.MaxTextSetValuesPerRule,
                20)
            .ToArray();
        var rejectedCounts = allowedCounts.Append(1).ToArray();
        var allowed = TextSetScenario(allowedCounts);
        var rejected = TextSetScenario(rejectedCounts);

        Assert.Empty(FilterScenarioValidator.Validate(
            allowed,
            new FilterValidationContext(true, false, false)));
        Assert.Empty(FilterScenarioValidator.Validate(
            rejected,
            new FilterValidationContext(true, false, false)));

        var plan = Builder().BuildPlan(allowed, Context, zeroModulus: 1_000_000);
        Assert.Equal(FilterScenarioLimits.MaxCompiledParameters, plan.Parameters.Count);

        var exception = Assert.Throws<JetActionException>(
            () => Builder().BuildPlan(rejected, Context, zeroModulus: 1_000_000));
        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Contains(
            FilterScenarioLimits.MaxCompiledParameters.ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    private static GlFilterWhereBuilder Builder() =>
        new(
            SqliteDialect.Instance,
            new GlRulePredicates(SqliteDialect.Instance, GlPopulationScopeSql.Predicate));

    private static FilterScenarioSpec Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return FilterScenarioPayloadParser.Parse(document.RootElement, moneyScale: Context.MoneyScale);
    }

    private static FilterScenarioSpec TextSetScenario(IReadOnlyList<int> valueCounts)
    {
        var json = JsonSerializer.Serialize(new
        {
            name = "synthetic",
            rationale = "compiled parameter budget",
            groups = new[]
            {
                new
                {
                    matchScope = "row",
                    rules = valueCounts.Select((count, ruleIndex) => new
                    {
                        join = "AND",
                        type = "textSet",
                        field = "description",
                        mode = "contains",
                        normalization = "preserve",
                        values = Enumerable.Range(0, count)
                            .Select(valueIndex => $"synthetic_{ruleIndex}_{valueIndex}")
                            .ToArray()
                    }).ToArray()
                }
            }
        });
        return Parse(json);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static void AssertParameter(FilterSqlParameter parameter, string name, object value)
    {
        Assert.Equal(name, parameter.Name);
        Assert.Equal(value, parameter.Value);
    }
}
