using System.Globalization;
using System.Text.Json;
using JET.Tests.Infrastructure;

namespace JET.Tests.Application;

/// <summary>
/// 測試內自含的小型 GL 工作簿 → 正式 file-based 管線（create → import → flag 模式 commit）。
/// 欄名→mapping key 為固定慣例（與 DemoDataFactory 同名詞彙）；
/// 「借方旗標」存在時明示借方為 "1"、貸方為 "0"。測試逐列宣告資料，無 mystery guest。
/// </summary>
internal sealed class InlineGlWorkbookBuilder
{
    private static readonly IReadOnlyDictionary<string, string> ColumnToKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["傳票號碼"] = "voucherNumber",
            ["傳票項次"] = "lineItem",
            ["傳票日期"] = "glDate",
            ["核准日期"] = "approvalDate",
            ["科目代號"] = "accountNumber",
            ["科目名稱"] = "accountName",
            ["摘要"] = "description",
            ["來源模組"] = "sourceModule",
            ["建立人員"] = "createdBy",
            ["核准人員"] = "approvedBy",
            ["人工傳票"] = "isManual",
            ["金額"] = "amount",
            ["借方旗標"] = "drCr"
        };

    private readonly List<object?[]> _rows = [];
    private string[] _columns = [];
    private bool _date1904;

    public InlineGlWorkbookBuilder WithColumns(params string[] columns)
    {
        _columns = columns;
        return this;
    }

    /// <summary>活頁簿改用 1904 日期系統，序列值 0 是 1904-01-01；DateTime 的儲存格照 1904 日期系統換成序列值。</summary>
    public InlineGlWorkbookBuilder UseDate1904()
    {
        _date1904 = true;
        return this;
    }

    public InlineGlWorkbookBuilder AddRow(params object?[] cells)
    {
        _rows.Add(cells);
        return this;
    }

    internal string WriteWorkbook()
    {
        var nonFiniteSerials = new List<(string Reference, double Value)>();
        var dateTypedCells = new List<(string Reference, string Text)>();
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            // 在寫入儲存格以前設定，DateTime 的儲存格才會照 1904 日期系統換成序列值。
            sheet.Workbook.Use1904DateSystem = _date1904;
            for (var c = 0; c < _columns.Length; c++)
            {
                sheet.Cell(1, c + 1).SetValue(_columns[c]);
            }

            for (var r = 0; r < _rows.Count; r++)
            {
                for (var c = 0; c < _rows[r].Length; c++)
                {
                    var cell = sheet.Cell(r + 2, c + 1);
                    switch (_rows[r][c])
                    {
                        case null:
                            break;
                        case bool boolean:
                            cell.SetValue(boolean);
                            break;
                        case int i:
                            cell.SetValue(i);
                            break;
                        case long l:
                            cell.SetValue(l);
                            break;
                        case decimal d:
                            cell.SetValue(d);
                            break;
                        case double dbl:
                            if (double.IsFinite(dbl))
                            {
                                cell.SetValue(dbl);
                            }
                            else
                            {
                                // ClosedXML 不接受 NaN 與無限大，先寫 0，存檔後再換成原本的值；不套日期格式，仍是一般的數值儲存格。
                                cell.SetValue(0);
                                nonFiniteSerials.Add((cell.Address.ToStringRelative(), dbl));
                            }

                            break;
                        case DateTime dateTime:
                            cell.SetValue(dateTime);
                            break;
                        case TimeSpan timeSpan:
                            cell.SetValue(timeSpan);
                            break;
                        case ExcelDateSerial dateSerial:
                            if (double.IsFinite(dateSerial.Value))
                            {
                                cell.SetValue(dateSerial.Value);
                            }
                            else
                            {
                                // ClosedXML 不接受 NaN 與無限大，先寫 0，存檔後再換成原本的值。
                                cell.SetValue(0);
                                nonFiniteSerials.Add((cell.Address.ToStringRelative(), dateSerial.Value));
                            }

                            cell.Style.NumberFormat.NumberFormatId = 14;
                            break;
                        case DateTypedCell dateTyped:
                            // ClosedXML 寫不出 t="d" 的儲存格，先寫文字，存檔後再改成 t="d" 與原本的文字。
                            cell.SetValue(dateTyped.Text);
                            dateTypedCells.Add((cell.Address.ToStringRelative(), dateTyped.Text));
                            break;
                        default:
                            cell.SetValue(_rows[r][c]!.ToString());
                            break;
                    }
                }
            }
        });
        if (nonFiniteSerials.Count > 0)
        {
            WriteNonFiniteSerials(path, nonFiniteSerials);
        }

        if (dateTypedCells.Count > 0)
        {
            WriteDateTypedCells(path, dateTypedCells);
        }

        return path;
    }

    // 其他程式產生的 xlsx 可能用 t="d" 的儲存格寫日期的文字，這裡用 Open XML SDK 照這種寫法改寫存好的檔案。
    private static void WriteDateTypedCells(string path, IReadOnlyList<(string Reference, string Text)> dateTypedCells)
    {
        using var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(path, true);
        var worksheet = document.WorkbookPart!.WorksheetParts.Single().Worksheet!;
        var cells = worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>()
            .ToDictionary(cell => cell.CellReference!.Value!, StringComparer.Ordinal);
        foreach (var (reference, text) in dateTypedCells)
        {
            var cell = cells[reference];
            cell.InlineString = null;
            cell.DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.Date;
            cell.CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue(text);
        }

        worksheet.Save();
    }

    // 其他程式產生的 xlsx 可能在數值儲存格寫 NaN、Infinity 或 -Infinity，這裡用 Open XML SDK 照這種寫法改寫存好的檔案。
    private static void WriteNonFiniteSerials(string path, IReadOnlyList<(string Reference, double Value)> serials)
    {
        using var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(path, true);
        var worksheet = document.WorkbookPart!.WorksheetParts.Single().Worksheet!;
        var cells = worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>()
            .ToDictionary(cell => cell.CellReference!.Value!, StringComparer.Ordinal);
        foreach (var (reference, value) in serials)
        {
            cells[reference].CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue(
                value.ToString(CultureInfo.InvariantCulture));
        }

        worksheet.Save();
    }

    internal Dictionary<string, string> BuildFlagModeMapping()
    {
        var mapping = _columns
            .Where(ColumnToKey.ContainsKey)
            .ToDictionary(c => ColumnToKey[c], c => c, StringComparer.Ordinal);

        if (mapping.ContainsKey("drCr"))
        {
            mapping["drCrDebitCode"] = "1";
            // Both debit and credit codes are required.
            // This synthetic fixture explicitly uses 1/0, so it does not rely on any product fallback.
            mapping["drCrCreditCode"] = "0";
        }

        return mapping;
    }

    internal void PopulateMatchingTb(
        InlineTbWorkbookBuilder tb,
        string periodStart,
        string periodEnd)
    {
        var glDateIndex = Array.IndexOf(_columns, "傳票日期");
        var accountNumberIndex = Array.IndexOf(_columns, "科目代號");
        var accountNameIndex = Array.IndexOf(_columns, "科目名稱");
        var amountIndex = Array.IndexOf(_columns, "金額");
        var debitFlagIndex = Array.IndexOf(_columns, "借方旗標");
        if (glDateIndex < 0
            || accountNumberIndex < 0
            || amountIndex < 0
            || debitFlagIndex < 0)
        {
            throw new InvalidOperationException(
                "自動建立 matching TB 需要傳票日期、科目代號、金額與借方旗標欄位。");
        }

        var start = DateOnly.ParseExact(periodStart, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(periodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var totals = new Dictionary<string, (string? Name, decimal Total)>(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            var glDate = ReadDate(row.ElementAtOrDefault(glDateIndex));
            var accountNumber = Convert.ToString(
                row.ElementAtOrDefault(accountNumberIndex),
                CultureInfo.InvariantCulture);
            if (glDate is null
                || glDate < start
                || glDate > end
                || string.IsNullOrWhiteSpace(accountNumber))
            {
                continue;
            }

            var accountName = accountNameIndex < 0
                ? null
                : Convert.ToString(
                    row.ElementAtOrDefault(accountNameIndex),
                    CultureInfo.InvariantCulture);
            var amount = ReadDecimal(row.ElementAtOrDefault(amountIndex));
            var debitFlag = Convert.ToString(
                row.ElementAtOrDefault(debitFlagIndex),
                CultureInfo.InvariantCulture);
            var signed = string.Equals(debitFlag, "1", StringComparison.Ordinal)
                ? Math.Abs(amount)
                : -Math.Abs(amount);

            totals.TryGetValue(accountNumber, out var current);
            totals[accountNumber] = (current.Name ?? accountName, current.Total + signed);
        }

        if (totals.Count == 0)
        {
            tb.AddRow("__EMPTY__", "空母體占位", 0m);
            return;
        }

        foreach (var (accountNumber, total) in totals.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            tb.AddRow(accountNumber, total.Name, total.Total);
        }
    }

    private static DateOnly? ReadDate(object? value) => value switch
    {
        DateOnly date => date,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        string text when DateOnly.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed) => parsed,
        _ => null
    };

    private static decimal ReadDecimal(object? value)
    {
        if (value is null)
        {
            return 0m;
        }

        if (value is IConvertible convertible)
        {
            try
            {
                return convertible.ToDecimal(CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
            }
        }

        throw new InvalidOperationException($"無法把 matching TB 金額 '{value}' 解析為 decimal。");
    }
}

/// <summary>
/// 測試內自含的小型 TB 工作簿（科目代號／科目名稱／變動金額），以 direct 變動模式 commit。
/// 與 GL 並行宣告，供完整性測試（存入筆數與合計核對、Not-in-TB）造對齊或刻意不齊的小母體。
/// 欄名固定:科目代號→accountNumber、科目名稱→accountName、變動金額→amount（單一帶號變動欄）。
/// </summary>
internal sealed class InlineTbWorkbookBuilder
{
    private const string AccCodeColumn = "科目代號";
    private const string AccountNameColumn = "科目名稱";
    private const string AmountColumn = "變動金額";

    private readonly List<(string? Code, string? Name, object Amount)> _rows = [];

    public InlineTbWorkbookBuilder AddRow(string? accountNumber, string? accountName, object changeAmount)
    {
        _rows.Add((accountNumber, accountName, changeAmount));
        return this;
    }

    internal bool HasRows => _rows.Count > 0;

    internal string WriteWorkbook()
    {
        return TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).SetValue(AccCodeColumn);
            sheet.Cell(1, 2).SetValue(AccountNameColumn);
            sheet.Cell(1, 3).SetValue(AmountColumn);

            for (var r = 0; r < _rows.Count; r++)
            {
                var (code, name, amount) = _rows[r];
                if (code is not null) { sheet.Cell(r + 2, 1).SetValue(code); }
                if (name is not null) { sheet.Cell(r + 2, 2).SetValue(name); }
                switch (amount)
                {
                    case int i: sheet.Cell(r + 2, 3).SetValue(i); break;
                    case long l: sheet.Cell(r + 2, 3).SetValue(l); break;
                    case decimal d: sheet.Cell(r + 2, 3).SetValue(d); break;
                    case double dbl: sheet.Cell(r + 2, 3).SetValue(dbl); break;
                    default: sheet.Cell(r + 2, 3).SetValue(amount.ToString()); break;
                }
            }
        });
    }

    internal static Dictionary<string, string> BuildDirectModeMapping() =>
        new(StringComparer.Ordinal)
        {
            ["accountNumber"] = AccCodeColumn,
            ["accountName"] = AccountNameColumn,
            ["changeAmount"] = AmountColumn
        };
}

