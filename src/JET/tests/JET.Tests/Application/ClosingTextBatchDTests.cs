using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 收尾文字批次 D（使用者 2026-10-14 第三輪勾選頁確認）的新增行為。預期值都是寫死的確認句子或代號，不由被測程式算出。
/// R3-62：篩選條件編譯時的檢查沒過，畫面只看到非預期錯誤那一句，檢查代號與位置記進支援日誌的
/// <c>filter.compile_check_failed</c>，不帶欄位名稱與輸入值；本機兩種資料庫的方言都走同一個檢查。
/// R3-03：開案回應帶回已移除的 GL 欄位名稱，畫面與讀回用它寫「傳票日期（已移除）」，不印程式代號。
/// </summary>
public sealed class ClosingTextBatchDTests
{
    private const string UnexpectedErrorSentence = "發生非預期的錯誤，此操作未完成。請再試一次。";
    private const string TextFieldId = "rde.d0000000d0000000d0000000d0000001";
    private const string RemovedFieldId = "rde.d0000000d0000000d0000000d0000009";

    private static readonly FilterRuleContext Context =
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

        var exception = Assert.Throws<JetActionException>(() => builder.BuildPlan(scenario, Context, zeroModulus: 1_000_000));

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
        var exception = Assert.Throws<JetActionException>(() => builder.BuildPlan(scenario, Context, zeroModulus: 1_000_000));
        using var support = new SupportRingBufferLoggerProvider(64);
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(support));

        FilterScenarioDiagnostics.LogException(factory.CreateLogger("JET.Bridge.ActionDispatcher"), "filter.preview", exception);

        var entry = Assert.Single(support.Snapshot(), item => item.EventName == "filter.compile_check_failed");
        Assert.Equal("typed_field_not_mapped", entry.Fields["compile_check"]?.ToString());
        Assert.Equal("1", entry.Fields["group_no"]?.ToString());
        Assert.Equal("1", entry.Fields["rule_no"]?.ToString());
        Assert.Equal("action filter.preview filter group 1 rule 1 compile check failed: typed_field_not_mapped", entry.Message);
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

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ProjectLoad_ReturnsTheRemovedGlFieldNames(string provider)
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var projectId = await CreateProjectAsync(host, provider);

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));

        var labels = loaded.GetProperty("retiredGlFieldLabels");
        Assert.Equal(JsonValueKind.Object, labels.ValueKind);
        Assert.Equal(
            new[] { ("voucherDate", "傳票日期") },
            labels.EnumerateObject().Select(item => (item.Name, item.Value.GetString()!)).ToArray());
    }

    private static FilterRuleSpec Rule(string fieldId, string value) =>
        new(
            FilterJoin.And,
            FilterRuleType.TypedField,
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

    private static async Task<string> CreateProjectAsync(HandlerTestHost host, string provider)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = $"TXD-{suffix}",
            entityName = "Synthetic wording test",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = provider,
        }));
        return created.GetProperty("projectId").GetString()!;
    }
}
