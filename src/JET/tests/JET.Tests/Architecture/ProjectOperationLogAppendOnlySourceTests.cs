using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// operation_log 的 append-only 程式紀律守衛。SQLite 已由資料庫的 trigger
/// 在資料庫層強制；persistence 層一律不得出現針對該表的 UPDATE 或 DELETE。
/// </summary>
public sealed partial class ProjectOperationLogAppendOnlySourceTests
{
    [Fact]
    public void Persistence_HasNoUpdateOrDeleteAgainstOperationLog()
    {
        var persistence = Path.Combine(RepoRoot(), "JET", "Infrastructure", "Persistence");
        var files = Directory
            .EnumerateFiles(persistence, "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);

        var violations = files
            .SelectMany(file => OperationLogMutation().Matches(File.ReadAllText(file))
                .Select(match => $"{Path.GetRelativePath(RepoRoot(), file)}: {match.Value}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("UPDATE operation_log SET subject_count = 0")]
    [InlineData("update {s}.operation_log set replaced_count = 1")]
    [InlineData("DELETE FROM operation_log")]
    [InlineData("delete  from  {s}.operation_log WHERE event_id = @id")]
    public void GuardDetector_RejectsMutationOfOperationLog(string source)
    {
        Assert.Matches(OperationLogMutation(), source);
    }

    [Theory]
    [InlineData("INSERT INTO operation_log (event_id) VALUES (@eventId)")]
    [InlineData("SELECT operation FROM operation_log WHERE target_id = @dataset")]
    [InlineData("CREATE INDEX ix_operation_log_occurred ON operation_log (occurred_utc, event_id)")]
    [InlineData("UPDATE schema_info SET value = '9' WHERE key = 'schema_version'")]
    public void GuardDetector_AllowsAppendReadAndSchemaStatements(string source)
    {
        Assert.DoesNotMatch(OperationLogMutation(), source);
    }

    [GeneratedRegex(
        @"\b(?:UPDATE|DELETE\s+FROM)\s+(?:\{s\}\.)?operation_log\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OperationLogMutation();

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
