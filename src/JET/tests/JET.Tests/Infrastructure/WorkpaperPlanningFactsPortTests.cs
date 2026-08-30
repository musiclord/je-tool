using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Working Paper planning 只允許讀取兩個首頁存在性事實與有界情境計數；
/// 完整明細仍須留在 writer 的 keyset streaming 路徑。
/// </summary>
public sealed class WorkpaperPlanningFactsPortTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_ReadsOnlyFirstRowPresenceAndPreservesBoundedScenarioCounts(
        bool hasCompletenessDifferences,
        bool hasUnbalancedDocuments)
    {
        var completeness = new RecordingCompletenessRepository(hasCompletenessDifferences);
        var unbalanced = new RecordingDocBalanceRepository(hasUnbalancedDocuments);
        IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)> counts =
            new Dictionary<int, (long VoucherHitCount, long RowHitCount)>
            {
                [1] = (7, 3),
                [2] = (0, 0)
        };
        var scenarioCounts = new RecordingScenarioCountsRepository(counts);
        var fieldDefinitions = new RecordingFieldDefinitionFactsPort();
        var port = new WorkpaperPlanningFactsPort(
            completeness,
            unbalanced,
            scenarioCounts,
            fieldDefinitions);
        var plan = JetAuditProgram.Plan(Request());
        using var source = new CancellationTokenSource();

        var facts = await port.ExecuteAsync(plan, source.Token);

        Assert.Equal(hasCompletenessDifferences, facts.HasCompletenessDifferences);
        Assert.Equal(hasUnbalancedDocuments, facts.HasUnbalancedDocuments);
        Assert.Same(counts, facts.ScenarioHitCounts);

        Assert.Equal(1, completeness.Calls);
        Assert.Equal("project-1", completeness.ProjectId);
        Assert.Equal(10_000, completeness.MoneyScale);
        Assert.Equal("2025-01-01", completeness.PeriodStart);
        Assert.Equal("2025-12-31", completeness.PeriodEnd);
        Assert.Null(completeness.Request?.Cursor);
        Assert.Equal(1, completeness.Request?.PageSize);
        Assert.Equal(source.Token, completeness.CancellationToken);

        Assert.Equal(1, unbalanced.Calls);
        Assert.Equal("project-1", unbalanced.ProjectId);
        Assert.Equal(10_000, unbalanced.MoneyScale);
        Assert.Equal("2025-01-01", unbalanced.PeriodStart);
        Assert.Equal("2025-12-31", unbalanced.PeriodEnd);
        Assert.Null(unbalanced.Request?.Cursor);
        Assert.Equal(1, unbalanced.Request?.PageSize);
        Assert.Equal(source.Token, unbalanced.CancellationToken);

        Assert.Equal(1, scenarioCounts.Calls);
        Assert.Equal("project-1", scenarioCounts.ProjectId);
        Assert.Equal(source.Token, scenarioCounts.CancellationToken);

        Assert.Equal(
            new[] { DatasetKind.Tb, DatasetKind.Gl },
            fieldDefinitions.Kinds);
        Assert.All(
            fieldDefinitions.Scopes,
            scope => Assert.Equal(LegacyFieldDefinitionScope.Target, scope));
        Assert.Equal(fieldDefinitions.TbRows, facts.TargetTbDefinitions);
        Assert.Equal(fieldDefinitions.GlRows, facts.TargetGlDefinitions);
    }

    [Theory]
    [InlineData(true, false, "GL")]
    [InlineData(false, true, "TB")]
    public async Task ExecuteAsync_MissingTargetDefinitions_RequiresReplace(
        bool missingGl,
        bool missingTb,
        string datasetName)
    {
        var port = new WorkpaperPlanningFactsPort(
            new RecordingCompletenessRepository(hasRows: false),
            new RecordingDocBalanceRepository(hasRows: false),
            new RecordingScenarioCountsRepository(
                new Dictionary<int, (long VoucherHitCount, long RowHitCount)>()),
            new RecordingFieldDefinitionFactsPort(missingGl, missingTb));

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            port.ExecuteAsync(
                JetAuditProgram.Plan(Request()),
                CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, exception.Code);
        Assert.Equal(
            $"{datasetName} 批次缺少 Legacy 欄位定義；本版不反推舊批次，"
            + "請以 mode 'replace' 重新匯入。",
            exception.Message);
    }

    private static WorkpaperRequest Request() => new(
        ProjectId: "project-1",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastPeriodStart: "2024-01-01",
        MoneyScale: 10_000,
        ValidationRunId: "validation-run",
        ScenarioRevision: "revision",
        Scenarios:
        [
            new WorkpaperScenarioSelection(1, "情境一", "理由一"),
            new WorkpaperScenarioSelection(2, "情境二", "理由二")
        ],
        PopulationScope: GlPopulationScope.AuditPeriod);

    private sealed class RecordingCompletenessRepository(bool hasRows)
        : ICompletenessDiffPageRepository
    {
        public int Calls { get; private set; }

        public string? ProjectId { get; private set; }

        public int MoneyScale { get; private set; }

        public string? PeriodStart { get; private set; }

        public string? PeriodEnd { get; private set; }

        public PageRequest? Request { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
            string projectId,
            int moneyScale,
            string periodStart,
            string periodEnd,
            PageRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            ProjectId = projectId;
            MoneyScale = moneyScale;
            PeriodStart = periodStart;
            PeriodEnd = periodEnd;
            Request = request;
            CancellationToken = cancellationToken;
            IReadOnlyList<CompletenessDiffAccount> rows = hasRows
                ? [new CompletenessDiffAccount("1101", "現金", 1, 0, 1, false)]
                : [];
            return Task.FromResult(new PageResult<CompletenessDiffAccount>(
                rows,
                hasRows ? "next-page-must-not-be-read" : null));
        }
    }

    private sealed class RecordingDocBalanceRepository(bool hasRows)
        : IDocBalancePageRepository
    {
        public int Calls { get; private set; }

        public string? ProjectId { get; private set; }

        public int MoneyScale { get; private set; }

        public string? PeriodStart { get; private set; }

        public string? PeriodEnd { get; private set; }

        public PageRequest? Request { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<PageResult<UnbalancedDocument>> GetPageAsync(
            string projectId,
            int moneyScale,
            string periodStart,
            string periodEnd,
            PageRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            ProjectId = projectId;
            MoneyScale = moneyScale;
            PeriodStart = periodStart;
            PeriodEnd = periodEnd;
            Request = request;
            CancellationToken = cancellationToken;
            IReadOnlyList<UnbalancedDocument> rows = hasRows
                ? [new UnbalancedDocument("JV-1", 2, 1, 1)]
                : [];
            return Task.FromResult(new PageResult<UnbalancedDocument>(
                rows,
                hasRows ? "next-page-must-not-be-read" : null));
        }
    }

    private sealed class RecordingScenarioCountsRepository(
        IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)> result)
        : ITagMatrixScenariosRepository
    {
        public int Calls { get; private set; }

        public string? ProjectId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<IReadOnlyDictionary<int, (long VoucherHitCount, long RowHitCount)>>
            GetCountsAsync(
                string projectId,
                CancellationToken cancellationToken)
        {
            Calls++;
            ProjectId = projectId;
            CancellationToken = cancellationToken;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingFieldDefinitionFactsPort(
        bool missingGl = false,
        bool missingTb = false)
        : ILegacyFieldDefinitionFactsPort
    {
        public IReadOnlyList<LegacyFieldDefinition> TbRows { get; } =
            missingTb
                ? []
                :
                [
                    new LegacyFieldDefinition(
                        1,
                        "會計科目編號_TB",
                        "來源科目",
                        LegacyFieldKind.Text,
                        18,
                        null)
                ];

        public IReadOnlyList<LegacyFieldDefinition> GlRows { get; } =
            missingGl
                ? []
                :
                [
                    new LegacyFieldDefinition(
                        1,
                        "傳票金額_JE",
                        "來源金額",
                        LegacyFieldKind.Number,
                        null,
                        4)
                ];

        public List<DatasetKind> Kinds { get; } = [];

        public List<LegacyFieldDefinitionScope> Scopes { get; } = [];

        public Task<IReadOnlyList<LegacyFieldDefinition>> ReadAsync(
            string projectId,
            DatasetKind kind,
            LegacyFieldDefinitionScope scope,
            CancellationToken cancellationToken)
        {
            Assert.Equal("project-1", projectId);
            Kinds.Add(kind);
            Scopes.Add(scope);
            return Task.FromResult(
                kind == DatasetKind.Tb ? TbRows : GlRows);
        }
    }
}
