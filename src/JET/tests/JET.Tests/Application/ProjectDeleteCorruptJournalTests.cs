using System.Text;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>使用者從 picker 遇到 report journal 衝突時，載入維持 fail-closed，但確認刪案可解除卡死。</summary>
public sealed class ProjectDeleteCorruptJournalTests
{
    [Fact]
    public async Task ConflictedReportJournal_LoadFailsWithSpecificCode_ButDeleteRemovesProject()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = $"JOURNAL-{Guid.NewGuid():N}"[..20],
            entityName = "Journal Conflict Test",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = "sqlite",
        }));
        var projectId = created.GetProperty("projectId").GetString()!;
        var folder = new JetProjectFolder(host.ProjectsRoot);
        var projectDirectory = folder.GetProjectDirectory(projectId);
        var seed = new ProjectReportArtifactStore(folder);
        await seed.WriteAsync(
            projectId,
            Request("11111111111111111111111111111111", "old"),
            CancellationToken.None);

        var crashing = new ProjectReportArtifactStore(
            folder,
            TimeProvider.System,
            new ReportArtifactStoreTestHooks
            {
                OnCheckpoint = checkpoint =>
                {
                    if (checkpoint == ReportArtifactStoreCheckpoint.FileTransitionsApplied)
                    {
                        throw new ReportArtifactStoreSimulatedCrashException("simulated process death");
                    }
                }
            });
        await Assert.ThrowsAsync<ReportArtifactStoreSimulatedCrashException>(() => crashing.WriteAsync(
            projectId,
            Request("22222222222222222222222222222222", "new"),
            CancellationToken.None));

        var journalPath = Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName);
        var formalPath = Assert.Single(Directory.GetFiles(projectDirectory, "*.xlsx"));
        await File.WriteAllTextAsync(formalPath, "content copied outside the journal transaction");
        Assert.True(File.Exists(journalPath));

        const string correlationId = "journal-conflict-correlation";
        var loadFailure = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId }),
            CancellationToken.None,
            correlationId));
        Assert.Equal(JetErrorCodes.ArtifactRecoveryConflict, loadFailure.Code);
        Assert.True(File.Exists(journalPath));

        var supportExport = await host.DispatchAsync(
            "support.log.export",
            JsonSerializer.Serialize(new { projectId, correlationId }));
        var supportPath = supportExport.GetProperty("filePath").GetString()!;
        var supportText = await File.ReadAllTextAsync(supportPath);
        Assert.Contains("artifact.recovery.conflict", supportText, StringComparison.Ordinal);
        Assert.Contains("artifact_recovery_conflict", supportText, StringComparison.Ordinal);
        Assert.Contains(correlationId, supportText, StringComparison.Ordinal);
        Assert.DoesNotContain(projectId, supportText, StringComparison.Ordinal);
        Assert.DoesNotContain(projectDirectory, supportText, StringComparison.OrdinalIgnoreCase);

        var deleted = await host.DispatchAsync(
            "project.delete",
            JsonSerializer.Serialize(new { projectId }));

        Assert.True(deleted.GetProperty("ok").GetBoolean());
        Assert.False(Directory.Exists(projectDirectory));
        var list = await host.DispatchAsync("project.listLocal");
        Assert.DoesNotContain(
            list.GetProperty("projects").EnumerateArray(),
            project => string.Equals(
                project.GetProperty("projectId").GetString(),
                projectId,
                StringComparison.OrdinalIgnoreCase));
    }

    private static ReportArtifactWriteRequest Request(string validationRunId, string content)
        => new(
            ReportArtifactKind.ValidationReport,
            new ReportArtifactSourceRefs(ValidationRunId: validationRunId),
            (output, cancellationToken) => output.WriteAsync(
                Encoding.UTF8.GetBytes(content),
                cancellationToken).AsTask());
}
