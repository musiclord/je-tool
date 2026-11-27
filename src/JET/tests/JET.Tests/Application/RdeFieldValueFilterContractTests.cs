using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 欄位值條件選攸關資料元素欄位時的解析守衛：比較值的屬性都要是字串，原始拼法照樣保留；
/// 另核對驗證與物化時帶入的攸關資料元素欄位清單與金額倍數。
/// </summary>
public sealed class RdeFieldValueFilterContractTests
{
    private const string TextFieldId = "rde.0123456789abcdef0123456789abcdef";

    [Fact]
    public void RdeFieldValueRule_PreservesRawWireSpelling_IncludingUntrimmedOperands()
    {
        // operand carrier 的原始拼法逐字保留（trim／正規化只屬比較語意，不改寫 wire）。
        var spec = Parse($$"""
            {"name":"typed","rationale":"contract","groups":[{"conditions":[
              {"join":"AND","type":"fieldValue","fieldId":"{{TextFieldId}}","operator":"in",
               "values":["  Alpha ", "beta"]}
            ]}]}
            """);

        var rule = spec.Groups[0].Conditions[0];
        Assert.Equal(FilterConditionType.FieldValue, rule.Type);
        Assert.Equal(TextFieldId, rule.FieldId);
        Assert.Equal("in", rule.TypedOperator);
        Assert.Equal(["  Alpha ", "beta"], rule.TypedValues);
        Assert.Null(rule.TypedValue);
    }

    [Theory]
    [InlineData("""{"join":"AND","type":"fieldValue","fieldId":"rde.x","operator":"equals","value":null}""")]
    [InlineData("""{"join":"AND","type":"fieldValue","fieldId":"rde.x","operator":"equals","value":123}""")]
    [InlineData("""{"join":"AND","type":"fieldValue","fieldId":"rde.x","operator":"between","from":true,"to":"1"}""")]
    [InlineData("""{"join":"AND","type":"fieldValue","fieldId":"rde.x","operator":"in","values":"a,b"}""")]
    [InlineData("""{"join":"AND","type":"fieldValue","fieldId":"rde.x","operator":"in","values":[1]}""")]
    [InlineData("""{"join":"AND","type":"fieldValue","fieldId":null,"operator":"equals","value":"x"}""")]
    [InlineData("""{"join":"AND","type":"fieldValue","fieldId":"rde.x","operator":"equals","value":"x","amountBasis":7}""")]
    public void RdeFieldValueRule_MalformedCarrierShapes_FailLoudAtParse(string ruleJson)
    {
        var exception = Assert.Throws<JetActionException>(() => Parse(
            $$"""{"name":"typed","rationale":"contract","groups":[{"conditions":[{{ruleJson}}]}]}"""));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
    }

    [Fact]
    public void StampFilterDefinition_KeepsRdeFieldValueRuleRawJson_AndUnknownProperties()
    {
        // 存檔照收到的情境 JSON 原文：條件的未知屬性與原始數字寫法不會因解析而遺失或被改寫。
        using var source = JsonDocument.Parse($$"""
            {
              "name":"typed",
              "rationale":"contract",
              "groups":[{"conditions":[
                {"join":"AND","type":"fieldValue","fieldId":"{{TextFieldId}}","operator":"equals",
                 "value":"  Alpha ","unknownRule":1E+02}
              ]}],
              "logicVersion":"caller-value"
            }
            """);

        var stamped = RuleLogicVersions.StampFilterDefinition(source.RootElement);

        using var persisted = JsonDocument.Parse(stamped);
        var rule = persisted.RootElement.GetProperty("groups")[0].GetProperty("conditions")[0];
        Assert.Equal("  Alpha ", rule.GetProperty("value").GetString());
        Assert.Equal("1E+02", rule.GetProperty("unknownRule").GetRawText());
        Assert.Equal(
            RuleLogicVersions.Filter,
            persisted.RootElement.GetProperty("logicVersion").GetString());
    }

    [Fact]
    public void ValidationContextFactory_ExposesCommittedRdeRegistryAndMoneyScale()
    {
        var document = Project();
        var context = FilterValidationContextFactory.Create(
            document,
            MappingWithRde(),
            accountMappingState: null,
            hasAuthorizedPreparers: false,
            GlPopulationScope.AuditPeriod);

        var field = Assert.Single(context.RdeFields);
        Assert.Equal(TextFieldId, field.FieldId);
        Assert.Equal(document.MoneyScale, context.MoneyScale);
    }

