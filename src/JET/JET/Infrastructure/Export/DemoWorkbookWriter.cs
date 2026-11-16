using ClosedXML.Excel;
using JET.Application;

namespace JET.Infrastructure;

/// <summary>
/// 把示範案件的資料寫成 xlsx 檔，供內部測試使用。用無參數的建構子時，同一個程序裡每種檔案只寫一次，
/// 寫在系統暫存資料夾，之後都回傳同一份；用 internal 建構子時，把四份內容固定的檔案寫到指定的資料夾。
/// 檔名固定，取自示範資料。科目代號這類代碼欄寫成文字儲存格，日期寫成 DateTime，旗標寫成 1 或 0，
/// 用來確認匯入時讀檔程式的型別正規化。
/// </summary>
public sealed class DemoWorkbookWriter : IDemoFileWriter
{
    private static readonly Lazy<DemoExportedFile> GlFile =
        new(() => WriteGlCore(DemoDataFactory.Create(), outputDirectory: null));
    private static readonly Lazy<DemoExportedFile> TbFile =
        new(() => WriteTbCore(DemoDataFactory.Create(), outputDirectory: null));
    private static readonly Lazy<DemoExportedFile> AccountMappingFile =
        new(() => WriteAccountMappingCore(DemoDataFactory.Create(), outputDirectory: null));
    private static readonly Lazy<DemoExportedFile> AuthorizedPreparerFile =
        new(() => WriteAuthorizedPreparerCore(DemoDataFactory.Create(), outputDirectory: null));

    private readonly Lazy<DemoExportedFile>? runOwnedGlFile;
    private readonly Lazy<DemoExportedFile>? runOwnedTbFile;
    private readonly Lazy<DemoExportedFile>? runOwnedAccountMappingFile;
    private readonly Lazy<DemoExportedFile>? runOwnedAuthorizedPreparerFile;

    public DemoWorkbookWriter()
    {
    }

    internal DemoWorkbookWriter(string outputRoot, DemoProjectData? fixtureData = null)
    {
        var outputDirectory = Path.GetFullPath(outputRoot);
        runOwnedGlFile = new(() => WriteGlCore(fixtureData ?? DemoDataFactory.Create(), outputDirectory));
        runOwnedTbFile = new(() => WriteTbCore(fixtureData ?? DemoDataFactory.Create(), outputDirectory));
        runOwnedAccountMappingFile =
            new(() => WriteAccountMappingCore(fixtureData ?? DemoDataFactory.Create(), outputDirectory));
        runOwnedAuthorizedPreparerFile =
            new(() => WriteAuthorizedPreparerCore(fixtureData ?? DemoDataFactory.Create(), outputDirectory));
    }

    public Task<DemoExportedFile> WriteGlAsync(DemoProjectData data, CancellationToken cancellationToken)
        => Task.FromResult(runOwnedGlFile?.Value ?? GlFile.Value);

    public Task<DemoExportedFile> WriteTbAsync(DemoProjectData data, CancellationToken cancellationToken)
        => Task.FromResult(runOwnedTbFile?.Value ?? TbFile.Value);

    public Task<DemoExportedFile> WriteAccountMappingAsync(DemoProjectData data, CancellationToken cancellationToken)
        => Task.FromResult(runOwnedAccountMappingFile?.Value ?? AccountMappingFile.Value);

    public Task<DemoExportedFile> WriteAuthorizedPreparerAsync(DemoProjectData data, CancellationToken cancellationToken)
        => Task.FromResult(runOwnedAuthorizedPreparerFile?.Value ?? AuthorizedPreparerFile.Value);

