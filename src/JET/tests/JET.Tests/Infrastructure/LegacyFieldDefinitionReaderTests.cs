using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Legacy TableDef 等價欄位定義的 reader-side evidence。
/// Oracle：master spec「Legacy 等價欄位定義持久化」——XLSX 依 native cell type
/// 與日期／時間 number format；CSV/TXT 固定為文字。這裡只鎖 reader 觀察值，
/// 跨列／Append 合併與持久化另由 repository seam 驗證。
/// </summary>
public sealed class LegacyFieldDefinitionReaderTests
{
    [Fact]
    public async Task XlsxNativeCells_ExposeTextNumberDateAndTimeObservations()
    {
        var path = new RawXlsxBuilder()
            .WithStyles(
                """
                <cellXfs count="3"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/><xf numFmtId="21" applyNumberFormat="1"/></cellXfs>
                """)
            .AddSheet("Data",
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>TextField</t></is></c><c r="B1" t="inlineStr"><is><t>NumberField</t></is></c><c r="C1" t="inlineStr"><is><t>DateField</t></is></c><c r="D1" t="inlineStr"><is><t>TimeField</t></is></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t> alpha </t></is></c><c r="B2"><v>12.340</v></c><c r="C2" s="1"><v>45292</v></c><c r="D2" s="2"><v>0.5</v></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var row = Assert.Single(await ReadXlsxRowsAsync(path));

            AssertObservation(row, "TextField", LegacyFieldKind.Text, textLength: 5, decimalPlaces: null);
            AssertObservation(row, "NumberField", LegacyFieldKind.Number, textLength: 5, decimalPlaces: 2);
            AssertObservation(row, "DateField", LegacyFieldKind.Date, textLength: 10, decimalPlaces: null);
            AssertObservation(row, "TimeField", LegacyFieldKind.Time, textLength: 8, decimalPlaces: null);

            Assert.Equal("alpha", row.Values["TextField"]);
            Assert.Equal("12.34", row.Values["NumberField"]);
            Assert.Equal("2024-01-01", row.Values["DateField"]);
            Assert.Equal("12:00:00", row.Values["TimeField"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task XlsxMixedNativeTypes_EmitPerCellEvidenceWithoutStringGuessing()
    {
        var path = new RawXlsxBuilder()
            .AddSheet("Data",
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>Mixed</t></is></c></row>
                <row r="2"><c r="A2"><v>1.25</v></c></row>
                <row r="3"><c r="A3" t="inlineStr"><is><t>1.250</t></is></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var rows = await ReadXlsxRowsAsync(path);

            Assert.Equal(2, rows.Count);
            AssertObservation(rows[0], "Mixed", LegacyFieldKind.Number, textLength: 4, decimalPlaces: 2);
            AssertObservation(rows[1], "Mixed", LegacyFieldKind.Text, textLength: 5, decimalPlaces: null);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task XlsxNamedEmptyColumn_EmitsNoFalseTypeEvidence()
    {
        var path = new RawXlsxBuilder()
            .AddSheet("Data",
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>NamedEmpty</t></is></c><c r="B1" t="inlineStr"><is><t>Present</t></is></c></row>
                <row r="2"><c r="B2" t="inlineStr"><is><t>x</t></is></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var reader = new OpenXmlSaxTableReader();
            var request = new TabularSourceRequest(path);
            var columns = await reader.ReadColumnsAsync(request, CancellationToken.None);
            var row = Assert.Single(await ReadRowsAsync(reader, request));

            Assert.Equal(["NamedEmpty", "Present"], columns);
            Assert.DoesNotContain(row.FieldObservations, observation => observation.FieldName == "NamedEmpty");
            AssertObservation(row, "Present", LegacyFieldKind.Text, textLength: 1, decimalPlaces: null);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task XlsxCachedFormulaResults_UseCachedNativeTypeAndNumberFormat()
    {
        var path = new RawXlsxBuilder()
            .WithStyles(
                """
                <cellXfs count="3"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/><xf numFmtId="21" applyNumberFormat="1"/></cellXfs>
                """)
            .AddSheet("Data",
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>FormulaText</t></is></c><c r="B1" t="inlineStr"><is><t>FormulaNumber</t></is></c><c r="C1" t="inlineStr"><is><t>FormulaDate</t></is></c><c r="D1" t="inlineStr"><is><t>FormulaTime</t></is></c><c r="E1" t="inlineStr"><is><t>NoCache</t></is></c></row>
                <row r="2"><c r="A2" t="str"><f>CONCAT("formula"," text")</f><v>formula text</v></c><c r="B2"><f>1+1.5</f><v>2.50</v></c><c r="C2" s="1"><f>DATE(2024,1,1)</f><v>45292</v></c><c r="D2" s="2"><f>TIME(12,0,0)</f><v>0.5</v></c><c r="E2"><f>1+1</f></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var row = Assert.Single(await ReadXlsxRowsAsync(path));

            AssertObservation(row, "FormulaText", LegacyFieldKind.Text, textLength: 12, decimalPlaces: null);
            AssertObservation(row, "FormulaNumber", LegacyFieldKind.Number, textLength: 3, decimalPlaces: 1);
            AssertObservation(row, "FormulaDate", LegacyFieldKind.Date, textLength: 10, decimalPlaces: null);
            AssertObservation(row, "FormulaTime", LegacyFieldKind.Time, textLength: 8, decimalPlaces: null);
            Assert.DoesNotContain(row.FieldObservations, observation => observation.FieldName == "NoCache");
            Assert.False(row.Values.ContainsKey("NoCache"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task XlsxDateTimeFormat_UsesExistingDatePrecedence()
    {
        var path = new RawXlsxBuilder()
            .WithStyles(
                """
                <cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="22" applyNumberFormat="1"/></cellXfs>
                """)
            .AddSheet("Data",
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>DateTimeField</t></is></c></row>
                <row r="2"><c r="A2" s="1"><v>45292.5</v></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var row = Assert.Single(await ReadXlsxRowsAsync(path));

            AssertObservation(row, "DateTimeField", LegacyFieldKind.Date, textLength: 10, decimalPlaces: null);
            Assert.Equal("2024-01-01", row.Values["DateTimeField"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task XlsxNativeIsoDateCell_UsesDeclaredDateType()
    {
        var path = new RawXlsxBuilder()
            .AddSheet("Data",
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>NativeDate</t></is></c></row>
                <row r="2"><c r="A2" t="d"><v>2024-06-30T15:45:00Z</v></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var row = Assert.Single(await ReadXlsxRowsAsync(path));

            AssertObservation(row, "NativeDate", LegacyFieldKind.Date, textLength: 10, decimalPlaces: null);
            Assert.Equal("2024-06-30", row.Values["NativeDate"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".csv")]
    [InlineData(".txt")]
    public async Task CsvAndTxtCells_AreAlwaysTextObservations(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-field-observation-{Guid.NewGuid():N}{extension}");
        await File.WriteAllTextAsync(path, "Value\n123.40\n2025-01-02\n");

        try
        {
            var reader = new CsvTableReader();
            var rows = await ReadRowsAsync(reader, new TabularSourceRequest(path));

            Assert.Equal(2, rows.Count);
            AssertObservation(rows[0], "Value", LegacyFieldKind.Text, textLength: 6, decimalPlaces: null);
            AssertObservation(rows[1], "Value", LegacyFieldKind.Text, textLength: 10, decimalPlaces: null);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Task<List<StagingRow>> ReadXlsxRowsAsync(string path) =>
        ReadRowsAsync(new OpenXmlSaxTableReader(), new TabularSourceRequest(path));

    private static async Task<List<StagingRow>> ReadRowsAsync(
        ITabularFileReader reader,
        TabularSourceRequest request)
    {
        var rows = new List<StagingRow>();
        await foreach (var row in reader.ReadRowsAsync(request, CancellationToken.None))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static void AssertObservation(
        StagingRow row,
        string fieldName,
        LegacyFieldKind kind,
        int textLength,
        int? decimalPlaces)
    {
        var observation = row.FieldObservations.Single(item => item.FieldName == fieldName);
        Assert.Equal(new TabularCellObservation(fieldName, kind, textLength, decimalPlaces), observation);
    }
}
