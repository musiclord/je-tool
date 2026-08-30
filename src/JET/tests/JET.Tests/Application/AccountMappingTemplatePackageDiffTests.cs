using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class AccountMappingTemplatePackageDiffTests
{
    private static readonly string[] ExpectedCategories =
    [
        "Revenue",
        "Receivables",
        "Cash",
        "Receipt in advance",
        "Others"
    ];

    [Fact]
    public async Task Writer_ChangesOnlyAuthorizedTemplatePartsAndDynamicRegions()
    {
        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            ReportTemplateCatalog.AccountMapping);
        var beforeBytes = await File.ReadAllBytesAsync(templatePath);
        Assert.Equal(
            "20A592907609933D837C8A617FABD11BD046C7D271AE55522545AC4CB61444AB",
            Convert.ToHexString(SHA256.HashData(beforeBytes)));

        var rows = new AccountMappingTemplateRow[]
        {
            new("001101", "Cash on hand"),
            new("1120", "Accounts receivable"),
            new("2160", "Customer deposits"),
            new("4100", "Revenue"),
            new("9999", null)
        };
        using var output = new MemoryStream();
        await new AccountMappingTemplateWriter().WriteAsync(output, rows, CancellationToken.None);

        AccountMappingTemplatePackageDiff.AssertAllowed(beforeBytes, output.ToArray(), rows);
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(templatePath));

        var evidencePath = Environment.GetEnvironmentVariable("JET_ACCOUNT_MAPPING_EVIDENCE_PATH");
        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(evidencePath))!);
            await File.WriteAllBytesAsync(evidencePath, output.ToArray());
        }
    }

    [Fact]
    public async Task AllowedDiffOracle_RejectsUnauthorizedWorkbookPartMutation()
    {
        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            ReportTemplateCatalog.AccountMapping);
        var sourceBytes = await File.ReadAllBytesAsync(templatePath);
        var rows = new[] { new AccountMappingTemplateRow("1101", "Cash") };
        using var output = new MemoryStream();
        await new AccountMappingTemplateWriter().WriteAsync(output, rows, CancellationToken.None);
        var mutated = AccountMappingTemplatePackageDiff.MutateWorkbookPart(output.ToArray());

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
        {
            AccountMappingTemplatePackageDiff.AssertAllowed(sourceBytes, mutated, rows);
        });
    }

    private static class AccountMappingTemplatePackageDiff
    {
        private static readonly XNamespace Spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace OfficeRelationships =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRelationships =
            "http://schemas.openxmlformats.org/package/2006/relationships";

        internal static void AssertAllowed(
            byte[] sourceBytes,
            byte[] resultBytes,
            IReadOnlyList<AccountMappingTemplateRow> rows)
        {
            var source = PackageSnapshot.Capture(sourceBytes);
            var result = PackageSnapshot.Capture(resultBytes);
            Assert.Equal(source.PartNames, result.PartNames);

            var accountPart = source.ResolveWorksheetPart("AccountMapping");
            var listPart = source.ResolveWorksheetPart("List");
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                accountPart,
                listPart,
                "xl/sharedStrings.xml",
                "xl/styles.xml"
            };
            var changed = source.PartNames
                .Where(name => !source.Hash(name).SequenceEqual(result.Hash(name)))
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(
                allowed.SetEquals(changed),
                $"Changed package parts were: {string.Join(", ", changed.Order(StringComparer.Ordinal))}");

            AssertStaticAccountRegion(source.Xml(accountPart), result.Xml(accountPart));
            AssertStaticListRegion(source.Xml(listPart), result.Xml(listPart));
            AssertStyles(source.Xml("xl/styles.xml"), result.Xml("xl/styles.xml"));
            AssertDynamicContent(result.Xml(accountPart), result.Xml(listPart), rows);
        }

        private static void AssertStaticAccountRegion(XDocument source, XDocument result)
        {
            NormalizeAccountAuthorizedRegions(source);
            NormalizeAccountAuthorizedRegions(result);
            Assert.Equal(Canonical(source.Root!), Canonical(result.Root!));
        }

        private static void AssertStaticListRegion(XDocument source, XDocument result)
        {
            NormalizeDimension(source);
            NormalizeDimension(result);
            RemoveRowsAtOrAfter(source, 2);
            RemoveRowsAtOrAfter(result, 2);
            Assert.Equal(Canonical(source.Root!), Canonical(result.Root!));
        }

        private static void NormalizeAccountAuthorizedRegions(XDocument document)
        {
            NormalizeDimension(document);
            RemoveRowsAtOrAfter(document, 4);
            var instruction = document.Descendants(Spreadsheet + "c")
                .Single(cell => string.Equals(
                    (string?)cell.Attribute("r"),
                    "A2",
                    StringComparison.Ordinal));
            instruction.SetAttributeValue("t", "__authorized_instruction_type__");
            instruction.RemoveNodes();
            foreach (var column in document.Descendants(Spreadsheet + "col"))
            {
                var minimum = ParseUInt(column.Attribute("min"));
                var maximum = ParseUInt(column.Attribute("max"));
                if (minimum <= 3 && maximum >= 1)
                {
                    column.SetAttributeValue("style", "__authorized_style__");
                    column.Attribute("bestFit")?.Remove();
                }
            }

            document.Root!.Elements(Spreadsheet + "sheetProtection").Remove();
            document.Root.Elements(Spreadsheet + "dataValidations").Remove();
            document.Root.Elements(Spreadsheet + "extLst").Remove();
        }

        private static void NormalizeDimension(XDocument document) =>
            document.Root!
                .Element(Spreadsheet + "dimension")!
                .SetAttributeValue("ref", "__authorized_dimension__");

        private static void RemoveRowsAtOrAfter(XDocument document, uint firstRow) =>
            document.Descendants(Spreadsheet + "row")
                .Where(row => ParseUInt(row.Attribute("r")) >= firstRow)
                .Remove();

        private static void AssertStyles(XDocument source, XDocument result)
        {
            var sourceRoot = source.Root!;
            var resultRoot = result.Root!;
            var sourceFormats = sourceRoot.Element(Spreadsheet + "cellXfs")!;
            var resultFormats = resultRoot.Element(Spreadsheet + "cellXfs")!;
            var sourceCells = sourceFormats.Elements(Spreadsheet + "xf").ToArray();
            var resultCells = resultFormats.Elements(Spreadsheet + "xf").ToArray();

            Assert.Equal(15, sourceCells.Length);
            Assert.Equal(17, resultCells.Length);
            for (var index = 0; index < sourceCells.Length; index++)
            {
                Assert.Equal(Canonical(sourceCells[index]), Canonical(resultCells[index]));
            }

            AssertLockedClone(sourceCells[7], resultCells[15]);
            AssertLockedClone(sourceCells[8], resultCells[16]);

            var sourceWithoutCellFormats = new XElement(sourceRoot);
            var resultWithoutCellFormats = new XElement(resultRoot);
            sourceWithoutCellFormats.Element(Spreadsheet + "cellXfs")!.Remove();
            resultWithoutCellFormats.Element(Spreadsheet + "cellXfs")!.Remove();
            RemoveFontFamily(sourceWithoutCellFormats);
            RemoveFontFamily(resultWithoutCellFormats);
            Assert.Equal(
                Canonical(sourceWithoutCellFormats),
                Canonical(resultWithoutCellFormats));

            Assert.All(
                resultRoot.Element(Spreadsheet + "fonts")!
                    .Elements(Spreadsheet + "font"),
                font =>
                {
                    Assert.Equal(
                        "微軟正黑體",
                        (string?)font.Element(Spreadsheet + "name")?.Attribute("val"));
                    Assert.Null(font.Element(Spreadsheet + "scheme"));
                });
        }

        private static void RemoveFontFamily(XElement styles)
        {
            foreach (var font in styles.Element(Spreadsheet + "fonts")?
                         .Elements(Spreadsheet + "font") ?? [])
            {
                font.Element(Spreadsheet + "name")?.Remove();
                font.Element(Spreadsheet + "scheme")?.Remove();
            }
        }

        private static void AssertLockedClone(XElement prototype, XElement actual)
        {
            var expected = new XElement(prototype);
            expected.SetAttributeValue("applyProtection", "1");
            expected.Elements(Spreadsheet + "protection").Remove();
            expected.Add(new XElement(
                Spreadsheet + "protection",
                new XAttribute("locked", "1")));
            Assert.Equal(Canonical(expected), Canonical(actual));
        }

        private static void AssertDynamicContent(
            XDocument account,
            XDocument list,
            IReadOnlyList<AccountMappingTemplateRow> expectedRows)
        {
            Assert.Equal(
                $"A1:D{Math.Max(4, expectedRows.Count + 3)}",
                (string?)account.Root!.Element(Spreadsheet + "dimension")!.Attribute("ref"));

            var columnStyles = account.Descendants(Spreadsheet + "col")
                .Where(column => ParseUInt(column.Attribute("min")) <= 3)
                .ToDictionary(
                    column => ParseUInt(column.Attribute("min")),
                    column => ParseUInt(column.Attribute("style")));
            Assert.Equal(15U, columnStyles[1]);
            Assert.Equal(16U, columnStyles[2]);
            Assert.Equal(16U, columnStyles[3]);
            var autoFitColumns = account.Descendants(Spreadsheet + "col")
                .Where(column => ParseUInt(column.Attribute("min")) <= 3)
                .ToDictionary(
                    column => ParseUInt(column.Attribute("min")),
                    column => (
                        BestFit: (string?)column.Attribute("bestFit"),
                        CustomWidth: (string?)column.Attribute("customWidth")));
            Assert.Equal(("1", "1"), autoFitColumns[1]);
            Assert.Equal(("1", "1"), autoFitColumns[2]);
            Assert.Equal(("1", "1"), autoFitColumns[3]);

            var actualRows = account.Descendants(Spreadsheet + "row")
                .Where(row => ParseUInt(row.Attribute("r")) >= 4)
                .ToArray();
            Assert.Equal(expectedRows.Count, actualRows.Length);
            for (var index = 0; index < expectedRows.Count; index++)
            {
                var rowNumber = (uint)index + 4;
                var row = actualRows[index];
                Assert.Equal(rowNumber, ParseUInt(row.Attribute("r")));
                var cells = row.Elements(Spreadsheet + "c")
                    .ToDictionary(cell => (string)cell.Attribute("r")!, StringComparer.Ordinal);
                Assert.Equal(
                    new[] { $"A{rowNumber}", $"B{rowNumber}", $"C{rowNumber}" },
                    cells.Keys.Order(StringComparer.Ordinal).ToArray());
                Assert.Equal(expectedRows[index].AccountCode, InlineText(cells[$"A{rowNumber}"]));
                Assert.Equal(15U, ParseUInt(cells[$"A{rowNumber}"].Attribute("s")));
                Assert.Equal(expectedRows[index].AccountName ?? string.Empty, InlineText(cells[$"B{rowNumber}"]));
                Assert.Equal(16U, ParseUInt(cells[$"B{rowNumber}"].Attribute("s")));
                Assert.Equal(8U, ParseUInt(cells[$"C{rowNumber}"].Attribute("s")));
                Assert.Null(cells[$"C{rowNumber}"].Element(Spreadsheet + "v"));
                Assert.Null(cells[$"C{rowNumber}"].Element(Spreadsheet + "is"));
            }
            Assert.Equal("13.5", (string?)actualRows[0].Attribute("ht"));
            Assert.Equal("1", (string?)actualRows[0].Attribute("thickTop"));

            var instructionCell = account.Descendants(Spreadsheet + "c")
                .Single(cell => string.Equals(
                    (string?)cell.Attribute("r"),
                    "A2",
                    StringComparison.Ordinal));
            Assert.Equal(
                "請在 C 欄選擇標準科目分類；允許分類僅限 Revenue、Receivables、Cash、Receipt in advance、Others；A、B 欄由系統產生，不應修改。",
                InlineText(instructionCell));

            var protection = Assert.Single(account.Root.Elements(Spreadsheet + "sheetProtection"));
            Assert.Equal("1", (string?)protection.Attribute("sheet"));
            Assert.Equal("1", (string?)protection.Attribute("objects"));
            Assert.Equal("1", (string?)protection.Attribute("scenarios"));

            var validations = Assert.Single(account.Root.Elements(Spreadsheet + "dataValidations"));
            var validation = Assert.Single(validations.Elements(Spreadsheet + "dataValidation"));
            Assert.Equal("list", (string?)validation.Attribute("type"));
            Assert.Equal("1", (string?)validation.Attribute("allowBlank"));
            Assert.Equal($"C4:C{expectedRows.Count + 3}", (string?)validation.Attribute("sqref"));
            Assert.Equal("List!$A$2:$A$6", validation.Element(Spreadsheet + "formula1")!.Value);
            Assert.Empty(account.Root.Elements(Spreadsheet + "extLst"));

            Assert.Equal("A1:A6", (string?)list.Root!.Element(Spreadsheet + "dimension")!.Attribute("ref"));
            var categories = list.Descendants(Spreadsheet + "row")
                .Where(row => ParseUInt(row.Attribute("r")) >= 2)
                .Select(row => InlineText(Assert.Single(row.Elements(Spreadsheet + "c"))))
                .ToArray();
            Assert.Equal(ExpectedCategories, categories);
            foreach (var row in list.Descendants(Spreadsheet + "row")
                         .Where(row => ParseUInt(row.Attribute("r")) >= 2))
            {
                Assert.Equal(
                    [$"A{ParseUInt(row.Attribute("r"))}"],
                    row.Elements(Spreadsheet + "c")
                        .Select(cell => (string)cell.Attribute("r")!)
                        .ToArray());
            }
        }

        private static string InlineText(XElement cell) =>
            cell.Element(Spreadsheet + "is")?.Element(Spreadsheet + "t")?.Value ?? string.Empty;

        private static uint ParseUInt(XAttribute? attribute) =>
            uint.Parse(attribute?.Value ?? throw new InvalidDataException("Required uint attribute is missing."), CultureInfo.InvariantCulture);

        private static string Canonical(XElement element) =>
            CanonicalElement(element).ToString(SaveOptions.DisableFormatting);

        private static XElement CanonicalElement(XElement element) =>
            new(
                element.Name,
                element.Attributes()
                    .Where(attribute => !attribute.IsNamespaceDeclaration)
                    .OrderBy(attribute => attribute.Name.NamespaceName, StringComparer.Ordinal)
                    .ThenBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
                    .Select(attribute => new XAttribute(attribute.Name, attribute.Value)),
                element.Nodes().Select(CloneCanonicalNode));

        private static XNode CloneCanonicalNode(XNode node) =>
            node switch
            {
                XElement child => CanonicalElement(child),
                XCData data => new XCData(data.Value),
                XText text => new XText(text.Value),
                XComment comment => new XComment(comment.Value),
                XProcessingInstruction instruction =>
                    new XProcessingInstruction(instruction.Target, instruction.Data),
                _ => throw new InvalidDataException(
                    $"Unsupported XML node type '{node.NodeType}' in package oracle.")
            };

        internal static byte[] MutateWorkbookPart(byte[] packageBytes)
        {
            using var stream = new MemoryStream();
            stream.Write(packageBytes);
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
            {
                var entry = archive.GetEntry("xl/workbook.xml")
                    ?? throw new InvalidDataException("Workbook package part is missing.");
                XDocument workbook;
                using (var input = entry.Open())
                {
                    workbook = XDocument.Load(input, LoadOptions.PreserveWhitespace);
                }
                entry.Delete();
                workbook.Root!.SetAttributeValue("unauthorizedDirectTemplateProbe", "changed");
                var replacement = archive.CreateEntry("xl/workbook.xml");
                using var output = replacement.Open();
                workbook.Save(output, SaveOptions.DisableFormatting);
            }

            return stream.ToArray();
        }

        private sealed class PackageSnapshot
        {
            private readonly IReadOnlyDictionary<string, byte[]> _parts;

            private PackageSnapshot(IReadOnlyDictionary<string, byte[]> parts)
            {
                _parts = parts;
                PartNames = parts.Keys.Order(StringComparer.Ordinal).ToArray();
            }

            internal IReadOnlyList<string> PartNames { get; }

            internal static PackageSnapshot Capture(byte[] bytes)
            {
                using var input = new MemoryStream(bytes, writable: false);
                using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
                var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
                {
                    using var part = entry.Open();
                    using var copy = new MemoryStream();
                    part.CopyTo(copy);
                    parts.Add(entry.FullName.Replace('\\', '/'), copy.ToArray());
                }

                return new PackageSnapshot(parts);
            }

            internal byte[] Hash(string name) => SHA256.HashData(_parts[name]);

            internal XDocument Xml(string name)
            {
                using var stream = new MemoryStream(_parts[name], writable: false);
                return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            }

            internal string ResolveWorksheetPart(string sheetName)
            {
                var workbook = Xml("xl/workbook.xml");
                var sheet = workbook.Descendants(Spreadsheet + "sheet")
                    .Single(item => string.Equals(
                        (string?)item.Attribute("name"),
                        sheetName,
                        StringComparison.Ordinal));
                var relationshipId = (string?)sheet.Attribute(OfficeRelationships + "id")
                    ?? throw new InvalidDataException($"Worksheet '{sheetName}' has no relationship id.");
                var relationships = Xml("xl/_rels/workbook.xml.rels");
                var target = (string?)relationships.Root!
                    .Elements(PackageRelationships + "Relationship")
                    .Single(item => string.Equals(
                        (string?)item.Attribute("Id"),
                        relationshipId,
                        StringComparison.Ordinal))
                    .Attribute("Target")
                    ?? throw new InvalidDataException($"Worksheet '{sheetName}' has no relationship target.");
                return Uri.UnescapeDataString(
                    new Uri(new Uri("https://package.invalid/xl/workbook.xml"), target)
                        .AbsolutePath
                        .TrimStart('/'));
            }
        }
    }
}
