using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class DiagnosticDbTests
{
    [Fact]
    public void SqlExecuted_DebugLogger_EmitsStructuredSqlEvent()
    {
        // Arrange
        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var logger = factory.CreateLogger<DiagnosticDbTests>();

            // Act
            DiagnosticDbLog.SqlExecuted(logger, "SELECT 1", "@p=1", 12, 3, "sqlite");
        }

        // Assert
        var entry = Assert.Single(diagnostic.Snapshot(), e => e.EventName == "sql.executed");
        Assert.Equal("Debug", entry.Level);
        Assert.Equal("sqlite", entry.Fields["provider"]?.ToString());
        Assert.Equal("SELECT 1", entry.Fields["sql"]?.ToString());
        Assert.Equal("@p=1", entry.Fields["parameters"]?.ToString());
        Assert.Equal(12L, Convert.ToInt64(entry.Fields["duration_ms"]));
        Assert.Equal(3, Convert.ToInt32(entry.Fields["rows_affected"]));
    }

    [Fact]
    public void TransactionBegin_DebugLogger_EmitsStructuredBeginEvent()
    {
        // Arrange
        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var logger = factory.CreateLogger<DiagnosticDbTests>();

            // Act
            DiagnosticDbLog.TransactionBegin(logger, "sqlite");
        }

        // Assert
        var entry = Assert.Single(diagnostic.Snapshot(), e => e.EventName == "tx.begin");
        Assert.Equal("Debug", entry.Level);
        Assert.Equal("sqlite", entry.Fields["provider"]?.ToString());
    }

    [Fact]
    public void TransactionCommit_DebugLogger_EmitsStructuredCommitEvent()
    {
        // Arrange
        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var logger = factory.CreateLogger<DiagnosticDbTests>();

            // Act
            DiagnosticDbLog.TransactionCommit(logger, "sqlServer", 34);
        }

        // Assert
        var entry = Assert.Single(diagnostic.Snapshot(), e => e.EventName == "tx.commit");
        Assert.Equal("Debug", entry.Level);
        Assert.Equal("sqlServer", entry.Fields["provider"]?.ToString());
        Assert.Equal(34L, Convert.ToInt64(entry.Fields["duration_ms"]));
    }

    [Fact]
    public void Committed_CalledAfterSettled_DoesNotLogSecondOutcome()
    {
        // Arrange
        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var logger = factory.CreateLogger<DiagnosticDbTests>();
            using var scope = new DiagnosticTransactionScope(logger, "sqlite");

            // Act
            scope.Committed();
            scope.Committed();
        }

        // Assert
        var entries = diagnostic.Snapshot();
        var begin = Assert.Single(entries, e => e.EventName == "tx.begin");
        var commit = Assert.Single(entries, e => e.EventName == "tx.commit");
        Assert.DoesNotContain(entries, e => e.EventName == "tx.rollback");
        Assert.NotNull(begin.TransactionId);
        Assert.Equal(begin.TransactionId, commit.TransactionId);
        Assert.Equal("sqlite", commit.Fields["provider"]?.ToString());
        Assert.True(Convert.ToInt64(commit.Fields["duration_ms"]) >= 0);
    }

    [Fact]
    public void RolledBack_CalledAfterSettled_DoesNotLogSecondOutcome()
    {
        // Arrange
        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var logger = factory.CreateLogger<DiagnosticDbTests>();
            using var scope = new DiagnosticTransactionScope(logger, "sqlServer");

            // Act
            scope.RolledBack();
            scope.RolledBack();
        }

        // Assert
        var entries = diagnostic.Snapshot();
        var begin = Assert.Single(entries, e => e.EventName == "tx.begin");
        var rollback = Assert.Single(entries, e => e.EventName == "tx.rollback");
        Assert.DoesNotContain(entries, e => e.EventName == "tx.commit");
        Assert.NotNull(begin.TransactionId);
        Assert.Equal(begin.TransactionId, rollback.TransactionId);
        Assert.Equal("sqlServer", rollback.Fields["provider"]?.ToString());
        Assert.True(Convert.ToInt64(rollback.Fields["duration_ms"]) >= 0);
    }

    [Fact]
    public void TransactionRollback_DebugLogger_EmitsStructuredRollbackEvent()
    {
        // Arrange
        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var logger = factory.CreateLogger<DiagnosticDbTests>();

            // Act
            DiagnosticDbLog.TransactionRollback(logger, "sqlite", 89);
        }

        // Assert
        var entry = Assert.Single(diagnostic.Snapshot(), e => e.EventName == "tx.rollback");
        Assert.Equal("Debug", entry.Level);
        Assert.Equal("sqlite", entry.Fields["provider"]?.ToString());
        Assert.Equal(89L, Convert.ToInt64(entry.Fields["duration_ms"]));
    }



    private static (RingBufferLoggerProvider Diagnostic, ILoggerFactory Factory) NewDiagnostic()
    {
        var diagnostic = new RingBufferLoggerProvider(100);
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(diagnostic);
        });
        return (diagnostic, factory);
    }
}
