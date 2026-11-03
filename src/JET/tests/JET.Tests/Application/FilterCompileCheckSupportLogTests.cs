using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 篩選條件編譯時的檢查沒過，只會是程式送錯資料或存檔損毀，審計員在畫面上無從修正。畫面只寫非預期錯誤那一句，
/// 檢查代號與位置記進支援日誌的 <c>filter.compile_check_failed</c>，不帶欄位名稱與輸入值；本機兩種資料庫的方言都走同一個檢查。
/// 裁定 2026-10-14-01 確認過這段的用字。預期值都是寫死的句子或代號，不由被測程式算出。
/// </summary>
public sealed class FilterCompileCheckSupportLogTests
{
    private const string UnexpectedErrorSentence = "發生非預期的錯誤，此操作未完成。請再試一次。";
    private const string TextFieldId = "rde.d0000000d0000000d0000000d0000001";
    private const string RemovedFieldId = "rde.d0000000d0000000d0000000d0000009";

    private static readonly FilterConditionContext Context =
        new(100, null, "2025-01-01", "2025-12-31", PopulationScope: GlPopulationScope.AuditPeriod)
        {
            RdeFields = [new GlRdeFieldMetadata(TextFieldId, "合成欄", "合成附註", RdeFieldValueTypeNames.Text)]
        };

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public void CompileCheck_ShowsTheUnexpectedSentenceAndRemembersCheckAndPosition(string provider)
    {
        IProviderSqlDialect dialect = provider == "duckdb" ? DuckDbDialect.Instance : SqliteDialect.Instance;
        var builder = new GlFilterWhereBuilder(dialect, new GlRulePredicates(dialect, GlPopulationScopeSql.Predicate));
        var scenario = new FilterScenarioSpec("合成", "compile check", [
            new FilterGroupSpec(FilterJoin.And, [Rule(TextFieldId, "Alpha")]),
            new FilterGroupSpec(FilterJoin.And, [Rule(TextFieldId, "Beta"), Rule(RemovedFieldId, "Synthetic secret value")])
        ]);

        var exception = Assert.Throws<JetActionException>(() => builder.BuildPlan(scenario, Context));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Equal(UnexpectedErrorSentence, exception.Message);
        Assert.Equal("typed_field_not_mapped", FilterCompileCheck.CheckOf(exception));
        Assert.Equal(((int?)2, (int?)2), FilterCompileCheck.PositionOf(exception));
    }

    [Fact]
    public void CompileCheck_IsWrittenToTheSupportLogWithoutFieldNamesOrValues()
    {
        var builder = new GlFilterWhereBuilder(
            SqliteDialect.Instance, new GlRulePredicates(SqliteDialect.Instance, GlPopulationScopeSql.Predicate));
        var scenario = new FilterScenarioSpec("合成", "compile check", [
            new FilterGroupSpec(FilterJoin.And, [Rule(RemovedFieldId, "Synthetic secret value")])
        ]);
        var exception = Assert.Throws<JetActionException>(() => builder.BuildPlan(scenario, Context));
        using var support = new SupportRingBufferLoggerProvider(64);
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(support));

        FilterScenarioDiagnostics.LogException(factory.CreateLogger("JET.Bridge.ActionDispatcher"), "filter.preview", exception);

        var entry = Assert.Single(support.Snapshot(), item => item.EventName == "filter.compile_check_failed");
        Assert.Equal("typed_field_not_mapped", entry.Fields["compile_check"]?.ToString());
        Assert.Equal("1", entry.Fields["group_no"]?.ToString());
        Assert.Equal("1", entry.Fields["condition_no"]?.ToString());
        Assert.Equal("action filter.preview filter group 1 condition 1 compile check failed: typed_field_not_mapped", entry.Message);
        Assert.DoesNotContain(support.Snapshot(), item => item.EventName == "action.payload_rejected");
        foreach (var value in entry.Fields.Values.Select(item => item?.ToString() ?? string.Empty).Append(entry.Message))
        {
            Assert.DoesNotContain(RemovedFieldId, value, StringComparison.Ordinal);
            Assert.DoesNotContain("Synthetic secret value", value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CompileCheck_IsNotLoggedForOrdinaryScenarioErrors()
    {
        using var support = new SupportRingBufferLoggerProvider(64);
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(support));

        FilterScenarioDiagnostics.LogException(
            factory.CreateLogger("JET.Bridge.ActionDispatcher"),
            "filter.preview",
            new JetActionException(JetErrorCodes.InvalidScenario, "合成的一般錯誤"));

        Assert.DoesNotContain(support.Snapshot(), item => item.EventName == "filter.compile_check_failed");
    }

    private static FilterConditionSpec Rule(string fieldId, string value) =>
        new(
            FilterJoin.And,
            FilterConditionType.TypedField,
            PrescreenKey: null,
            Field: null,
            Keywords: [],
            Mode: TextMatchMode.Contains,
            FromDate: null,
            ToDate: null,
            FromAmountScaled: null,
            ToAmountScaled: null,
            DrCr: null,
            IsManual: null)
        {
            FieldId = fieldId,
            TypedOperator = "equals",
            TypedValue = value
        };
}
