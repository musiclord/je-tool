using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 報告與範本的文字儲存格只透過 ExcelText 建立，來源文字才一定經過 Excel 的跳脫編碼。其他地方直接建立 Text、InlineString
/// 或 is 元素時，含控制字元的來源資料會讓整批報告匯出失敗。文字寫成 inline string 還是共用字串表，也只由 ExcelText 決定，
/// 所以其他地方不直接把格子的型別設成 inline string 或共用字串，也不自己建立共用字串表的項目。
/// </summary>
public sealed partial class ExcelTextOwnershipTests
{
    [Fact]
    public void ProductionCode_CreatesCellTextOnlyThroughExcelText()
    {
        var product = Path.Combine(SolutionRoot(), "JET");
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
                .Select(match => $"{Path.GetRelativePath(SolutionRoot(), file)}: {match.Value}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("cell.InlineString = new InlineString(new Text(value));")]
    [InlineData("new Text(value) { Space = SpaceProcessingModeValues.Preserve }")]
    [InlineData("cell.Add(new XElement(spreadsheet + \"is\", value));")]
    [InlineData("new XElement(Spreadsheet+\"is\")")]
    [InlineData("new SharedStringItem(new Text(value))")]
    [InlineData("new XElement(spreadsheet + \"si\", value)")]
    [InlineData("cell.DataType = CellValues.InlineString;")]
    [InlineData("new Cell { DataType = CellValues.SharedString, CellValue = new CellValue(index) }")]
    [InlineData("cell.SetAttributeValue(\"t\", \"inlineStr\");")]
    [InlineData("cell.SetAttributeValue(\"t\",  \"s\");")]
    public void GuardDetector_RejectsDirectCellText(string source)
    {
        Assert.Matches(DirectCellText(), source);
    }

    [Theory]
    [InlineData("cell.InlineString = ExcelText.InlineString(value);")]
    [InlineData("cell.Add(ExcelText.InlineStringElement(Spreadsheet, value));")]
    [InlineData("\"is\" => side + \"科目屬於「\" + categories + \"」\",")]
    [InlineData("var context = new TextContext();")]
    [InlineData("if (cell.DataType?.Value == CellValues.SharedString)")]
    [InlineData("return cell.DataType == CellValues.InlineString;")]
    [InlineData("cell.DataType = CellValues.Number;")]
    [InlineData("cell.SetAttributeValue(\"s\", style);")]
    [InlineData("var items = table.Elements<SharedStringItem>();")]
    public void GuardDetector_AllowsExcelTextAndUnrelatedCode(string source)
    {
        Assert.DoesNotMatch(DirectCellText(), source);
    }

    [GeneratedRegex(
        @"\bnew\s+(?:Text|InlineString|SharedStringItem)\s*\(|\+\s*""(?:is|si)"""
        + @"|DataType\s*=\s*CellValues\.(?:InlineString|SharedString)\b"
        + @"|SetAttributeValue\(\s*""t""\s*,\s*""(?:inlineStr|s)""\s*\)")]
    private static partial Regex DirectCellText();

    /// <summary>
    /// 上面的檢查沒有涵蓋的其他寫法，同樣會繞過 ExcelText：用物件初始式建立 Text、InlineString 或共用字串項目，
    /// 用 EnumValue 設格子的型別，以及用 XAttribute 或 XmlWriter 直接寫出 t="inlineStr" 或 t="s"。
    /// </summary>
    [Fact]
    public void ProductionCode_SetsTextCellTypeOnlyThroughExcelText()
    {
        var product = Path.Combine(SolutionRoot(), "JET");
        var excluded = new[] { Path.Combine(product, "bin"), Path.Combine(product, "obj") };
        var violations = Directory
            .EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
            .Where(file => !excluded.Any(folder => file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Where(file => Path.GetFileName(file) != "ExcelText.cs")
            .Order(StringComparer.Ordinal)
            .SelectMany(file => OtherCellTextWriting().Matches(File.ReadAllText(file))
                .Select(match => $"{Path.GetRelativePath(SolutionRoot(), file)}: {match.Value}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("cell.InlineString = new InlineString { InnerXml = xml };")]
    [InlineData("var text = new Text { Text = value };")]
    [InlineData("table.Append(new SharedStringItem { InnerXml = xml });")]
    [InlineData("cell.DataType = new EnumValue<CellValues>(CellValues.InlineString);")]
    [InlineData("cell.DataType = new EnumValue<CellValues>( CellValues.SharedString );")]
    [InlineData("cell.Add(new XAttribute(\"t\", \"inlineStr\"));")]
    [InlineData("writer.WriteAttributeString(\"t\", \"s\");")]
    public void OtherWritingDetector_RejectsTextCellTypeOutsideExcelText(string source)
    {
        Assert.Matches(OtherCellTextWriting(), source);
    }

    [Theory]
    [InlineData("writer.WriteAttributeString(\"s\", style);")]
    [InlineData("writer.WriteAttributeString(\"t\", \"n\");")]
    [InlineData("cell.Add(new XAttribute(\"r\", reference));")]
    [InlineData("var settings = new XmlWriterSettings { Indent = false };")]
    [InlineData("var context = new TextContext { Name = name };")]
    [InlineData("cell.DataType = new EnumValue<CellValues>(CellValues.Number);")]
    public void OtherWritingDetector_AllowsUnrelatedCode(string source)
    {
        Assert.DoesNotMatch(OtherCellTextWriting(), source);
    }

    [GeneratedRegex(
        @"\bnew\s+(?:Text|InlineString|SharedStringItem)\s*\{"
        + @"|EnumValue<CellValues>\(\s*CellValues\.(?:InlineString|SharedString)\b"
        + @"|\bnew\s+XAttribute\(\s*""t""\s*,\s*""(?:inlineStr|s)""\s*\)"
        + @"|WriteAttributeString\(\s*""t""\s*,\s*""(?:inlineStr|s)""\s*\)")]
    private static partial Regex OtherCellTextWriting();

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
