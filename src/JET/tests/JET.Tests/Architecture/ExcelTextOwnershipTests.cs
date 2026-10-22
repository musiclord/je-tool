using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 報告與範本的文字儲存格只透過 ExcelText 建立，來源文字才一定經過 Excel 的跳脫編碼。其他地方直接建立 Text、InlineString
/// 或 is 元素時，含控制字元的來源資料會讓整批報告匯出失敗（使用者 2026-10-21 回報）。
/// </summary>
public sealed partial class ExcelTextOwnershipTests
{
    [Fact]
    public void ProductionCode_CreatesCellTextOnlyThroughExcelText()
    {
        var product = Path.Combine(RepoRoot(), "JET");
        var excluded = new[] { Path.Combine(product, "bin"), Path.Combine(product, "obj") };
        var files = Directory
            .EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
            .Where(file => !excluded.Any(folder => file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Where(file => Path.GetFileName(file) != "ExcelText.cs")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);

        var violations = files
            .SelectMany(file => DirectCellText().Matches(File.ReadAllText(file))
                .Select(match => $"{Path.GetRelativePath(RepoRoot(), file)}: {match.Value}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("cell.InlineString = new InlineString(new Text(value));")]
    [InlineData("new Text(value) { Space = SpaceProcessingModeValues.Preserve }")]
    [InlineData("cell.Add(new XElement(spreadsheet + \"is\", value));")]
    [InlineData("new XElement(Spreadsheet+\"is\")")]
    public void GuardDetector_RejectsDirectCellText(string source)
    {
        Assert.Matches(DirectCellText(), source);
    }

    [Theory]
    [InlineData("cell.InlineString = ExcelText.InlineString(value);")]
    [InlineData("cell.Add(ExcelText.InlineStringElement(Spreadsheet, value));")]
    [InlineData("\"is\" => side + \"科目屬於「\" + categories + \"」\",")]
    [InlineData("var context = new TextContext();")]
    public void GuardDetector_AllowsExcelTextAndUnrelatedCode(string source)
    {
        Assert.DoesNotMatch(DirectCellText(), source);
    }

    [GeneratedRegex(@"\bnew\s+(?:Text|InlineString)\s*\(|\+\s*""is""")]
    private static partial Regex DirectCellText();

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