    [Fact]
    public void ValidationContextFactory_MappingWithoutOptions_HasEmptyRegistry()
    {
        var context = FilterValidationContextFactory.Create(
            Project(),
            MappingWithRde() with { GlOptions = null },
            accountMappingState: null,
            hasAuthorizedPreparers: false,
            GlPopulationScope.AuditPeriod);

        Assert.Empty(context.RdeFields);
    }

    [Fact]
    public async Task MaterializeAllAsync_PassesCommittedRdeRegistryToCompilationContext()
    {
        // 物化路徑（條件篩選報告、工作底稿與 filterHitsPage 補算）必須拿到已確認的攸關資料元素欄位清單，
        // 否則用到攸關資料元素欄位的欄位值條件在編譯前就會失敗。
        var materializer = new RecordingMaterializer();
        var document = Project();
        var scenarios = new[] { SavedDrCrScenario() };
        var service = new FilterHitMaterializeService(
            materializer,
            new FixedProjectStore(document),
            new FixedScenarioStore(scenarios),
            new FixedMappingStore(MappingWithRde()),
            new ActionExecutionGate());

        await service.MaterializeAllAsync(
            document.ProjectId, document, scenarios, CancellationToken.None);

        Assert.NotNull(materializer.LastContext);
        var field = Assert.Single(materializer.LastContext!.RdeFields);
        Assert.Equal(TextFieldId, field.FieldId);
    }

    private static FilterScenarioSpec Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return FilterScenarioPayloadParser.Parse(document.RootElement, moneyScale: 100);
    }

    private static ProjectDocument Project() => new(
        ProjectId: "typed-project",
        ProjectCode: "P-T",
        ClientName: "typed 接線測試",
        OperatorId: "tester",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: "2024-12-31",
        MoneyScale: ProjectDocument.DefaultMoneyScale,
        CreatedUtc: new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
        CurrentStep: 4,
        SchemaVersion: ProjectDocument.CurrentSchemaVersion);

    private static CommittedMapping MappingWithRde() => new(
        DatasetKind.Gl,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.VoucherNumber] = "傳票號碼",
            [GlMappingKeys.GlDate] = "傳票日期",
            [GlMappingKeys.AccountNumber] = "科目代號",
            [GlMappingKeys.Amount] = "金額"
        },
        GlAmountModeNames.Signed,
        "typed-batch",
        new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
        GlOptions: new GlMappingOptions(
            ApprovalDateModeNames.Unmapped,
            PostingStatusPolicy: null,
            new GlManualAutoPolicy(["1"], ["0"]),
            [new GlRdeFieldMetadata(TextFieldId, "備註欄", "備註", RdeFieldValueTypeNames.Text)]));

    private static SavedFilterScenario SavedDrCrScenario() => new(
        Position: 1,
        Name: "借方",
        Rationale: "typed 接線測試",
        DefinitionJson: $$"""
            {"name":"借方","rationale":"typed 接線測試","groups":[{"join":"AND","conditions":[
              {"join":"AND","type":"drCrOnly","drCr":"debit"}
            ]}],"populationScope":"{{GlPopulationScopeValues.AuditPeriod}}","logicVersion":"{{RuleLogicVersions.Filter}}"}
            """,
        SavedUtc: new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));

    private sealed class RecordingMaterializer : IFilterHitMaterializer
    {
        public FilterConditionContext? LastContext { get; private set; }

        public Task MaterializeAsync(
            string projectId,
            IReadOnlyList<MaterializableScenario> scenarios,
            FilterConditionContext context,
            CancellationToken cancellationToken,
            bool replaceAll = true)
        {
            LastContext = context;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedProjectStore(ProjectDocument document) : IProjectStore
    {
        public Task CreateAsync(ProjectDocument created, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        // 測試替身沿用原本的正常清單，不在產品介面提供相容實作。
        public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) =>
            ProjectStoreTestEntries.FromAsync(ListAsync(cancellationToken));

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectDocument>>([document]);

        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectDocument?>(document);

        public Task SaveAsync(ProjectDocument saved, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedScenarioStore(IReadOnlyList<SavedFilterScenario> scenarios)
        : IFilterScenarioStore
    {
        public Task<IReadOnlyList<SavedFilterScenario>> ListAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult(scenarios);

        public Task ReplaceAllAsync(
            string projectId,
            IReadOnlyList<SavedFilterScenario> replaced,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RemoveAsync(
            string projectId,
            SavedFilterScenario scenario,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedMappingStore(CommittedMapping mapping) : IMappingStateStore
    {
        public Task SaveAsync(
            string projectId,
            CommittedMapping committedMapping,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommittedMapping?> FindAsync(
            string projectId,
            DatasetKind kind,
            CancellationToken cancellationToken) =>
            Task.FromResult<CommittedMapping?>(kind == DatasetKind.Gl ? mapping : null);
    }
}
