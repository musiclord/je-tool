using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SupportRingBufferLoggerProviderTests
{
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
