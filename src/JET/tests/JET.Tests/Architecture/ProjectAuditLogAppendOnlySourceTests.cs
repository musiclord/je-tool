using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// audit_event_log 的 append-only 程式紀律守衛。SQLite 與 SQL Server 已由 schema v9 的 trigger
/// 在資料庫層強制，DuckDB 1.5.3 沒有可用的引擎機制（見 guide §13），因此 persistence 層一律不得
/// 出現針對該表的 UPDATE 或 DELETE；少了這條，DuckDB 專案的 append-only 就沒有任何守門。
/// </summary>
public sealed partial class ProjectAuditLogAppendOnlySourceTests
{
    [Fact]
    public void Persistence_HasNoUpdateOrDeleteAgainstAuditEventLog()
    {
        var persistence = Path.Combine(RepoRoot(), "JET", "Infrastructure", "Persistence");
        var files = Directory
            .EnumerateFiles(persistence, "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);

        var violations = files
            .SelectMany(file => AuditEventLogMutation().Matches(File.ReadAllText(file))
                .Select(match => $"{Path.GetRelativePath(RepoRoot(), file)}: {match.Value}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("UPDATE audit_event_log SET subject_count = 0")]
    [InlineData("update {s}.audit_event_log set replaced_count = 1")]
    [InlineData("DELETE FROM audit_event_log")]
    [InlineData("delete  from  {s}.audit_event_log WHERE event_id = @id")]
    public void GuardDetector_RejectsMutationOfAuditEventLog(string source)
    {
        Assert.Matches(AuditEventLogMutation(), source);
    }

    [Theory]
    [InlineData("INSERT INTO audit_event_log (event_id) VALUES (@eventId)")]
    [InlineData("SELECT operation FROM audit_event_log WHERE target_id = @dataset")]
    [InlineData("CREATE INDEX ix_audit_event_log_occurred ON audit_event_log (occurred_utc, event_id)")]
    [InlineData("UPDATE schema_info SET value = '9' WHERE key = 'schema_version'")]
    public void GuardDetector_AllowsAppendReadAndSchemaStatements(string source)
    {
        Assert.DoesNotMatch(AuditEventLogMutation(), source);
    }

    [GeneratedRegex(
        @"\b(?:UPDATE|DELETE\s+FROM)\s+(?:\{s\}\.)?audit_event_log\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuditEventLogMutation();

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }
}