internal static class InlineWorkbookProject
{
    public static async Task<string> SetupAsync(
        HandlerTestHost host,
        Action<InlineGlWorkbookBuilder> configure,
        string? prepStartDate = null,
        IReadOnlyList<string>? holidays = null,
        IReadOnlyList<string>? makeupDays = null,
        string databaseProvider = "sqlite",
        Action<InlineTbWorkbookBuilder>? configureTb = null,
        string periodStart = "2025-01-01",
        string periodEnd = "2025-12-31",
        bool validateForDownstream = false, string manualBlankValueKind = "reject")
    {
        var builder = new InlineGlWorkbookBuilder();
        configure(builder);

        var tbBuilder = new InlineTbWorkbookBuilder();
        configureTb?.Invoke(tbBuilder);
        if (validateForDownstream && configureTb is null)
        {
            builder.PopulateMatchingTb(tbBuilder, periodStart, periodEnd);
        }

        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "INLINE-2025-001",
            clientName = "測試自含案件",
            operatorId = "tester",
            periodStart,
            periodEnd,
            prepStartDate,
            databaseProvider
        }));
        var projectId = created.GetProperty("projectId").GetString()!;

        var filePath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                filePath,
                fileName = "inline-gl.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(filePath);
        }

        if (tbBuilder.HasRows)
        {
            var tbPath = tbBuilder.WriteWorkbook();
            try
            {
                await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new
                {
                    filePath = tbPath,
                    fileName = "inline-tb.xlsx"
                }));
            }
            finally
            {
                TestWorkbookBuilder.Delete(tbPath);
            }
        }

        if (holidays is { Count: > 0 })
        {
            await host.DispatchAsync("import.holiday", JsonSerializer.Serialize(new { dates = holidays }));
        }

        if (makeupDays is { Count: > 0 })
        {
            await host.DispatchAsync("import.makeupDay", JsonSerializer.Serialize(new { dates = makeupDays }));
        }

        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = builder.BuildFlagModeMapping(),
            amountMode = "flag",
            manualAutoPolicy = new
            {
                manualValues = new[] { "true", "1" },
                automaticValues = new[] { "false", "0" }, blankValueKind = manualBlankValueKind
            }
        }));

        if (tbBuilder.HasRows)
        {
            await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
            {
                mapping = InlineTbWorkbookBuilder.BuildDirectModeMapping(),
                changeMode = "direct"
            }));
        }

        if (validateForDownstream)
        {
            var validation = await host.DispatchAsync("validate.run");
            var eligibility = validation
                .GetProperty("completenessTest")
                .GetProperty("eligibility");
            if (!eligibility.GetProperty("isEligible").GetBoolean())
            {
                throw new InvalidOperationException(
                    eligibility.GetProperty("reason").GetString()
                    ?? "自含測試母體未通過完整性驗證。");
            }
        }

        return projectId;
    }
}

/// <summary>
/// 寫成 Excel 日期儲存格的序列值，數字格式是內建的日期格式 14；讓測試指定 Excel 實際存的數字，例如 0 與 60，
/// 也可以是其他程式寫進檔案的 NaN 或無限大。
/// </summary>
internal sealed record ExcelDateSerial(double Value);

/// <summary>
/// 寫成儲存格型別 t="d" 的儲存格，值照 Text 的原文寫；其他程式產生的 xlsx 用這種寫法存日期的文字，
/// 也可能寫進不是日期的內容。
/// </summary>
internal sealed record DateTypedCell(string Text);
