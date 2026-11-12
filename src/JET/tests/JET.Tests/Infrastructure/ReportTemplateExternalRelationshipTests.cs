using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Xunit;
using JET.Tests.TestInfrastructure;

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
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
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

        // 只有 Step 1-2 A4 與 step4 A12 的文字可以和來源不同；其餘儲存格必須仍與來源相同。
        var expected = ExpectedSanitizationPackageDiffs.AsEnumerable();
        if (fileName == "WorkingPaper.xlsx")
        {
            expected = expected.Append("changed:xl/worksheets/sheet5.xml");
            var sourceSheet = Load(sourceArchive.GetEntry("xl/worksheets/sheet5.xml")!);
            var runtimeSheet = Load(runtimeArchive.GetEntry("xl/worksheets/sheet5.xml")!);
            var original = sourceSheet.Descendants(Spreadsheet + "c").Single(c => (string?)c.Attribute("r") == "A4");
            var revised = runtimeSheet.Descendants(Spreadsheet + "c").Single(c => (string?)c.Attribute("r") == "A4");
            Assert.Equal("此處底稿係記錄分錄編製人員資訊",
                string.Concat(revised.Descendants(Spreadsheet + "t").Select(t => t.Value)));
            Assert.Equal((string?)original.Attribute("s"), (string?)revised.Attribute("s"));
            original.ReplaceWith(new XElement(revised));
            Assert.True(XNode.DeepEquals(sourceSheet.Root, runtimeSheet.Root),
                "Only the requested A4 content may differ.");

            // 空白傳票號碼的說明寫在範本 step4 A12，改的是 A12 引用的共用字串。
            // 這裡只允許那一則字串不同，而且它只被 step4 A12 引用。
            expected = expected.Append("changed:xl/sharedStrings.xml");
            AssertOnlyStep4SelectionNoteStringChanged(sourceArchive, runtimeArchive);
        }
        Assert.Equal(expected.Order(StringComparer.Ordinal), actualDiffs);
    }

    private static void AssertOnlyStep4SelectionNoteStringChanged(ZipArchive sourceArchive, ZipArchive runtimeArchive)
    {
        const string Step4SheetEntry = "xl/worksheets/sheet9.xml";
        var sourceStrings = Load(sourceArchive.GetEntry("xl/sharedStrings.xml")!).Root!
            .Elements(Spreadsheet + "si").ToArray();
        var runtimeStrings = Load(runtimeArchive.GetEntry("xl/sharedStrings.xml")!).Root!
            .Elements(Spreadsheet + "si").ToArray();
        Assert.Equal(sourceStrings.Length, runtimeStrings.Length);
        var changed = Enumerable.Range(0, sourceStrings.Length)
            .Where(index => !XNode.DeepEquals(sourceStrings[index], runtimeStrings[index]))
            .ToArray();
        var index = Assert.Single(changed);

        var step4 = Load(runtimeArchive.GetEntry(Step4SheetEntry)!);
        var a12 = step4.Descendants(Spreadsheet + "c").Single(c => (string?)c.Attribute("r") == "A12");
        Assert.Equal("s", (string?)a12.Attribute("t"));
        Assert.Equal(index.ToString(System.Globalization.CultureInfo.InvariantCulture), a12.Element(Spreadsheet + "v")?.Value);
        var references = runtimeArchive.Entries
            .Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal))
            .SelectMany(entry => Load(entry).Descendants(Spreadsheet + "c")
                .Where(c => (string?)c.Attribute("t") == "s"
                    && c.Element(Spreadsheet + "v")?.Value == a12.Element(Spreadsheet + "v")!.Value)
                .Select(c => $"{entry.FullName}!{(string?)c.Attribute("r")}"))
            .ToArray();
        Assert.Equal([$"{Step4SheetEntry}!A12"], references);

        Assert.Equal(
            "因為設定高風險範圍條件，從母體#2挑選之分錄傳票(執行重大性或其他固定金額不應作為挑選的門檻)",
            string.Concat(sourceStrings[index].Descendants(Spreadsheet + "t").Select(t => t.Value)));
        Assert.Equal(
            "因為設定高風險範圍條件，從母體#2挑選之分錄傳票(執行重大性或其他固定金額不應作為挑選的門檻)"
                + "；空白傳票號碼的命中分錄逐筆列出，不計傳票張數，金額為該筆借方金額。",
            string.Concat(runtimeStrings[index].Descendants(Spreadsheet + "t").Select(t => t.Value)));
        var sourceRest = new XElement(sourceStrings[index]);
        var runtimeRest = new XElement(runtimeStrings[index]);
        sourceRest.Elements(Spreadsheet + "t").Remove();
        runtimeRest.Elements(Spreadsheet + "t").Remove();
        Assert.True(XNode.DeepEquals(sourceRest, runtimeRest), "Only the A12 text may differ.");
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
