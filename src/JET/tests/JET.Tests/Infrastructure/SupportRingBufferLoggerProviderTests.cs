using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SupportRingBufferLoggerProviderTests
{
    [Fact]
    public void FailureSurvivesHeartbeats_AndSnapshotReportsOmittedEvents()
    {
        using var provider = new SupportRingBufferLoggerProvider(8);
        var logger = provider.CreateLogger("JET.Support");
        DispatcherDiagnostics.ActionError(logger, "import.gl.fromFile", 1, "file_read_error", new IOException("PRIVATE_PATH"));
        for (var i = 0; i < 100; i++) DispatcherDiagnostics.ActionEnd(logger, "system.ping", "ok", 1);
        var entries = provider.Snapshot();
        Assert.Equal(8, entries.Count);
        Assert.Single(entries, entry => entry.EventName == "action.error");
        Assert.Equal(93, provider.EventsOmitted);
        Assert.DoesNotContain("PRIVATE_PATH", string.Join('\n', entries.Select(SupportDiagnosticNdjson.SerializeLine)));
    }

    [Fact]
    public async Task RollbackFailure_PreservesFirstErrorAndRecordsSeparateFailureWithoutValues()
    {
        var original = new System.Text.DecoderFallbackException("PRIVATE_VALUE");
        ImportFailureDiagnostics.Attach(original, new ImportFailureContext(ImportFailureStage.Rows, 2, 2, 40));
        using var transaction = new FailingRollback();
        var rollback = await LocalImportRepository.TryRollbackAsync(transaction);
        Assert.IsType<IOException>(rollback);
        ImportFailureDiagnostics.RecordRollback(original, rollback, "sqlite");
        using var provider = new SupportRingBufferLoggerProvider(8);
        DispatcherDiagnostics.ActionError(provider.CreateLogger("JET.Support"), "import.gl.fromFile", 1, "file_read_error", original);
        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal("decode_invalid_bytes", entry.Fields["failure_cause"]);
        Assert.Equal("failed", entry.Fields["rollback_state"]);
        Assert.Equal("System.IO.IOException", entry.Fields["rollback_exception_type"]);
        Assert.DoesNotContain("PRIVATE_", SupportDiagnosticNdjson.SerializeLine(entry));
    }

    private sealed class FailingRollback : System.Data.Common.DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.Serializable;
        protected override System.Data.Common.DbConnection? DbConnection => null;
        public override void Commit() => throw new NotSupportedException();
        public override void Rollback() => throw new IOException("PRIVATE_ROLLBACK");
        public override Task RollbackAsync(CancellationToken cancellationToken = default) => Task.FromException(new IOException("PRIVATE_ROLLBACK"));
    }
    [Fact]
    public void ErrorEvent_IsAllowlistedBoundedAndSanitizedBeforeSerialization()
    {
        using var provider = new SupportRingBufferLoggerProvider(capacity: 8);
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
        });
        var logger = factory.CreateLogger("JET.Tests.SecretCategory");
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlation_id"] = "corr-123",
            ["support_project_id"] = "secret-project-id",
            ["file_path"] = @"C:\Secret\client.xlsx",
        });

        DispatcherDiagnostics.ActionError(
            logger,
            "project.load",
            57,
            JetErrorCodes.ArtifactRecoveryConflict,
            new InvalidDataException(@"secret message C:\Secret\client.xlsx"));
        logger.LogInformation(new EventId(9999, "not.allowlisted"), "secret ignored");

        var entry = Assert.Single(provider.Snapshot());
        Assert.Equal("corr-123", entry.CorrelationId);
        Assert.Equal("secret-project-id", entry.InternalProjectId);
        Assert.Equal(JetErrorCodes.ArtifactRecoveryConflict, entry.Fields["error_code"]);
        Assert.DoesNotContain("file_path", entry.Fields.Keys);

        var line = SupportDiagnosticNdjson.SerializeLine(entry);
        Assert.Contains("artifact_recovery_conflict", line, StringComparison.Ordinal);
        Assert.Contains("InvalidDataException", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-project-id", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret message", line, StringComparison.Ordinal);
        Assert.DoesNotContain("client.xlsx", line, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Secret", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Buffer_OverwritesOldestAndIgnoresDebugEvenWhenFactoryAllowsIt()
    {
        using var provider = new SupportRingBufferLoggerProvider(capacity: 2);
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
        });
        var logger = factory.CreateLogger("JET.Tests.Support");

        logger.LogDebug(new EventId(1000, "action.start"), "debug action {action}", "debug");
        DispatcherDiagnostics.ActionStart(logger, "first");
        DispatcherDiagnostics.ActionStart(logger, "second");
        DispatcherDiagnostics.ActionStart(logger, "third");

        var entries = provider.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal("second", entries[0].Fields["action"]);
        Assert.Equal("third", entries[1].Fields["action"]);
    }
}
