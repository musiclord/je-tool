using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// DuckDB/local target 投影只能走 IBulkRowWriter（DuckDB 原生 Appender）。若有人另加具體
/// INSERT INTO target_gl_entry/target_tb_balance，會繞過這條效能與 auto-id 契約，本守衛即擋下。
/// </summary>
public sealed partial class DuckDbTargetWriteGuardTests
{
    [Fact]
    public void DuckDbAndLocalPersistence_HaveNoConcreteDirectTargetInsert()
    {
        var persistence = Path.Combine(RepoRoot(), "JET", "Infrastructure", "Persistence");
        var files = new[] { "DuckDb", "Local" }
            .SelectMany(folder => Directory.EnumerateFiles(
                Path.Combine(persistence, folder), "*.cs", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);

        var violations = files
            .SelectMany(file => DirectTargetInsert().Matches(File.ReadAllText(file))
                .Select(match => $"{Path.GetRelativePath(RepoRoot(), file)}: {match.Value}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("INSERT INTO target_gl_entry (entry_id) VALUES (1)")]
    [InlineData("insert  into target_tb_balance(balance_id) values(1)")]
    public void GuardDetector_RejectsConcreteTargetInsert(string source)
    {
        Assert.Matches(DirectTargetInsert(), source);
    }

    [Theory]
    [InlineData("database.CreateBulkRowWriter(connection, tx, \"target_gl_entry\", columns)")]
    [InlineData("INSERT INTO {table} ({columns}) VALUES ({values})")]
    [InlineData("DELETE FROM target_tb_balance")]
    public void GuardDetector_AllowsBulkWriterAndNonInsertReferences(string source)
    {
        Assert.DoesNotMatch(DirectTargetInsert(), source);
    }

    [GeneratedRegex(
        @"\bINSERT\s+INTO\s+(?:\{s\}\.)?(?:target_gl_entry|target_tb_balance)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectTargetInsert();

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
