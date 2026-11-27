using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 有公式、卻沒有存計算結果的儲存格（有 f 沒有 v）。讀取器不重算公式，這些格子讀成空白；
/// 逐列讀取時記下格數與第一格，交給匯入結果提醒使用者。活頁簿用 RawXlsxBuilder 手刻，值都是合成的。
/// </summary>
public sealed class OpenXmlSaxTableReaderFormulaTests
{
    private const string SheetName = "合成總帳";

    [Fact]
    public async Task ReadRows_FormulaWithoutResult_CountsCellsAndKeepsThemBlank()
    {
        var path = new RawXlsxBuilder()
            .AddSheet(SheetName,
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>doc</t></is></c><c r="B1" t="inlineStr"><is><t>amount</t></is></c><c r="C1" t="inlineStr"><is><t>memo</t></is></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>V1</t></is></c><c r="B2"><f>10*2</f></c><c r="C2" t="str"><f>""</f><v></v></c></row>
                <row r="3"><c r="A3"><f>1+1</f></c><c r="B3" t="str"><f>"x"</f></c></row>
                <row r="4"><c r="A4" t="inlineStr"><is><t>V4</t></is></c><c r="B4"><f>2*3</f><v>6</v></c></row>
                </sheetData>
                """)
            .Save();
        try
        {
            var tally = new FormulaWithoutResultTally();
            var rows = await ReadAllRowsAsync(new TabularSourceRequest(path) { FormulasWithoutResult = tally });

            Assert.Equal([2, 4], rows.Select(row => row.SourceRowNumber));
            Assert.Equal("V1", rows[0].Values["doc"]);
            Assert.False(rows[0].Values.ContainsKey("amount"));
            Assert.False(rows[0].Values.ContainsKey("memo"));
            Assert.Equal("6", rows[1].Values["amount"]);
            Assert.Equal(3, tally.Count);
            Assert.Equal(SheetName, tally.SheetName);
            Assert.Equal("B2", tally.FirstCell);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadColumnsThenRows_FormulaInHeaderRow_CountedOnce()
    {
        var path = new RawXlsxBuilder()
            .AddSheet(SheetName,
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>doc</t></is></c><c r="B1" t="str"><f>"amount"</f></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>V1</t></is></c><c r="B2"><v>5</v></c></row>
                </sheetData>
                """)
            .Save();
        try
        {
            var tally = new FormulaWithoutResultTally();
            var request = new TabularSourceRequest(path) { FormulasWithoutResult = tally };

            Assert.Equal(["doc"], await new OpenXmlSaxTableReader().ReadColumnsAsync(request, CancellationToken.None));
            Assert.Equal(0, tally.Count);

            await ReadAllRowsAsync(request);

            Assert.Equal(1, tally.Count);
            Assert.Equal("B1", tally.FirstCell);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadRows_FormulaInSkippedLeadingRows_NotCounted()
    {
        var path = new RawXlsxBuilder()
            .AddSheet(SheetName,
                """
                <sheetData>
                <row r="1"><c r="A1" t="str"><f>"合成標題"</f></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>doc</t></is></c><c r="B2" t="inlineStr"><is><t>amount</t></is></c></row>
                <row r="3"><c r="A3" t="inlineStr"><is><t>V1</t></is></c><c r="B3"><f>A1</f></c></row>
                </sheetData>
                """)
            .Save();
        try
        {
            var tally = new FormulaWithoutResultTally();
            await ReadAllRowsAsync(new TabularSourceRequest(path, LeadingRowsToSkip: 1) { FormulasWithoutResult = tally });

            Assert.Equal(1, tally.Count);
            Assert.Equal("B3", tally.FirstCell);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<List<StagingRow>> ReadAllRowsAsync(TabularSourceRequest request)
    {
        var rows = new List<StagingRow>();
        await foreach (var row in new OpenXmlSaxTableReader().ReadRowsAsync(request, CancellationToken.None))
        {
            rows.Add(row);
        }

        return rows;
    }
}
