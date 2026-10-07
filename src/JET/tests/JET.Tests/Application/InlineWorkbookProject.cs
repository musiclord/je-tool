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
            ["傳票號碼"] = "docNum",
            ["傳票項次"] = "lineID",
            ["傳票日期"] = "postDate",
            ["傳票日期_僅條件_JE"] = "voucherDate",
            ["核准日期"] = "docDate",
            ["科目代號"] = "accNum",
            ["科目名稱"] = "accName",
            ["摘要"] = "description",
            ["來源模組"] = "jeSource",
            ["建立人員"] = "createBy",
            ["核准人員"] = "approveBy",
            ["人工傳票"] = "manual",
            ["金額"] = "amount",
            ["借方旗標"] = "dcField"
        };

    private readonly List<object?[]> _rows = [];
    private string[] _columns = [];

    public InlineGlWorkbookBuilder WithColumns(params string[] columns)
    {
        _columns = columns;
        return this;
    }

    public InlineGlWorkbookBuilder AddRow(params object?[] cells)
    {
        _rows.Add(cells);
        return this;
    }

    internal string WriteWorkbook()
    {
        return TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
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
                            cell.SetValue(dbl);
                            break;
                        case DateTime dateTime:
                            cell.SetValue(dateTime);
                            break;
                        case TimeSpan timeSpan:
                            cell.SetValue(timeSpan);
                            break;
                        default:
                            cell.SetValue(_rows[r][c]!.ToString());
                            break;
                    }
                }
            }
        });
    }

    internal Dictionary<string, string> BuildFlagModeMapping()
    {
        var mapping = _columns
            .Where(ColumnToKey.ContainsKey)
            .ToDictionary(c => ColumnToKey[c], c => c, StringComparer.Ordinal);

        if (mapping.ContainsKey("dcField"))
        {
            mapping["dcDebitCode"] = "1";
            // R9 requires both codes. Receipt 20261004-100405726 preserves the first failure;
            // this synthetic fixture has always explicitly used 1/0, so no product fallback is introduced.
            mapping["dcCreditCode"] = "0";
        }

        return mapping;
    }

    internal void PopulateMatchingTb(
        InlineTbWorkbookBuilder tb,
        string periodStart,
        string periodEnd)
    {
        var postDateIndex = Array.IndexOf(_columns, "傳票日期");
        var accountCodeIndex = Array.IndexOf(_columns, "科目代號");
        var accountNameIndex = Array.IndexOf(_columns, "科目名稱");
        var amountIndex = Array.IndexOf(_columns, "金額");
        var debitFlagIndex = Array.IndexOf(_columns, "借方旗標");
        if (postDateIndex < 0
            || accountCodeIndex < 0
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
            var postDate = ReadDate(row.ElementAtOrDefault(postDateIndex));
            var accountCode = Convert.ToString(
                row.ElementAtOrDefault(accountCodeIndex),
                CultureInfo.InvariantCulture);
            if (postDate is null
                || postDate < start
                || postDate > end
                || string.IsNullOrWhiteSpace(accountCode))
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

            totals.TryGetValue(accountCode, out var current);
            totals[accountCode] = (current.Name ?? accountName, current.Total + signed);
        }

        if (totals.Count == 0)
        {
            tb.AddRow("__EMPTY__", "空母體占位", 0m);
            return;
        }

        foreach (var (accountCode, total) in totals.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            tb.AddRow(accountCode, total.Name, total.Total);
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
/// 與 GL 並行宣告，供完整性測試（part(a) 控制總數、Not-in-TB）造對齊或刻意不齊的小母體。
/// 欄名固定:科目代號→accNum、科目名稱→accName、變動金額→amount（單一帶號變動欄）。
/// </summary>
internal sealed class InlineTbWorkbookBuilder
{
    private const string AccCodeColumn = "科目代號";
    private const string AccNameColumn = "科目名稱";
    private const string AmountColumn = "變動金額";

    private readonly List<(string? Code, string? Name, object Amount)> _rows = [];

    public InlineTbWorkbookBuilder AddRow(string? accountCode, string? accountName, object changeAmount)
    {
        _rows.Add((accountCode, accountName, changeAmount));
        return this;
    }

    internal bool HasRows => _rows.Count > 0;

    internal string WriteWorkbook()
    {
        return TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).SetValue(AccCodeColumn);
            sheet.Cell(1, 2).SetValue(AccNameColumn);
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
            ["accNum"] = AccCodeColumn,
            ["accName"] = AccNameColumn,
            ["amount"] = AmountColumn
        };
}

internal static class InlineWorkbookProject
{
    public static async Task<string> SetupAsync(
        HandlerTestHost host,
        Action<InlineGlWorkbookBuilder> configure,
        string? lastPeriodStart = null,
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
            entityName = "測試自含案件",
            operatorId = "tester",
            periodStart,
            periodEnd,
            lastPeriodStart,
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
