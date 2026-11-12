using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 本機 target 投影只能走 IBulkRowWriter。若有人另加具體 INSERT INTO gl/tb，會繞過這條效能與 auto-id 契約，本守衛即擋下。
/// 檢查 Sqlite 與 Local 兩個資料夾。
/// </summary>
public sealed partial class LocalTargetWriteGuardTests
{
    [Fact]
    public void SqliteAndLocalPersistence_HaveNoConcreteDirectTargetInsert()
    {
        var persistence = Path.Combine(SolutionRoot(), "JET", "Infrastructure", "Persistence");
        var files = new[] { "Sqlite", "Local" }
            .SelectMany(folder => Directory.EnumerateFiles(
                Path.Combine(persistence, folder), "*.cs", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);

        var violations = files
            .SelectMany(file => DirectTargetInsert().Matches(File.ReadAllText(file))
                .Select(match => $"{Path.GetRelativePath(SolutionRoot(), file)}: {match.Value}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("INSERT INTO gl (entry_id) VALUES (1)")]
    [InlineData("insert  into tb(balance_id) values(1)")]
    public void GuardDetector_RejectsConcreteTargetInsert(string source)
    {
        Assert.Matches(DirectTargetInsert(), source);
    }

    [Theory]
    [InlineData("database.CreateBulkRowWriter(connection, tx, \"gl\", columns)")]
    [InlineData("INSERT INTO {table} ({columns}) VALUES ({values})")]
    [InlineData("DELETE FROM tb")]
    public void GuardDetector_AllowsBulkWriterAndNonInsertReferences(string source)
    {
        Assert.DoesNotMatch(DirectTargetInsert(), source);
    }

    [GeneratedRegex(
        @"\bINSERT\s+INTO\s+(?:\{s\}\.)?(?:gl|tb)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectTargetInsert();

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
