using System.Text;
using System.Text.Json;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9AmountModeWorkflowTests
{
    [Theory]
    [InlineData("sqlite", "signed")]
    [InlineData("duckdb", "signed")]
    [InlineData("sqlite", "dual")]
    [InlineData("duckdb", "dual")]
    [InlineData("sqlite", "rde")]
    [InlineData("duckdb", "rde")]
    [InlineData("sqlite", "csv")]
    [InlineData("duckdb", "csv")]
    public async Task AccountingParentheses_ReachGlOrRdeWithIdenticalFixedAnswers(string provider, string mode)
    {
        using var fixture = await Batch9MappingTestFixture.CreateAsync(provider);
        var path = mode == "csv" ? WriteCsv() : TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            string[] header = ["Document", "Date", "Account", "Name", "Description", "Amount", "Debit", "Credit", "RdeAmount"];
            for (var column = 0; column < header.Length; column++) sheet.Cell(1, column + 1).Value = header[column];
            for (var row = 2; row <= 3; row++)
            {
                var raw = row == 2 ? "(1,234.50)" : "1,234.50";
                string[] values = [$"V{row - 2:000}", "2025-03-05", "A", "Synthetic", "Normal", mode == "rde" ? "1" : raw, raw, "0", raw];
                for (var column = 0; column < values.Length; column++) sheet.Cell(row, column + 1).Value = values[column];
            }
        });
        try
        {
            await fixture.Host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var mapping = Batch9MappingTestFixture.Mapping();
            if (mode == "dual")
            {
                mapping.Remove("amount"); mapping["debitAmount"] = "Debit"; mapping["creditAmount"] = "Credit";
            }
            object[] rde = mode == "rde" ? [new { sourceColumn = "RdeAmount", label = "Synthetic amount", valueType = "money" }] : [];
            await fixture.CommitAsync(mapping, mode == "dual" ? "dual" : "signed", rde);
            var amount = mode == "rde" ? "v.amount_scaled" : "g.amount_scaled";
            var join = mode == "rde" ? " JOIN target_gl_rde_value v ON v.entry_id = g.entry_id" : "";
            Assert.Equal(-12_345_000, await fixture.ScalarAsync($"SELECT {amount} FROM target_gl_entry g{join} WHERE g.document_number = 'V000';"));
            Assert.Equal(12_345_000, await fixture.ScalarAsync($"SELECT {amount} FROM target_gl_entry g{join} WHERE g.document_number = 'V001';"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("sqlite", "direct", -12_345_000L)]
    [InlineData("duckdb", "direct", -12_345_000L)]
    [InlineData("sqlite", "debitCredit", -13_347_500L)]
    [InlineData("duckdb", "debitCredit", -13_347_500L)]
    [InlineData("sqlite", "openClose", 13_347_500L)]
    [InlineData("duckdb", "openClose", 13_347_500L)]
    [InlineData("sqlite", "openCloseBySide", 13_347_500L)]
    [InlineData("duckdb", "openCloseBySide", 13_347_500L)]
    public async Task TbFourModes_UseSameParenthesesParserAndFixedScaledResult(string provider, string changeMode, long expected)
    {
        using var fixture = await Batch9MappingTestFixture.CreateAsync(provider);
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            string[] headers = ["Account", "Name", "negative", "positive", "zero"];
            string[] values = ["A", "Synthetic", "(1,234.50)", "100.25", "0"];
            for (var column = 0; column < headers.Length; column++)
            { sheet.Cell(1, column + 1).Value = headers[column]; sheet.Cell(2, column + 1).Value = values[column]; }
        });
        try
        {
            await fixture.Host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var mapping = new Dictionary<string, string>
            {
                ["accNum"] = "Account", ["accName"] = "Name", ["amount"] = "negative",
                ["debitAmt"] = "negative", ["creditAmt"] = "positive", ["openingBalance"] = "negative",
                ["closingBalance"] = "positive", ["openingDebit"] = "negative", ["openingCredit"] = "zero",
                ["closingDebit"] = "positive", ["closingCredit"] = "zero"
            };
            await fixture.Host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new { mapping, changeMode }));
            Assert.Equal(expected, await fixture.ScalarAsync("SELECT change_amount_scaled FROM target_tb_balance;"));
        }
        finally { File.Delete(path); }
    }

    private static string WriteCsv()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-batch9-amount-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "Document,Date,Account,Name,Description,Amount\n"
            + "V000,2025-03-05,A,Synthetic,Normal,\"(1,234.50)\"\n"
            + "V001,2025-03-05,A,Synthetic,Normal,\"1,234.50\"\n", new UTF8Encoding(false));
        return path;
    }
}
