using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class ProgramMilestoneProgressTests
{
    [Theory]
    [InlineData(2, "validate.run", 4, 1)]
    [InlineData(4, "filter.commit", 5, 1)]
    [InlineData(5, "validate.run", 5, 0)]
    [InlineData(4, "mapping.commit.gl", 4, 0)]
    public async Task MappingCommitShared_UsesMonotonicProgramNodeMilestoneOnly(
        int currentStep,
        string actionName,
        int expectedStep,
        int expectedSaveCalls)
    {
        var document = Document(currentStep);
        var store = new RecordingProjectStore(document);

        await MappingCommitShared.AdvanceStepAsync(
            store,
            document,
            ProgramGraph.Current.RequireNode(actionName),
            CancellationToken.None);

        Assert.Equal(expectedStep, store.Document.CurrentStep);
        Assert.Equal(expectedSaveCalls, store.SaveCalls);
    }

    private static ProjectDocument Document(int currentStep) => new(
        ProjectId: "milestone-project",
        ProjectCode: "MILESTONE-001",
        EntityName: "里程碑測試公司",
        OperatorId: "tester",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: null,
        MoneyScale: ProjectDocument.DefaultMoneyScale,
        RoundingMode: ProjectDocument.DefaultRoundingMode,
        CreatedUtc: DateTimeOffset.UnixEpoch,
        CurrentStep: currentStep,
        SchemaVersion: ProjectDocument.CurrentSchemaVersion);

    private sealed class RecordingProjectStore(ProjectDocument document) : IProjectStore
    {
        internal ProjectDocument Document { get; private set; } = document;

        internal int SaveCalls { get; private set; }

        public Task CreateAsync(
            ProjectDocument document,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectDocument>>([Document]);

        public Task<ProjectDocument?> FindAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ProjectDocument?>(Document);

        public Task SaveAsync(
            ProjectDocument document,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Document = document;
            SaveCalls++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
