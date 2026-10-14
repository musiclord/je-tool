using ClosedXML.Excel;

namespace Jet.Harness.TaiwanSme;

/// <summary>寫出審計員會收到的 PBC 檔案。檔案放在主機資料夾，不進 Git，也不含任何真實資料。</summary>
internal sealed record SmeSourceFiles(
    SmeCase Case,
    IReadOnlyList<(string FilePath, string FileName)> GlFiles,
    string TbFile,
    string AccountMappingFile,
    string AuthorizedPreparerFile);

internal static class SmeWorkbookWriter
{
    public static readonly string[] GlColumns =
    [
        "傳票日期", "傳票編號", "分錄序號", "會計科目", "科目名稱", "摘要", "借方金額", "貸方金額",
        "部門", "傳票來源", "製單人員", "審核人員", "審核日期", "人工傳票", "過帳狀態",
    ];

    public static readonly string[] TbColumns =
    [
        "科目代號", "科目名稱", "期初借方餘額", "期初貸方餘額", "本期借方發生額", "本期貸方發生額", "期末借方餘額", "期末貸方餘額",
    ];

    public const string PreparerNameColumn = "姓名";

    public static SmeSourceFiles Write(SmeCase smeCase, string directory)
    {
        Directory.CreateDirectory(directory);
        var split = new DateOnly(2025, 7, 1);
        var firstHalf = Path.Combine(directory, "合成精密-總帳明細-114上半年.xlsx");
        var secondHalf = Path.Combine(directory, "合成精密-總帳明細-114下半年.xlsx");
        WriteGl(firstHalf, smeCase.Lines.Where(line => line.PostDate < split));
        WriteGl(secondHalf, smeCase.Lines.Where(line => line.PostDate >= split));

        var tb = Path.Combine(directory, "合成精密-試算表-114年度.xlsx");
        WriteTb(tb, smeCase);
        var mapping = Path.Combine(directory, "合成精密-科目配對.xlsx");
        WriteAccountMapping(mapping, smeCase.Accounts);
        var preparers = Path.Combine(directory, "合成精密-ERP使用者權限清單.xlsx");
        WriteAuthorizedPreparers(preparers);
        return new SmeSourceFiles(
            smeCase,
            [(firstHalf, Path.GetFileName(firstHalf)), (secondHalf, Path.GetFileName(secondHalf))],
            tb,
            mapping,
            preparers);
    }

    private static void WriteGl(string path, IEnumerable<SmeLine> lines)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("傳票明細");
        WriteHeader(sheet, GlColumns);
        var row = 1;
        foreach (var line in lines
                     .OrderBy(line => line.PostDate)
                     .ThenBy(line => line.VoucherNo, StringComparer.Ordinal)
                     .ThenBy(line => line.LineNo))
        {
            row++;
            sheet.Cell(row, 1).Value = line.PostDate.ToDateTime(TimeOnly.MinValue);
            sheet.Cell(row, 2).Value = line.VoucherNo;
            sheet.Cell(row, 3).Value = line.LineNo;
            sheet.Cell(row, 4).Value = line.AccountCode;
            sheet.Cell(row, 5).Value = line.AccountName;
            sheet.Cell(row, 6).Value = line.Description;
            sheet.Cell(row, 7).Value = line.Debit;
            sheet.Cell(row, 8).Value = line.Credit;
            sheet.Cell(row, 9).Value = line.Department;
            sheet.Cell(row, 10).Value = line.Source;
            sheet.Cell(row, 11).Value = line.CreatedBy;
            sheet.Cell(row, 12).Value = line.ApprovedBy;
            if (line.ApprovalDate is { } approval)
            {
                sheet.Cell(row, 13).Value = approval.ToDateTime(TimeOnly.MinValue);
            }

            sheet.Cell(row, 14).Value = line.IsManual ? "Y" : "N";
            sheet.Cell(row, 15).Value = line.IsPosted ? "已過帳" : "未過帳";
        }

