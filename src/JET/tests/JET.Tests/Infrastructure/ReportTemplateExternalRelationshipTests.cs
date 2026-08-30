using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 固定範本不得保留會讓 Excel 嘗試更新外部活頁簿的 package 關係；
/// WorkingPaper 既有的可點 hyperlink 不是 external workbook link，必須保留。
/// </summary>
public sealed class ReportTemplateExternalRelationshipTests
{
    private static readonly string[] ExpectedSanitizationPackageDiffs =
    [
        "changed:[Content_Types].xml",
        "changed:xl/_rels/workbook.xml.rels",
        "changed:xl/workbook.xml",
        "removed:xl/externalLinks/_rels/externalLink1.xml.rels",
        "removed:xl/externalLinks/_rels/externalLink2.xml.rels",
        "removed:xl/externalLinks/_rels/externalLink3.xml.rels",
        "removed:xl/externalLinks/externalLink1.xml",
        "removed:xl/externalLinks/externalLink2.xml",
        "removed:xl/externalLinks/externalLink3.xml"
    ];

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

    [Theory]
    [InlineData("INFReport.xlsx")]
    [InlineData("WorkingPaper.xlsx")]
    public void FixedTemplate_DiffFromSourceAnchor_IsOnlyExternalWorkbookSanitation(string fileName)
    {
        using var sourceArchive = ZipFile.OpenRead(Path.Combine(SourceTemplateRoot(), fileName));
        using var runtimeArchive = ZipFile.OpenRead(Path.Combine(TemplateRoot(), fileName));
        var sourceEntries = HashEntries(sourceArchive);
        var runtimeEntries = HashEntries(runtimeArchive);

        var actualDiffs = sourceEntries.Keys
            .Concat(runtimeEntries.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(entryName =>
            {
                if (!sourceEntries.ContainsKey(entryName))
                {
                    return $"added:{entryName}";
                }

                if (!runtimeEntries.ContainsKey(entryName))
                {
                    return $"removed:{entryName}";
                }

                return sourceEntries[entryName] == runtimeEntries[entryName]
                    ? null
                    : $"changed:{entryName}";
            })
            .Where(diff => diff is not null)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedSanitizationPackageDiffs, actualDiffs);
    }

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static Dictionary<string, string> HashEntries(ZipArchive archive) =>
        archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var stream = entry.Open();
                return Convert.ToHexString(SHA256.HashData(stream));
            },
            StringComparer.Ordinal);

    private static string TemplateRoot() => Path.Combine(RepoRoot(), "JET", "Templates");

    private static string SourceTemplateRoot() => Path.GetFullPath(
        Path.Combine(RepoRoot(), "..", "..", "data"));

    private static string RepoRoot()
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
