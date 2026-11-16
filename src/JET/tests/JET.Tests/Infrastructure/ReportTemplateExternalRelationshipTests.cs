using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 固定範本不得保留會讓 Excel 嘗試更新外部活頁簿的 package 關係；
/// WorkingPaper 既有的可點 hyperlink 不是 external workbook link，必須保留。
/// </summary>
public sealed partial class ReportTemplateExternalRelationshipTests
{
    private static readonly XNamespace PackageRelationships =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Spreadsheet =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Theory]
    [InlineData("INFReport.xlsx", 0)]
    [InlineData("WorkingPaper.xlsx", 1)]
    public void FixedTemplate_HasNoExternalWorkbookLinks_AndPreservesExpectedHyperlinks(
        string fileName,
        int expectedHyperlinks)
    {
        using var archive = ZipFile.OpenRead(Path.Combine(TemplateRoot(), fileName));

        var relationships = archive.Entries
            .Where(entry => entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            .SelectMany(entry => Load(entry).Root!
                .Elements(PackageRelationships + "Relationship"))
            .ToArray();

        Assert.DoesNotContain(
            relationships,
            relationship => ((string?)relationship.Attribute("Type"))?
                .EndsWith("/externalLink", StringComparison.OrdinalIgnoreCase) == true
                || ((string?)relationship.Attribute("Type"))?
                    .EndsWith("/externalLinkPath", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(
            archive.Entries,
            entry => entry.FullName.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase));

        var workbook = Load(Assert.IsType<ZipArchiveEntry>(archive.GetEntry("xl/workbook.xml")));
        Assert.Empty(workbook.Descendants(Spreadsheet + "externalReference"));
        Assert.DoesNotContain(
            workbook.Descendants(Spreadsheet + "definedName"),
            name => name.Value.Contains('[', StringComparison.Ordinal));

        Assert.Equal(
            expectedHyperlinks,
            relationships.Count(relationship => ((string?)relationship.Attribute("Type"))?
                .EndsWith("/hyperlink", StringComparison.OrdinalIgnoreCase) == true));
    }

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static string TemplateRoot() => Path.Combine(SolutionRoot(), "JET", "Templates");

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
