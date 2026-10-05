using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Typed dynamic rule（2026-08-14 契約凍結）的 wire contract 守衛：closed grammar 解析、
/// operand carrier、amountBasis、logicVersion 推進與 raw JSON read-back 保真。
/// 完整驗證規則的唯一權威是 manifest「Mapping」段落的凍結條目。
/// </summary>
public sealed class TypedFieldFilterContractTests
{
    private const string TextFieldId = "rde.0123456789abcdef0123456789abcdef";

    [Fact]
    public void TypedRule_TextEquals_ParsesInsteadOfFailingLoud()
    {
        var spec = Parse($$"""
            {"name":"typed","rationale":"contract","groups":[{"rules":[
              {"join":"AND","type":"typed","fieldId":"{{TextFieldId}}","operator":"equals","value":"ALPHA"}
            ]}]}
            """);

        Assert.Single(spec.Groups);
        Assert.Single(spec.Groups[0].Rules);
    }

    [Fact]
    public void FilterLogicVersion_CurrentContractIsV17()
    {
        // 2026-10-04 R1、R2 改變空白傳票號碼的命中結果，因此推進整份篩選定義的版本。
        Assert.Equal("filter-2026-10-04-v17", RuleLogicVersions.Filter);
    }

    [Fact]
    public void TypedRule_PreservesRawWireSpelling_IncludingUntrimmedOperands()
    {
        // operand carrier 的原始拼法逐字保留（trim／正規化只屬比較語意，不改寫 wire）。
        var spec = Parse($$"""
            {"name":"typed","rationale":"contract","groups":[{"rules":[
              {"join":"AND","type":"typed","fieldId":"{{TextFieldId}}","operator":"in",
               "values":["  Alpha ", "beta"]}
            ]}]}
            """);

        var rule = spec.Groups[0].Rules[0];
        Assert.Equal(FilterRuleType.TypedField, rule.Type);
        Assert.Equal(TextFieldId, rule.FieldId);
        Assert.Equal("in", rule.TypedOperator);
        Assert.Equal(["  Alpha ", "beta"], rule.TypedValues);
        Assert.Null(rule.TypedValue);
    }

    [Theory]
    [InlineData("""{"join":"AND","type":"typed","fieldId":"rde.x","operator":"equals","value":null}""")]
    [InlineData("""{"join":"AND","type":"typed","fieldId":"rde.x","operator":"equals","value":123}""")]
    [InlineData("""{"join":"AND","type":"typed","fieldId":"rde.x","operator":"between","from":true,"to":"1"}""")]
    [InlineData("""{"join":"AND","type":"typed","fieldId":"rde.x","operator":"in","values":"a,b"}""")]
    [InlineData("""{"join":"AND","type":"typed","fieldId":"rde.x","operator":"in","values":[1]}""")]
    [InlineData("""{"join":"AND","type":"typed","fieldId":null,"operator":"equals","value":"x"}""")]
    [InlineData("""{"join":"AND","type":"typed","fieldId":"rde.x","operator":"looksLike","value":"x"}""")]
    [InlineData("""{"join":"AND","type":"typed","fieldId":"rde.x","operator":" equals","value":"x"}""")]
    [InlineData("""{"join":"AND","type":"typed","fieldId":"rde.x","operator":"equals","value":"x","amountBasis":7}""")]
    public void TypedRule_MalformedCarrierShapes_FailLoudAtParse(string ruleJson)
    {
        var exception = Assert.Throws<JetActionException>(() => Parse(
            $$"""{"name":"typed","rationale":"contract","groups":[{"rules":[{{ruleJson}}]}]}"""));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
    }

    [Fact]
    public void TypedRule_MoreThanHundredValues_FailsLoudAtParse()
    {
        var values = string.Join(",", Enumerable.Range(0, 101).Select(i => $"\"v{i}\""));
        var exception = Assert.Throws<JetActionException>(() => Parse(
            $$"""
            {"name":"typed","rationale":"contract","groups":[{"rules":[
              {"join":"AND","type":"typed","fieldId":"{{TextFieldId}}","operator":"in","values":[{{values}}]}
            ]}]}
            """));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Contains("100", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StampFilterDefinition_KeepsTypedRuleRawJson_AndUnknownProperties()
    {
        // accepted raw scenario JSON 仍是 persistence authority：typed 條件的未知屬性與
        // 原始數字 lexeme 不得因 typed projection 而遺失或重寫。
        using var source = JsonDocument.Parse($$"""
            {
              "name":"typed",
              "rationale":"contract",
              "groups":[{"rules":[
                {"join":"AND","type":"typed","fieldId":"{{TextFieldId}}","operator":"equals",
                 "value":"  Alpha ","unknownRule":1E+02}
              ]}],
              "logicVersion":"caller-value"
            }
            """);

        var stamped = RuleLogicVersions.StampFilterDefinition(source.RootElement);

        using var persisted = JsonDocument.Parse(stamped);
        var rule = persisted.RootElement.GetProperty("groups")[0].GetProperty("rules")[0];
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
    public void ValidationContextFactory_LegacyMappingWithoutOptions_HasEmptyRegistry()
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
        // typed 條件的物化路徑（Criteria／Working Paper／filterHitsPage 補算）必須拿到
        // committed RDE registry，否則 typed 定義在編譯前就會 fail loud。
        var materializer = new RecordingMaterializer();
        var document = Project();
        var scenarios = new[] { SavedDrCrScenario() };
        var service = new FilterRunMaterializeService(
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
        EntityName: "typed 接線測試",
        OperatorId: "tester",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: "2024-12-31",
        MoneyScale: ProjectDocument.DefaultMoneyScale,
        RoundingMode: ProjectDocument.DefaultRoundingMode,
        CreatedUtc: new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
        CurrentStep: 4,
        SchemaVersion: ProjectDocument.CurrentSchemaVersion);

    private static CommittedMapping MappingWithRde() => new(
        DatasetKind.Gl,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.DocNum] = "傳票號碼",
            [GlMappingKeys.PostDate] = "傳票日期",
            [GlMappingKeys.AccNum] = "科目代號",
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
            {"name":"借方","rationale":"typed 接線測試","groups":[{"join":"AND","rules":[
              {"join":"AND","type":"drCrOnly","drCr":"debit"}
            ]}],"populationScope":"{{GlPopulationScopeValues.AuditPeriod}}","logicVersion":"{{RuleLogicVersions.Filter}}"}
            """,
        SavedUtc: new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));

    private sealed class RecordingMaterializer : IFilterRunMaterializer
    {
        public FilterRuleContext? LastContext { get; private set; }

        public Task MaterializeAsync(
            string projectId,
            IReadOnlyList<MaterializableScenario> scenarios,
            FilterRuleContext context,
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

        // 第 9 批中低 12：測試替身沿用原本的正常清單，不在產品介面提供相容實作。
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
