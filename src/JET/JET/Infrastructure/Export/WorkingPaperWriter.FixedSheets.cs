using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class WorkingPaperWriter
{
    // ================= 封面與固定文字的工作表：只寫固定文字，沒有資料列 =================

    /// <summary>
    /// 「資料預先整理之說明」:公司名、測試期間與固定說明。
    /// A6 只保留儲存格格式，不寫字；兩份舊版 IDEA 程式都沒有寫這一格。
    /// </summary>
    private static void EmitCoverSheet(
        WorkingPaperWriteSession session, List<SheetStat> stats,
        WorkingPaperContext context, WorkingPaperSheetPlan? sheetPlan, CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        using var sheet = session.OpenSheet(WorkingPaperSheetCatalog.Cover);

        sheet.WriteFixedRow(1, [sheet.TextCell(1, 1, $"公司名稱 : {context.CompanyName}", WorkingPaperStyles.Bold)]);
        sheet.WriteFixedRow(2, [sheet.TextCell(2, 1,
            $"測試資料期間 :  {context.PeriodStart} ~ {context.PeriodEnd}", WorkingPaperStyles.Bold)]);
        sheet.WriteFixedRow(3, [sheet.TextCell(3, 1,
            null,
            WorkingPaperStyles.Bold)]);
        sheet.WriteFixedRow(5, [sheet.TextCell(5, 1,
            "請於下方加入針對JE Testing Tool所需資料預先整理之說明底稿(CAATs Document)。", WorkingPaperStyles.Plain)]);
        sheet.WriteFixedRow(6, [sheet.TextCell(6, 1, null, WorkingPaperStyles.Plain)]);

        AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
    }

    /// <summary>「JE WorkingPaper說明」：A1 是「說明：」標籤，B1 是整段固定說明文字，合併 B1:O1。</summary>
    private static void EmitIntroSheet(
        WorkingPaperWriteSession session, List<SheetStat> stats, WorkingPaperSheetPlan? sheetPlan,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        using var sheet = session.OpenSheet(WorkingPaperSheetCatalog.Intro);

        sheet.WriteFixedRow(1,
        [
            sheet.TextCell(1, 1, "說明：", WorkingPaperStyles.Bold),
            sheet.TextCell(
                1,
                2,
                PlannedText(sheetPlan, 0, IntroBoilerplate),
                WorkingPaperStyles.BoldWrap)
        ]);
        sheet.AddMerge("B1:O1");

        AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
    }

    /// <summary>「step5 財務報表關帳後調整之分錄」:A1 黃底橫幅(合併 A1:R1);其餘手填留空。</summary>
    private static void EmitStep5Sheet(
        WorkingPaperWriteSession session, List<SheetStat> stats, WorkingPaperSheetPlan? sheetPlan,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        using var sheet = session.OpenSheet(WorkingPaperSheetCatalog.Step5);

        sheet.WriteFixedRow(1,
        [
            sheet.TextCell(
                1,
                1,
                sheetPlan?.AuditCondition ?? Step5Banner,
                WorkingPaperStyles.YellowBanner)
        ]);
        sheet.AddMerge("A1:R1");

        AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
    }

    // ================= 三張「自動化工具」參考資料工作表：結構各不相同，各自處理 =================

    /// <summary>
    /// 「自動化工具-檔案欄位資訊」：內容只用 AuditCore 算好的那一份配對後欄位資訊，這裡不自己推算。
    /// TB 的欄位列完後空兩列，再接 GL；GL 的起始列號隨 TB 的列數變動。
    /// </summary>
    private async Task EmitFieldInfoSheetAsync(
        WorkingPaperWriteSession session, List<SheetStat> stats,
        WorkingPaperContext context, FieldInfoProjection projection,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var tbMapping = await mappingStates.FindAsync(context.ProjectId, DatasetKind.Tb, cancellationToken);
        var glMapping = await mappingStates.FindAsync(context.ProjectId, DatasetKind.Gl, cancellationToken);
        var metadata = glMapping is not null && tbMapping is not null
            ? MappingMetadataCodec.Encode(glMapping, tbMapping)
            : null;

        EmitCanonicalFieldInfoSheet(
            session,
            stats,
            projection,
            metadata,
            cancellationToken,
            progress);
    }

    private static void EmitCanonicalFieldInfoSheet(
        WorkingPaperWriteSession session,
        List<SheetStat> stats,
        FieldInfoProjection projection,
        string? metadata,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var headers = new[]
        {
            "配對前欄位名稱", "欄位型態", "文字長度", "小數位數", "配對後欄位名稱"
        };
        var allRows = projection.TbRows.Concat(projection.GlRows).ToArray();
        var widths = new[]
        {
            FieldInfoAutoFitWidth(new[]
            {
                "TB檔案配對前後欄位對照表",
                "GL檔案配對前後欄位對照表",
                headers[0]
            }.Concat(allRows.Select(row => row.DisplayName))),
            FieldInfoAutoFitWidth(new[] { headers[1] }.Concat(allRows.Select(row => row.FieldType))),
            FieldInfoAutoFitWidth(new[] { headers[2] }.Concat(
                allRows.Select(row => row.TextLength?.ToString(CultureInfo.InvariantCulture)))),
            FieldInfoAutoFitWidth(new[] { headers[3] }.Concat(
                allRows.Select(row => row.DecimalPlaces?.ToString(CultureInfo.InvariantCulture)))),
            FieldInfoAutoFitWidth(new[] { headers[4] }.Concat(allRows.Select(row => row.ActualFieldName)))
        };
        widths[0] = Math.Min(widths[0], FormalFieldInfoAppearance.NameColumnMaxWidth);

        var glHeaderRow = checked((uint)projection.TbRows.Count + 7U);
        using var sheet = session.OpenSheet(
            WorkingPaperSheetCatalog.FieldInfo,
            FieldInfoAppearance(glHeaderRow));
        var headerHeight = FormalWorkbookTypography.RowHeight(FormalFieldInfoAppearance.HeaderFontSize);
        sheet.SetDefaultRowHeight(FormalWorkbookTypography.RowHeight(WorkingPaperStyles.RegularFontSize));
        sheet.SetRowHeight(3, headerHeight);
        sheet.SetRowHeight(glHeaderRow, headerHeight);
        SetWrappedFieldNameHeights(sheet, projection.TbRows, firstRow: 4, widths[0]);
        SetWrappedFieldNameHeights(sheet, projection.GlRows, firstRow: glHeaderRow + 1, widths[0]);
        for (uint column = 1; column <= 5; column++)
        {
            sheet.SetColumnWidth(column, column, widths[column - 1], bestFit: true);
        }
        sheet.SetColumnWidth(6, 8, 2, hidden: true);

        // 第一列只放隱藏的配對資料（F 到 H 欄）。A1 不寫舊版的版本字樣 V.2019，也不套藍底紅字的版本列外觀；
        // 列號不動，配對資料的位置與還原方式照舊。
        var firstRow = new List<Cell>();
        if (metadata is not null)
        {
            firstRow.Add(sheet.TextCell(1, 6, MappingMetadataFormat.Marker));
            firstRow.Add(sheet.TextCell(1, 7, MappingMetadataFormat.CurrentVersion.ToString(
                CultureInfo.InvariantCulture)));
            firstRow.Add(sheet.VerbatimTextCell(1, 8, metadata));
        }
        sheet.WriteFixedRow(1, firstRow);
        // TB 與 GL 的標題列都不合併儲存格，底稿和資料驗證報告一致。
        sheet.WriteFixedRow(
            2,
            [sheet.TextCell(2, 1, "TB檔案配對前後欄位對照表", WorkingPaperStyles.Bold)]);
        WriteCanonicalFieldInfoHeader(sheet, 3, headers);

        uint rowIndex = 4;
        foreach (var row in projection.TbRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCanonicalFieldInfoRow(sheet, rowIndex++, row, widths[0]);
        }

        sheet.WriteFixedRow(rowIndex++, []);
        sheet.WriteFixedRow(rowIndex++, []);

        var glTitleRow = rowIndex++;
        sheet.WriteFixedRow(
            glTitleRow,
            [sheet.TextCell(
                glTitleRow,
                1,
                "GL檔案配對前後欄位對照表",
                WorkingPaperStyles.Bold)]);
        WriteCanonicalFieldInfoHeader(sheet, rowIndex++, headers);

        foreach (var row in projection.GlRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCanonicalFieldInfoRow(sheet, rowIndex++, row, widths[0]);
        }

        sheet.Protect();
        AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
    }

    private static void SetWrappedFieldNameHeights(
        SheetWriter sheet,
        IReadOnlyList<FieldInfoRow> rows,
        uint firstRow,
        double nameColumnWidth)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            var lines = FormalFieldInfoAppearance.NameLineCount(rows[index].DisplayName, nameColumnWidth);
            if (lines > 1)
            {
                sheet.SetRowHeight(
                    checked(firstRow + (uint)index),
                    FormalWorkbookTypography.RowHeight(WorkingPaperStyles.RegularFontSize, lines));
            }
        }
    }

    private static void WriteCanonicalFieldInfoHeader(
        SheetWriter sheet,
        uint rowIndex,
        IReadOnlyList<string> headers) =>
        sheet.WriteFixedRow(
            rowIndex,
            headers.Select((header, index) =>
                sheet.TextCell(
                    rowIndex,
                    (uint)index + 1,
                    header,
                    WorkingPaperStyles.Bold)).ToArray());

    private static void WriteCanonicalFieldInfoRow(
        SheetWriter sheet,
        uint rowIndex,
        FieldInfoRow row,
        double nameColumnWidth)
    {
        var cells = new List<Cell>
        {
            FormalFieldInfoAppearance.NameLineCount(row.DisplayName, nameColumnWidth) > 1
                ? sheet.TextCell(rowIndex, 1, row.DisplayName, WorkingPaperStyles.Wrap)
                : sheet.TextCell(rowIndex, 1, row.DisplayName),
            sheet.TextCell(rowIndex, 2, row.FieldType),
            row.TextLength is { } textLength
                ? sheet.NumberCell(rowIndex, 3, textLength)
                : sheet.BlankCell(rowIndex, 3),
            row.DecimalPlaces is { } decimals
                ? sheet.NumberCell(rowIndex, 4, decimals)
                : sheet.BlankCell(rowIndex, 4),
            sheet.TextCell(rowIndex, 5, row.ActualFieldName)
        };
        sheet.WriteRow(rowIndex, cells);
    }

    private static double FieldInfoAutoFitWidth(IEnumerable<string?> values)
    {
        var maximum = values
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(ExcelDisplayWidth.Measure)
            .DefaultIfEmpty(0D)
            .Max();
        return ExcelDisplayWidth.ToColumnWidth(maximum);
    }

    /// <summary>
    /// 「自動化工具-假期假日資訊」：依序是週末表、國定假日表與補班日表。
    /// 週末表照案件設定的每週休息日寫出，見 <see cref="WeekendRows"/>。
    /// 國定假日與補班日一天一列，日數有限；日期由 yyyy-MM-dd 轉成 yyyy/MM/dd 顯示，假日的 IS_HOLIDAY 一律寫 Y。
    /// 沒有匯入國定假日或補班日時,照舊版寫提示句,寫在原本該段標題的那一列:
    /// 舊版把假日的提示句寫在第 12 列,有補班日時又被補班標題蓋掉,所以 JET 不照舊版的列號。
    /// </summary>
    private async Task EmitCalendarInfoSheetAsync(
        WorkingPaperWriteSession session, List<SheetStat> stats,
        WorkingPaperContext context, CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var holidays = await calendarDays.FetchDaysAsync(context.ProjectId, CalendarDayType.Holiday, cancellationToken);
        var makeups = await calendarDays.FetchDaysAsync(context.ProjectId, CalendarDayType.Makeup, cancellationToken);
        var widthTracker = new ExcelDisplayWidthTracker(
            ["DATE_OF_MAKEUPDAY", "MAKEUPDAY_DESC", "IS_HOLIDAY"]);
        widthTracker.Observe(["DAYOFWEEK", "WORKDAY", null]);
        widthTracker.Observe(["DATE_OF_HOLIDAY", "HOLIDAY_NAME", "IS_HOLIDAY"]);
        var weekendRows = WeekendRows(context.NonWorkingDays);
        foreach (var (dayName, workday) in weekendRows)
        {
            widthTracker.Observe([dayName, workday, null]);
        }
        foreach (var day in holidays)
        {
            widthTracker.Observe([DisplayDate(day.Date), day.Name, "Y"]);
        }
        foreach (var day in makeups)
        {
            widthTracker.Observe([DisplayDate(day.Date), day.Name, null]);
        }
        var widths = widthTracker.Widths;
        var autoFit = holidays.Count > 0 || makeups.Count > 0;
        var pageNumber = 1;
        var pageRows = 0L;
        var rowIndex = 11u;
        var sheet = CreateCalendarBasePage(
            session,
            pageNumber,
            widths,
            weekendRows,
            holidays.Count,
            makeups.Count > 0,
            autoFit,
            cancellationToken);

        try
        {
            foreach (var day in holidays)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rowIndex > _continuationRowLimit)
                {
                    AddStat(stats, sheet.CloseAndSummarizeWith(pageRows, cancellationToken), progress);
                    sheet.Dispose();
                    pageNumber = checked(pageNumber + 1);
                    sheet = CreateCalendarContinuationPage(
                        session,
                        pageNumber,
                        CalendarDayType.Holiday,
                        widths,
                        autoFit);
                    rowIndex = 2;
                    pageRows = 0;
                }

                sheet.WriteRow(rowIndex,
                [
                    sheet.TextCell(rowIndex, 1, DisplayDate(day.Date)),
                    sheet.TextCell(rowIndex, 2, day.Name ?? string.Empty),
                    sheet.TextCell(rowIndex, 3, "Y")
                ]);
                rowIndex++;
                pageRows++;
                ReportIntermediateRows(stats, progress, sheet.Name, pageRows);
            }

            var rowsNeededForMakeupStart = makeups.Count > 0 ? 2u : 1u;
            if ((long)rowIndex + rowsNeededForMakeupStart > _continuationRowLimit)
            {
                AddStat(stats, sheet.CloseAndSummarizeWith(pageRows, cancellationToken), progress);
                sheet.Dispose();
                pageNumber = checked(pageNumber + 1);
                sheet = CreateCalendarContinuationPage(
                    session,
                    pageNumber,
                    CalendarDayType.Makeup,
                    widths,
                    autoFit,
                    hasDays: makeups.Count > 0);
                rowIndex = 2;
                pageRows = 0;
            }
            else
            {
                var makeupHeaderRow = checked(rowIndex + 1);
                if (makeups.Count > 0)
                {
                    WriteMakeupHeader(sheet, makeupHeaderRow);
                }
                else
                {
                    WriteCalendarNotice(sheet, makeupHeaderRow, NoMakeupDaysNotice);
                }
                rowIndex = checked(makeupHeaderRow + 1);
            }

            foreach (var day in makeups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rowIndex > _continuationRowLimit)
                {
                    AddStat(stats, sheet.CloseAndSummarizeWith(pageRows, cancellationToken), progress);
                    sheet.Dispose();
                    pageNumber = checked(pageNumber + 1);
                    sheet = CreateCalendarContinuationPage(
                        session,
                        pageNumber,
                        CalendarDayType.Makeup,
                        widths,
                        autoFit);
                    rowIndex = 2;
                    pageRows = 0;
                }

                sheet.WriteRow(rowIndex,
                [
                    sheet.TextCell(rowIndex, 1, DisplayDate(day.Date)),
                    sheet.TextCell(rowIndex, 2, day.Name ?? string.Empty)
                ]);
                rowIndex++;
                pageRows++;
                ReportIntermediateRows(stats, progress, sheet.Name, pageRows);
            }

            AddStat(stats, sheet.CloseAndSummarizeWith(pageRows, cancellationToken), progress);
        }
        finally
        {
            sheet.Dispose();
        }
    }

    private static SheetWriter CreateCalendarBasePage(
        WorkingPaperWriteSession session,
        int pageNumber,
        IReadOnlyList<double> widths,
        IReadOnlyList<(string DayName, string Workday)> weekendRows,
        int holidayCount,
        bool hasMakeupDays,
        bool autoFit,
        CancellationToken cancellationToken)
    {
        var sheet = session.OpenSheet(
            ExcelWorksheetConstraints.ContinuationSheetName(WorkingPaperSheetCatalog.CalendarInfo, pageNumber),
            CalendarBaseAppearance(holidayCount, hasMakeupDays));
        // 舊版整張保護假期假日資訊，續頁也一樣。
        sheet.Protect();
        ConfigureCalendarLayout(sheet, widths, autoFit);
        sheet.WriteFixedRow(1,
        [
            sheet.TextCell(1, 1, "DAYOFWEEK", WorkingPaperStyles.Bold),
            sheet.TextCell(1, 2, "WORKDAY", WorkingPaperStyles.Bold)
        ]);
        var weekendRow = 2u;
        foreach (var (dayName, workday) in weekendRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheet.WriteFixedRow(weekendRow,
            [
                sheet.TextCell(weekendRow, 1, dayName),
                sheet.TextCell(weekendRow, 2, workday)
            ]);
            weekendRow++;
        }

        if (holidayCount > 0)
        {
            WriteHolidayHeader(sheet, 10);
        }
        else
        {
            WriteCalendarNotice(sheet, 10, NoHolidaysNotice);
        }
        return sheet;
    }

    private static SheetWriter CreateCalendarContinuationPage(
        WorkingPaperWriteSession session,
        int pageNumber,
        CalendarDayType type,
        IReadOnlyList<double> widths,
        bool autoFit,
        bool hasDays = true)
    {
        var sheet = session.OpenSheet(
            ExcelWorksheetConstraints.ContinuationSheetName(WorkingPaperSheetCatalog.CalendarInfo, pageNumber),
            CalendarContinuationAppearance(type, hasDays));
        sheet.Protect();
        ConfigureCalendarLayout(sheet, widths, autoFit);
        if (!hasDays)
        {
            WriteCalendarNotice(
                sheet,
                1,
                type == CalendarDayType.Holiday ? NoHolidaysNotice : NoMakeupDaysNotice);
        }
        else if (type == CalendarDayType.Holiday)
        {
            WriteHolidayHeader(sheet, 1);
        }
        else
        {
            WriteMakeupHeader(sheet, 1);
        }

        return sheet;
    }

    private static void WriteHolidayHeader(SheetWriter sheet, uint row) =>
        sheet.WriteFixedRow(row,
        [
            sheet.TextCell(row, 1, "DATE_OF_HOLIDAY", WorkingPaperStyles.Bold),
            sheet.TextCell(row, 2, "HOLIDAY_NAME", WorkingPaperStyles.Bold),
            sheet.TextCell(row, 3, "IS_HOLIDAY", WorkingPaperStyles.Bold)
        ]);

    private static void WriteMakeupHeader(SheetWriter sheet, uint row) =>
        sheet.WriteFixedRow(row,
        [
            sheet.TextCell(row, 1, "DATE_OF_MAKEUPDAY", WorkingPaperStyles.Bold),
            sheet.TextCell(row, 2, "MAKEUPDAY_DESC", WorkingPaperStyles.Bold)
        ]);

    // 舊版 JET 寫在 A 欄的提示句，不加粗，也不參與欄寬自動調整（舊版在寫提示句之前就調完欄寬）。
    internal const string NoHolidaysNotice = "未設定假期資訊";
    internal const string NoMakeupDaysNotice = "未設定補班日/結帳日資訊";

    private static void WriteCalendarNotice(SheetWriter sheet, uint row, string notice) =>
        sheet.WriteFixedRow(row, [sheet.TextCell(row, 1, notice)]);

    /// <summary>
    /// 「自動化工具-科目配對資訊」：表頭是 GL_NUMBER、GL_NAME、STANDARDIZED_ACCOUNT_NAME，下面每個科目一列。
    /// 在 GL 出現但 TB 沒有的科目，GL_NAME 不寫科目名稱，照舊版 JET 寫固定字串「Not in TB」，
    /// 讓審計員一眼看出這個科目在 GL 出現卻對不到 TB 餘額。
    /// 哪些科目算 TB 沒有，由 repository 用完整性測試的同一段 SQL（ValidationProcedures.CompletenessDiffCte）判定；
    /// 這裡只看每一列的 NotInTb 旗標決定寫什麼。
    /// </summary>
    private async Task EmitAccountMappingSheetAsync(
        WorkingPaperWriteSession session, List<SheetStat> stats,
        WorkingPaperContext context, CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress)
    {
        var mappings = await accountMapping.FetchAllAsync(
            context.ProjectId, context.PeriodStart, context.PeriodEnd, cancellationToken);
        // 正式組裝時有 _accountMappingStateStore，用有沒有科目配對的匯入紀錄判斷來源存不存在；
        // 所以匯入成功但一列都沒有時，仍然套用舊版 JET 的表頭外觀與自動調整欄寬。
        // 用公開建構子建立時沒有這個 store，改用列數是否大於 0 判斷。
        var sourceExists = _accountMappingStateStore is null
            ? mappings.Count > 0
            : await _accountMappingStateStore.FindStateAsync(
                context.ProjectId,
                cancellationToken) is not null;
        var widthTracker = new ExcelDisplayWidthTracker(
            ["GL_NUMBER", "GL_NAME", "STANDARDIZED_ACCOUNT_NAME"]);
        foreach (var account in mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var glName = account.NotInTb ? AccountMappingExportRow.NotInTbName : account.AccountName ?? string.Empty;
            widthTracker.Observe([account.AccountNumber, glName, account.Category]);
        }
        var widths = widthTracker.Widths;
        await EmitContinuedRowsAsync(
            stats,
            WorkingPaperSheetCatalog.AccountMapping,
            firstDataRow: 2,
            reservedRowsAfterData: 0,
            ToItems(mappings, cancellationToken),
            (name, _) =>
            {
                var sheet = session.OpenSheet(
                    name,
                    AccountMappingAppearance(sourceExists));
                ConfigureAccountMappingInfoLayout(sheet, widths, sourceExists);
                sheet.WriteFixedRow(1,
                [
                    sheet.TextCell(1, 1, "GL_NUMBER", WorkingPaperStyles.Bold),
                    sheet.TextCell(1, 2, "GL_NAME", WorkingPaperStyles.Bold),
                    sheet.TextCell(1, 3, "STANDARDIZED_ACCOUNT_NAME", WorkingPaperStyles.Bold)
                ]);
                return sheet;
            },
            (sheet, row, account) =>
            {
                var glName = account.NotInTb ? AccountMappingExportRow.NotInTbName : account.AccountName ?? string.Empty;
                return
                [
                    sheet.TextCell(row, 1, account.AccountNumber),
                    sheet.TextCell(row, 2, glName),
                    sheet.TextCell(row, 3, account.Category)
                ];
            },
            (sheet, rows, _, ct) =>
            {
                sheet.Protect();
                return sheet.CloseAndSummarizeWith(rows, ct);
            },
            omitWhenEmpty: false,
            progress: progress,
            cancellationToken: cancellationToken);
    }

    private static void ConfigureCalendarLayout(
        SheetWriter sheet,
        IReadOnlyList<double> widths,
        bool autoFit)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(widths.Count, 3);
        sheet.SetDefaultRowHeight(FormalWorkbookTypography.RowHeight(WorkingPaperStyles.RegularFontSize));
        for (var index = 0; index < widths.Count; index++)
        {
            var column = checked((uint)index + 1);
            sheet.SetColumnWidth(column, column, widths[index], bestFit: autoFit);
        }
    }

    private static void ConfigureAccountMappingInfoLayout(
        SheetWriter sheet,
        IReadOnlyList<double> widths,
        bool autoFit)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(widths.Count, 3);
        sheet.SetDefaultRowHeight(FormalWorkbookTypography.RowHeight(WorkingPaperStyles.RegularFontSize));
        for (var index = 0; index < widths.Count; index++)
        {
            var column = checked((uint)index + 1);
            sheet.SetColumnWidth(column, column, widths[index], bestFit: autoFit);
        }
        sheet.SetRowHeight(1, 30);
    }

    private const string IntroBoilerplate =
        "此JE測試的WorkingPaper係透過JE Testing Tool產生之工作底稿，並依下列步驟分別記錄會計分錄測試" +
        "(JE Testing)所需之程序。\n(有關高風險JE測試的篩選判斷由查核團隊在該工具執行過程中完成定義。)";

    private const string Step5Banner =
        "若查核團隊發現受查客戶在財務報表關帳後，尚有入帳之調整分錄(Post-closing entries)，" +
        "或未入帳直接對財務報表之調整(Other adjustments)，\n則可將此類調整記錄於此處，或說明無此類情形。\n";

    /// <summary>
    /// 假期假日資訊的週末對照(DAYOFWEEK / WORKDAY)。照舊版 X_Append_Weekend 由週一排到週日,
    /// 案件設定為每週休息日的那幾天寫 Y、其他寫 N;WORKDAY=Y 表示「視為非工作日」,是舊版既定的意思。
    /// 案件沒有設定時照 <see cref="NonWorkingDays.Default"/> 寫週六、週日;設定成空集合時七天都寫 N。
    /// </summary>
    internal static IReadOnlyList<(string DayName, string Workday)> WeekendRows(IReadOnlyList<int>? nonWorkingDays)
    {
        var resolved = NonWorkingDays.Resolve(nonWorkingDays);
        return WeekendDayOrder
            .Select(day => (day.ToString(), resolved.Contains((int)day) ? "Y" : "N"))
            .ToArray();
    }

    private static readonly IReadOnlyList<DayOfWeek> WeekendDayOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];
}
