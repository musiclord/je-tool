using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class WorkpaperWriter
{
    // ================= 封面 / 固定文字 emitter(一次性、固定文字;非資料表)=================

    /// <summary>
    /// 「資料預先整理之說明」:公司名 / 測試期間 / 固定說明 / CAATs 文件檔名。
    /// A6 檔名的 yyyymmdd 取 PeriodEnd 去掉非數字字元(樣本 PeriodEnd 形式不一:20241231 或 2024/12/31)。
    /// </summary>
    private static void EmitCoverSheet(
        WorkpaperWriteSession session, List<SheetStat> stats,
        WorkpaperContext context, WorkpaperSheetPlan? sheetPlan, CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        using var sheet = session.OpenSheet(WorkpaperSheetCatalog.Cover);

        sheet.WriteFixedRow(1, [sheet.TextCell(1, 1, $"公司名稱 : {context.CompanyName}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(2, [sheet.TextCell(2, 1,
            $"測試資料期間 :  {context.PeriodStart} ~ {context.PeriodEnd}", WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(3, [sheet.TextCell(3, 1,
            $"篩選測試母體 : {sheetPlan?.AuditCondition ?? GlPopulationScopeValues.DisplayName(context.PopulationScope)}",
            WorkpaperStyles.Bold)]);
        sheet.WriteFixedRow(5, [sheet.TextCell(5, 1,
            "請於下方加入針對JE Testing Tool所需資料預先整理之說明底稿(CAATs Document)。", WorkpaperStyles.Plain)]);
        sheet.WriteFixedRow(6, [sheet.TextCell(6, 1,
            $"請詳：{context.CompanyName}_CAATS_JE_WP_{DigitsOnly(context.PeriodEnd)}.docx", WorkpaperStyles.Plain)]);

        AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
    }

    /// <summary>「JE WorkingPaper說明」:A1「說明：」標籤 + B1 整段 boilerplate(合併 B1:O1)。</summary>
    private static void EmitIntroSheet(
        WorkpaperWriteSession session, List<SheetStat> stats, WorkpaperSheetPlan? sheetPlan,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        using var sheet = session.OpenSheet(WorkpaperSheetCatalog.Intro);

        sheet.WriteFixedRow(1,
        [
            sheet.TextCell(1, 1, "說明：", WorkpaperStyles.Bold),
            sheet.TextCell(
                1,
                2,
                PlannedText(sheetPlan, 0, IntroBoilerplate),
                WorkpaperStyles.BoldWrap)
        ]);
        sheet.AddMerge("B1:O1");

        AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
    }

    /// <summary>「step5 財務報表關帳後調整之分錄」:A1 黃底橫幅(合併 A1:R1);其餘手填留空。</summary>
    private static void EmitStep5Sheet(
        WorkpaperWriteSession session, List<SheetStat> stats, WorkpaperSheetPlan? sheetPlan,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        using var sheet = session.OpenSheet(WorkpaperSheetCatalog.Step5);

        sheet.WriteFixedRow(1,
        [
            sheet.TextCell(
                1,
                1,
                sheetPlan?.AuditCondition ?? Step5Banner,
                WorkpaperStyles.YellowBanner)
        ]);
        sheet.AddMerge("A1:R1");

        AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
    }

    // ================= 三張「自動化工具」參考資料表 emitter(Task 5;結構各異,各自處理)=================

    /// <summary>
    /// 「自動化工具-檔案欄位資訊」：typed production path 只接受 AuditCore
    /// finalized 的完整 target Field Info projection；TB 完整列後保留兩個
    /// physical blank rows，再以動態列號接 GL。public writer 的舊 mapping renderer
    /// 僅留作相容 fallback，不參與 production handler。
    /// </summary>

    private async Task EmitFieldInfoSheetAsync(
        WorkpaperWriteSession session, List<SheetStat> stats,
        WorkpaperContext context, FieldInfoProjection? projection,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var tbMapping = await mappingStates.FindAsync(context.ProjectId, DatasetKind.Tb, cancellationToken);
        var glMapping = await mappingStates.FindAsync(context.ProjectId, DatasetKind.Gl, cancellationToken);
        var metadata = glMapping is not null && tbMapping is not null
            ? MappingMetadataCodec.Encode(glMapping, tbMapping)
            : null;

        if (projection is null)
        {
            EmitCompatibilityFieldInfoSheet(
                session,
                stats,
                tbMapping,
                glMapping,
                metadata,
                cancellationToken,
                progress);
            return;
        }

        EmitCanonicalFieldInfoSheet(
            session,
            stats,
            projection,
            metadata,
            cancellationToken,
            progress);
    }

    private static void EmitCanonicalFieldInfoSheet(
        WorkpaperWriteSession session,
        List<SheetStat> stats,
        FieldInfoProjection projection,
        string? metadata,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
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
                "V.2019",
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

        var glHeaderRow = checked((uint)projection.TbRows.Count + 7U);
        using var sheet = session.OpenSheet(
            WorkpaperSheetCatalog.FieldInfo,
            FieldInfoAppearance(glHeaderRow));
        sheet.SetDefaultRowHeight(18);
        for (uint column = 1; column <= 5; column++)
        {
            sheet.SetColumnWidth(column, column, widths[column - 1], bestFit: true);
        }
        sheet.SetColumnWidth(6, 8, 2, hidden: true);

        var versionRow = new List<Cell> { sheet.TextCell(1, 1, "V.2019", WorkpaperStyles.Bold) };
        if (metadata is not null)
        {
            versionRow.Add(sheet.TextCell(1, 6, MappingMetadataFormat.Marker));
            versionRow.Add(sheet.TextCell(1, 7, MappingMetadataFormat.CurrentVersion.ToString(
                CultureInfo.InvariantCulture)));
            versionRow.Add(sheet.TextCell(1, 8, metadata));
        }
        sheet.WriteFixedRow(1, versionRow);
        sheet.AddMerge("A1:E1");
        sheet.WriteFixedRow(
            2,
            [sheet.TextCell(2, 1, "TB檔案配對前後欄位對照表", WorkpaperStyles.Bold)]);
        sheet.AddMerge("A2:E2");
        WriteCanonicalFieldInfoHeader(sheet, 3, headers);

        uint rowIndex = 4;
        foreach (var row in projection.TbRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCanonicalFieldInfoRow(sheet, rowIndex++, row);
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
                WorkpaperStyles.Bold)]);
        sheet.AddMerge($"A{glTitleRow}:E{glTitleRow}");
        WriteCanonicalFieldInfoHeader(sheet, rowIndex++, headers);

        foreach (var row in projection.GlRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCanonicalFieldInfoRow(sheet, rowIndex++, row);
        }

        sheet.Protect();
        AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
    }

    private static void EmitCompatibilityFieldInfoSheet(
        WorkpaperWriteSession session,
        List<SheetStat> stats,
        CommittedMapping? tbMapping,
        CommittedMapping? glMapping,
        string? metadata,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var tbFields = ResolveTbFields(tbMapping?.Mapping, cancellationToken);
        var glFields = ResolveGlFields(glMapping?.Mapping, cancellationToken);
        var allFields = tbFields.Concat(glFields).ToArray();
        var headers = new[]
        {
            "配對前欄位名稱", "欄位型態", "文字長度", "小數位數", "配對後欄位名稱"
        };
        var widths = new[]
        {
            FieldInfoAutoFitWidth(new[]
            {
                "V.2019",
                "TB檔案配對前後欄位對照表",
                "GL檔案配對前後欄位對照表",
                headers[0]
            }.Concat(allFields.Select(field => field.SourceColumn))),
            FieldInfoAutoFitWidth(new[] { headers[1] }.Concat(allFields.Select(field => field.KindLabel))),
            FieldInfoAutoFitWidth([headers[2]]),
            FieldInfoAutoFitWidth([headers[3]]),
            FieldInfoAutoFitWidth(new[] { headers[4] }.Concat(allFields.Select(field => field.CanonicalName)))
        };

        using var sheet = session.OpenSheet(
            WorkpaperSheetCatalog.FieldInfo,
            FieldInfoAppearance(glHeaderRow: 19));
        for (uint column = 1; column <= 5; column++)
        {
            sheet.SetColumnWidth(column, column, widths[column - 1], bestFit: true);
        }
        sheet.SetColumnWidth(6, 8, 2, hidden: true);

        var versionRow = new List<Cell> { sheet.TextCell(1, 1, "V.2019", WorkpaperStyles.Bold) };
        if (metadata is not null)
        {
            versionRow.Add(sheet.TextCell(1, 6, MappingMetadataFormat.Marker));
            versionRow.Add(sheet.TextCell(1, 7, MappingMetadataFormat.CurrentVersion.ToString(
                CultureInfo.InvariantCulture)));
            versionRow.Add(sheet.TextCell(1, 8, metadata));
        }
        sheet.WriteFixedRow(1, versionRow);
        sheet.AddMerge("A1:E1");

        WriteFieldSection(
            sheet, titleRow: 2, "TB檔案配對前後欄位對照表",
            tbFields, cancellationToken);

        var glDataRows = WriteFieldSection(
            sheet, titleRow: 18, "GL檔案配對前後欄位對照表",
            glFields, cancellationToken);

        var flagRow = (uint)(20 + glDataRows);
        foreach (var flag in DerivedFieldFlags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheet.WriteFixedRow(flagRow, [sheet.TextCell(flagRow, 1, flag, WorkpaperStyles.Default)]);
            flagRow++;
        }

        AddStat(stats, sheet.CloseAndSummarize(cancellationToken), progress);
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
                    WorkpaperStyles.Bold)).ToArray());

    private static void WriteCanonicalFieldInfoRow(
        SheetWriter sheet,
        uint rowIndex,
        FieldInfoRow row)
    {
        var cells = new List<Cell>
        {
            sheet.TextCell(rowIndex, 1, row.DisplayName),
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
    /// 寫一段欄位對照(標題列 + 欄標列 + 資料列),回資料列數。TB/GL 兩段結構相同故共用
    /// (業務分支只在「哪些欄、什麼 kind、有無正準名」,已由 <paramref name="fields"/> data structure 表達)。
    /// 標題列下一列為欄標、再下一列起為資料(對齊樣本:TB 標題 2/標頭 3/資料 4;GL 標題 18/標頭 19/資料 20)。
    /// </summary>
    private static int WriteFieldSection(
        SheetWriter sheet, uint titleRow, string title, IReadOnlyList<FieldMappingLine> fields,
        CancellationToken cancellationToken)
    {
        sheet.WriteFixedRow(titleRow, [sheet.TextCell(titleRow, 1, title, WorkpaperStyles.Bold)]);

        var headerRow = titleRow + 1;
        sheet.WriteFixedRow(headerRow,
        [
            sheet.TextCell(headerRow, 1, "配對前欄位名稱", WorkpaperStyles.Bold),
            sheet.TextCell(headerRow, 2, "欄位型態", WorkpaperStyles.Bold),
            sheet.TextCell(headerRow, 3, "文字長度", WorkpaperStyles.Bold),
            sheet.TextCell(headerRow, 4, "小數位數", WorkpaperStyles.Bold),
            sheet.TextCell(headerRow, 5, "配對後欄位名稱", WorkpaperStyles.Bold)
        ]);

        var dataRow = headerRow + 1;
        foreach (var field in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheet.WriteFixedRow(dataRow,
            [
                sheet.TextCell(dataRow, 1, field.SourceColumn),
                sheet.TextCell(dataRow, 2, field.KindLabel),
                sheet.BlankCell(dataRow, 3), // 文字長度:JET 未精確追蹤 → 留空
                sheet.BlankCell(dataRow, 4), // 小數位數:同上
                sheet.TextCell(dataRow, 5, field.CanonicalName ?? string.Empty)
            ]);
            dataRow++;
        }

        return fields.Count;
    }

    /// <summary>
    /// GL 已配對欄 → 欄位對照列。可見列集合、型態與順序由 Domain 欄位目錄提供；
    /// canonical name 仍走既有相容字典，因此未定義名稱保持空白。
    /// </summary>
    private static IReadOnlyList<FieldMappingLine> ResolveGlFields(
        IReadOnlyDictionary<string, string>? mapping,
        CancellationToken cancellationToken)
    {
        if (mapping is null)
        {
            return [];
        }

        var lines = new List<FieldMappingLine>();
        foreach (var slot in JetFieldCatalog.GlMappingSlots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 保留既有 GL Field Info 列集合：manual 與 amount-mode 輔助位置不列。
            if (slot.IncludeInFieldInfo
                && mapping.TryGetValue(slot.Key, out var source)
                && !string.IsNullOrWhiteSpace(source))
            {
                var field = JetFieldCatalog.FindGlSemanticFieldByMappingKey(slot.Key);
                lines.Add(new FieldMappingLine(
                    source, KindLabel(field.Kind), GlCanonicalNames.Gl.GetValueOrDefault(slot.Key)));
            }
        }

        return lines;
    }

    /// <summary>
    /// TB 已配對欄 → 欄位對照列。所有 mapping slots 仍依既有順序列出；
    /// <c>changeAmount</c> 是 semantic identity 而非 mapping key，所以各金額位置的
    /// compatibility canonical lookup 仍為空白，避免改動既有 Excel。
    /// </summary>
    private static IReadOnlyList<FieldMappingLine> ResolveTbFields(
        IReadOnlyDictionary<string, string>? mapping,
        CancellationToken cancellationToken)
    {
        if (mapping is null)
        {
            return [];
        }

        var lines = new List<FieldMappingLine>();
        foreach (var slot in JetFieldCatalog.TbMappingSlots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slot.IncludeInFieldInfo
                && mapping.TryGetValue(slot.Key, out var source)
                && !string.IsNullOrWhiteSpace(source))
            {
                var field = JetFieldCatalog.FindTbSemanticFieldByMappingKey(slot.Key);
                lines.Add(new FieldMappingLine(
                    source, KindLabel(field.Kind), GlCanonicalNames.Tb.GetValueOrDefault(slot.Key)));
            }
        }

        return lines;
    }

    /// <summary>Semantic field kind → 樣本中文型態標籤。</summary>
    private static string KindLabel(JetFieldValueKind kind) => kind switch
    {
        JetFieldValueKind.Text => "文字型態",
        JetFieldValueKind.Date => "日期型態",
        JetFieldValueKind.Boolean => "布林型態",
        JetFieldValueKind.Amount => "數字型態",
        _ => string.Empty
    };

    /// <summary>欄位對照的一列:配對前來源欄 + 中文型態標籤 + 配對後正準名(無則 null)。</summary>
    private readonly record struct FieldMappingLine(string SourceColumn, string KindLabel, string? CanonicalName);

    /// <summary>
    /// 「自動化工具-假期假日資訊」:固定週末表 + 假日表(calendar store holiday)+ 補班段(makeup)。
    /// 週末表為資料化常數(<see cref="WeekendRows"/>:Mon-Fri=N、Sat/Sun=Y),非逐列特判——
    /// 「WORKDAY=Y」反指「視為非工作日(週末)」是樣本既定語意,週末固定七天故以常數陣列表達。
    /// 假日/補班逐日列出(日數有界):日期由 yyyy-MM-dd 轉樣本顯示形式 yyyy/MM/dd;假日 IS_HOLIDAY 一律 Y。
    /// </summary>
    private async Task EmitCalendarInfoSheetAsync(
        WorkpaperWriteSession session, List<SheetStat> stats,
        WorkpaperContext context, CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var holidays = await calendarDays.FetchDaysAsync(context.ProjectId, CalendarDayType.Holiday, cancellationToken);
        var makeups = await calendarDays.FetchDaysAsync(context.ProjectId, CalendarDayType.Makeup, cancellationToken);
        var widthTracker = new ExcelDisplayWidthTracker(
            ["DATE_OF_MAKEUPDAY", "MAKEUPDAY_DESC", "IS_HOLIDAY"]);
        widthTracker.Observe(["DAYOFWEEK", "WORKDAY", null]);
        widthTracker.Observe(["DATE_OF_HOLIDAY", "HOLIDAY_NAME", "IS_HOLIDAY"]);
        foreach (var (dayName, workday) in WeekendRows)
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
                    autoFit);
                rowIndex = 2;
                pageRows = 0;
            }
            else
            {
                var makeupHeaderRow = checked(rowIndex + 1);
                WriteMakeupHeader(sheet, makeupHeaderRow);
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
        WorkpaperWriteSession session,
        int pageNumber,
        IReadOnlyList<double> widths,
        int holidayCount,
        bool hasMakeupDays,
        bool autoFit,
        CancellationToken cancellationToken)
    {
        var sheet = session.OpenSheet(
            ExcelWorksheetConstraints.ContinuationSheetName(WorkpaperSheetCatalog.CalendarInfo, pageNumber),
            CalendarBaseAppearance(holidayCount, hasMakeupDays));
        ConfigureCalendarLayout(sheet, widths, autoFit);
        sheet.WriteFixedRow(1,
        [
            sheet.TextCell(1, 1, "DAYOFWEEK", WorkpaperStyles.Bold),
            sheet.TextCell(1, 2, "WORKDAY", WorkpaperStyles.Bold)
        ]);
        var weekendRow = 2u;
        foreach (var (dayName, workday) in WeekendRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheet.WriteFixedRow(weekendRow,
            [
                sheet.TextCell(weekendRow, 1, dayName),
                sheet.TextCell(weekendRow, 2, workday)
            ]);
            weekendRow++;
        }

        WriteHolidayHeader(sheet, 10);
        return sheet;
    }

    private static SheetWriter CreateCalendarContinuationPage(
        WorkpaperWriteSession session,
        int pageNumber,
        CalendarDayType type,
        IReadOnlyList<double> widths,
        bool autoFit)
    {
        var sheet = session.OpenSheet(
            ExcelWorksheetConstraints.ContinuationSheetName(WorkpaperSheetCatalog.CalendarInfo, pageNumber),
            CalendarContinuationAppearance(type));
        ConfigureCalendarLayout(sheet, widths, autoFit);
        if (type == CalendarDayType.Holiday)
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
            sheet.TextCell(row, 1, "DATE_OF_HOLIDAY", WorkpaperStyles.Bold),
            sheet.TextCell(row, 2, "HOLIDAY_NAME", WorkpaperStyles.Bold),
            sheet.TextCell(row, 3, "IS_HOLIDAY", WorkpaperStyles.Bold)
        ]);

    private static void WriteMakeupHeader(SheetWriter sheet, uint row) =>
        sheet.WriteFixedRow(row,
        [
            sheet.TextCell(row, 1, "DATE_OF_MAKEUPDAY", WorkpaperStyles.Bold),
            sheet.TextCell(row, 2, "MAKEUPDAY_DESC", WorkpaperStyles.Bold)
        ]);

    /// <summary>
    /// 「自動化工具-科目配對資訊」:GL_NUMBER / GL_NAME / STANDARDIZED_ACCOUNT_NAME + 每科目列。
    /// **Not-in-TB**(在 GL 有、TB 無的科目)GL_NAME 寫字面「Not in TB」而非 account_name——
    /// 為什麼用字面值:對齊本機參考樣本中的固定字串「Not in TB」,標示「該科目在 GL 出現卻未配對到 TB 餘額」,
    /// 供審計員一眼辨識完整性缺口。判定來源是完整性 not_in_tb 集合(repo 以 ValidationProcedures.CompletenessDiffCte 為單一事實來源),
    /// emitter 只依旗標渲染(data-structure 對映,非逐列特判)。
    /// </summary>
    private async Task EmitAccountMappingSheetAsync(
        WorkpaperWriteSession session, List<SheetStat> stats,
        WorkpaperContext context, CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress)
    {
        var mappings = await accountMappings.FetchAllAsync(
            context.ProjectId, context.PeriodStart, context.PeriodEnd, cancellationToken);
        // Production internal DI uses import-batch presence, so a successful zero-row
        // source still receives the legacy header/AutoFit. The public compatibility
        // constructor has no presence store and retains its bounded row-count fallback.
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
            var glName = account.NotInTb ? "Not in TB" : account.AccountName ?? string.Empty;
            widthTracker.Observe([account.AccountCode, glName, account.Category]);
        }
        var widths = widthTracker.Widths;
        await EmitContinuedRowsAsync(
            stats,
            WorkpaperSheetCatalog.AccountMapping,
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
                    sheet.TextCell(1, 1, "GL_NUMBER", WorkpaperStyles.Bold),
                    sheet.TextCell(1, 2, "GL_NAME", WorkpaperStyles.Bold),
                    sheet.TextCell(1, 3, "STANDARDIZED_ACCOUNT_NAME", WorkpaperStyles.Bold)
                ]);
                return sheet;
            },
            (sheet, row, account) =>
            {
                var glName = account.NotInTb ? "Not in TB" : account.AccountName ?? string.Empty;
                return
                [
                    sheet.TextCell(row, 1, account.AccountCode),
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

    // ================= step1 家族 emitter =================

    /// <summary>
    /// 「step1 完整性測試」:A1-A4 共同表頭 + Step1 程序固定文字 + 結論 + 第 19 列欄標 +
    /// completenessAccounts **全科目逐頁**(科目編號/名稱/TB 變動(A)/GL 彙總(C)/差異(B)-(A))。
    /// 差異欄用 repo 的 DiffScaled(= tb_s - gl_s),step1／step1-3 同一定義不漂移。
    /// </summary>

    private static void ConfigureCalendarLayout(
        SheetWriter sheet,
        IReadOnlyList<double> widths,
        bool autoFit)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(widths.Count, 3);
        sheet.SetDefaultRowHeight(18);
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
        sheet.SetDefaultRowHeight(18);
        for (var index = 0; index < widths.Count; index++)
        {
            var column = checked((uint)index + 1);
            sheet.SetColumnWidth(column, column, widths[index], bestFit: autoFit);
        }
        sheet.SetRowHeight(1, 30);
    }

    private const string IntroBoilerplate =
        "此JE測試的WorkPaper係透過JE Testing Tool產生之工作底稿，並依下列步驟分別記錄會計分錄測試" +
        "(JE Testing)所需之程序。\n(有關高風險JE測試的篩選判斷由查核團隊在該工具執行過程中完成定義。)";

    private const string Step5Banner =
        "若查核團隊發現受查客戶在財務報表關帳後，尚有入帳之調整分錄(Post-closing entries)，" +
        "或未入帳直接對財務報表之調整(Other adjustments)，\n則可將此類調整記錄於此處，或說明無此類情形。\n";

    private static readonly IReadOnlyList<string> DerivedFieldFlags =
        ["K_R條件", "K_情境三四", "K_情境五", "K_情境八"];

    /// <summary>
    /// 假期假日資訊的固定週末對照(DAYOFWEEK / WORKDAY)。WORKDAY=Y 反指「視為非工作日(週末)」:
    /// 週一~五=N、週六日=Y(對齊樣本)。週末恆為固定七天,以資料化常數陣列表達,emitter 逐列輸出,不寫特判。
    /// </summary>
    private static readonly IReadOnlyList<(string DayName, string Workday)> WeekendRows =
    [
        ("Monday", "N"), ("Tuesday", "N"), ("Wednesday", "N"), ("Thursday", "N"),
        ("Friday", "N"), ("Saturday", "Y"), ("Sunday", "Y")
    ];

    // ================= 動態 C 欄集(step3/4/4-1 業務分支:data structure 消特例)=================

    /// <summary>
    /// 高風險情境的欄集 data structure。把「step4 用全部所選 position」
    /// 「step4-1 只用所選集合中 rowHitCount&gt;0 的 position」
    /// 這個業務分支(對齊樣本的動態 schema)收斂成兩個預先算好的有序 position list,emitter 只需:
    /// (1) 依 list 順序加欄標 cell;(2) 逐列依該列 matchedPositions 是否含某 position 標 Y——
    /// 完全由 data structure 對映,不寫「第幾欄特判」(Linus 好品味:用資料結構消除特例)。
    ///
    /// <see cref="Scenarios"/> 供 step3 列(name/rationale/voucherHitCount,position 升冪);
    /// <see cref="_allPositions"/> = 全部所選 position 升冪(step4 欄集);
    /// <see cref="_rowHitPositions"/> = rowHitCount&gt;0 的 position 升冪(step4-1 欄集)。
    /// name/rationale/position 取自 scenario store、命中數取自 tagMatrix counts,於此合併(鏡射 query.tagMatrixScenarios);
    /// step3 另需 rationale(D 欄),wire 摘要不含此欄,故用內部 <see cref="ScenarioRow"/> 承載。
    /// </summary>
}