    private static DemoExportedFile WriteGlCore(DemoProjectData data, string? outputDirectory)
    {
        var filePath = GetExportPath(data.GlFileName, outputDirectory);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("GL");
        WriteHeader(worksheet, data.GlColumns);

        var rowNumber = 1;
        foreach (var row in data.GlRows)
        {
            rowNumber++;
            worksheet.Cell(rowNumber, 1).Value = row.GlDate.ToDateTime(TimeOnly.MinValue);
            worksheet.Cell(rowNumber, 2).Value = row.VoucherNumber;
            worksheet.Cell(rowNumber, 3).Value = row.LineNumber;
            worksheet.Cell(rowNumber, 4).Value = row.AccountNumber;
            worksheet.Cell(rowNumber, 5).Value = row.AccountName;
            worksheet.Cell(rowNumber, 6).Value = row.Description;
            worksheet.Cell(rowNumber, 7).Value = row.Amount;
            worksheet.Cell(rowNumber, 8).Value = row.IsDebit ? 1 : 0;
            worksheet.Cell(rowNumber, 9).Value = row.CreatedBy;
            worksheet.Cell(rowNumber, 10).Value = row.ApprovalDate.ToDateTime(TimeOnly.MinValue);
            worksheet.Cell(rowNumber, 11).Value = row.IsManual ? 1 : 0;
        }

        workbook.SaveAs(filePath);
        return new DemoExportedFile(filePath, data.GlFileName);
    }

    private static DemoExportedFile WriteTbCore(DemoProjectData data, string? outputDirectory)
    {
        var filePath = GetExportPath(data.TbFileName, outputDirectory);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("TB");
        WriteHeader(worksheet, data.TbColumns);

        var rowNumber = 1;
        foreach (var row in data.TbRows)
        {
            rowNumber++;
            worksheet.Cell(rowNumber, 1).Value = row.AccountNumber;
            worksheet.Cell(rowNumber, 2).Value = row.AccountName;
            worksheet.Cell(rowNumber, 3).Value = row.OpeningBalance;
            worksheet.Cell(rowNumber, 4).Value = row.DebitTotal;
            worksheet.Cell(rowNumber, 5).Value = row.CreditTotal;
            worksheet.Cell(rowNumber, 6).Value = row.ClosingBalance;
        }

        workbook.SaveAs(filePath);
        return new DemoExportedFile(filePath, data.TbFileName);
    }

    private static DemoExportedFile WriteAccountMappingCore(
        DemoProjectData data,
        string? outputDirectory)
    {
        var filePath = GetExportPath(data.AccountMappingFileName, outputDirectory);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("AccountMapping");
        WriteHeader(worksheet, data.AccountMappingColumns);

        var rowNumber = 1;
        foreach (var row in data.AccountMappingRows)
        {
            rowNumber++;
            worksheet.Cell(rowNumber, 1).Value = row.AccountNumber;
            worksheet.Cell(rowNumber, 2).Value = row.AccountName;
            worksheet.Cell(rowNumber, 3).Value = row.Category;
        }

        workbook.SaveAs(filePath);
        return new DemoExportedFile(filePath, data.AccountMappingFileName);
    }

    private static DemoExportedFile WriteAuthorizedPreparerCore(
        DemoProjectData data,
        string? outputDirectory)
    {
        var filePath = GetExportPath(data.AuthorizedPreparerFileName, outputDirectory);

        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("AuthorizedPreparer");
        WriteHeader(worksheet, data.AuthorizedPreparerColumns);

        var rowNumber = 1;
        foreach (var name in data.AuthorizedPreparers)
        {
            rowNumber++;
            worksheet.Cell(rowNumber, 1).Value = name;
        }

        workbook.SaveAs(filePath);
        return new DemoExportedFile(filePath, data.AuthorizedPreparerFileName);
    }

    private static void WriteHeader(IXLWorksheet worksheet, IReadOnlyList<string> columns)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            worksheet.Cell(1, i + 1).Value = columns[i];
        }
    }

    private static string GetExportPath(string fileName, string? outputDirectory)
    {
        var directory = outputDirectory ?? Path.Combine(
            Path.GetTempPath(),
            "jet-demo",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, fileName);
    }
}