        sheet.Column(1).Style.DateFormat.Format = "yyyy/mm/dd";
        sheet.Column(13).Style.DateFormat.Format = "yyyy/mm/dd";
        sheet.Columns(7, 8).Style.NumberFormat.Format = "#,##0";
        workbook.SaveAs(path);
    }

    private static void WriteTb(string path, SmeCase smeCase)
    {
        var debit = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var credit = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var line in smeCase.Population)
        {
            debit[line.AccountCode] = debit.GetValueOrDefault(line.AccountCode) + line.Debit;
            credit[line.AccountCode] = credit.GetValueOrDefault(line.AccountCode) + line.Credit;
        }

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("試算表");
        WriteHeader(sheet, TbColumns);
        var row = 1;
        foreach (var account in smeCase.Accounts.OrderBy(account => account.Code, StringComparer.Ordinal))
        {
            row++;
            var periodDebit = debit.GetValueOrDefault(account.Code);
            var periodCredit = credit.GetValueOrDefault(account.Code);
            var closing = account.Opening + periodDebit - periodCredit;
            sheet.Cell(row, 1).Value = account.Code;
            sheet.Cell(row, 2).Value = account.Name;
            sheet.Cell(row, 3).Value = Math.Max(account.Opening, 0m);
            sheet.Cell(row, 4).Value = Math.Max(-account.Opening, 0m);
            sheet.Cell(row, 5).Value = periodDebit;
            sheet.Cell(row, 6).Value = periodCredit;
            sheet.Cell(row, 7).Value = Math.Max(closing, 0m);
            sheet.Cell(row, 8).Value = Math.Max(-closing, 0m);
        }

        sheet.Columns(3, 8).Style.NumberFormat.Format = "#,##0";
        workbook.SaveAs(path);
    }

    private static void WriteAccountMapping(string path, IReadOnlyList<SmeAccount> accounts)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("AccountMapping");
        WriteHeader(sheet, ["GL_Number", "GL_Name", "Standardized Account Name*"]);
        var row = 1;
        foreach (var account in accounts.Append(SmeChart.ClosedBankAccount))
        {
            row++;
            sheet.Cell(row, 1).Value = account.Code;
            sheet.Cell(row, 2).Value = account.Name;
            sheet.Cell(row, 3).Value = account.Category;
        }

        workbook.SaveAs(path);
    }

    /// <summary>ERP 使用者權限清單：主辦會計因兼任系統管理員列了兩次，另有一列職缺沒有姓名。</summary>
    private static void WriteAuthorizedPreparers(string path)
    {
        (string Id, string Name, string Title, string Right)[] rows =
        [
            ("E001", SmePeople.ChiefAccountant, "主辦會計", "傳票核准"),
            ("E002", SmePeople.ExpenseClerk, "會計", "傳票建立"),
            ("E003", SmePeople.PayablesClerk, "會計", "傳票建立"),
            ("E004", SmePeople.ReceivablesClerk, "會計", "傳票建立"),
            ("E005", SmePeople.Cashier, "出納", "傳票建立"),
            ("E006", SmePeople.SalesAssistant, "業務助理", "銷貨單"),
            ("E007", SmePeople.Purchaser, "採購", "進貨單"),
            ("E008", SmePeople.ProductionClerk, "生管", "工單"),
            ("E009", SmePeople.FinanceManager, "財務經理", "傳票核准"),
            ("E001", SmePeople.ChiefAccountant, "系統管理員", "系統設定"),
            ("E010", string.Empty, "會計（職缺）", "傳票建立"),
        ];
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("使用者權限");
        WriteHeader(sheet, ["員工編號", PreparerNameColumn, "職稱", "ERP 權限"]);
        for (var i = 0; i < rows.Length; i++)
        {
            sheet.Cell(i + 2, 1).Value = rows[i].Id;
            sheet.Cell(i + 2, 2).Value = rows[i].Name;
            sheet.Cell(i + 2, 3).Value = rows[i].Title;
            sheet.Cell(i + 2, 4).Value = rows[i].Right;
        }

        workbook.SaveAs(path);
    }

    private static void WriteHeader(IXLWorksheet sheet, IReadOnlyList<string> columns)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            sheet.Cell(1, i + 1).Value = columns[i];
        }
    }
}
