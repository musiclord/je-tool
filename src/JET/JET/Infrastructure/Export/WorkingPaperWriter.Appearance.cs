using JET.Domain;

namespace JET.Infrastructure;

internal sealed partial class WorkingPaperWriter
{
    // 照舊版 JET 的 Step5_Export_Excel_TW 訂的各工作表外觀。這裡每一項只改字型、字色或底色這類個別項目，
    // 以範本或寫出程式原本的樣式為底，不拿一個完整的儲存格樣式整個換掉；
    // 框線、數字格式、對齊與保護設定因此都照原樣。
    private const string LegacyRed = "FFFF0000";
    private const string LegacyWhite = "FFFFFFFF";
    private const string LegacyBlue = "FF0066FF";

    // Calibri 12 點粗體的區塊。
    private static readonly WorkbookStylePatch LegacyCalibri12Bold = new(
        FontName: "Calibri",
        FontSize: 12D,
        Bold: true);

    // Calibri 12 點粗體紅字的警示。
    private static readonly WorkbookStylePatch LegacyCalibri12BoldRed = new(
        FontName: "Calibri",
        FontSize: 12D,
        Bold: true,
        FontColorArgb: LegacyRed);

    // 借貸不平明細表頭用的藍底白字。
    private static readonly WorkbookStylePatch LegacyCalibri12BoldWhiteBlue = new(
        FontName: "Calibri",
        FontSize: 12D,
        Bold: true,
        FontColorArgb: LegacyWhite,
        FillForegroundArgb: LegacyBlue);

    private static readonly WorkbookStylePatch Borderless = new(Borderless: true);

    private sealed record WorkingPaperAppearanceProfile(
        Func<uint, uint, WorkbookStylePatch?> PatchForCell);

    // step1：有差異時，B15 到 B17 的警示用紅字。
    private static WorkingPaperAppearanceProfile? CompletenessAppearance(bool hasDifferences) =>
        hasDifferences
            ? new((row, column) =>
                column == 2 && row is >= 15 and <= 17
                    ? LegacyCalibri12BoldRed
                    : null)
            : null;

    // step1-1：有借貸不平的傳票時，B12 到 F16 這一塊的外觀。第 16 列是藍底白字的表頭，其他列 B 欄紅字、其餘粗體。
    private static WorkingPaperAppearanceProfile? VoucherBalanceAppearance(bool hasUnbalancedVouchers) =>
        hasUnbalancedVouchers
            ? new((row, column) =>
            {
                if (row is < 12 or > 16 || column is < 2 or > 6)
                {
                    return null;
                }
                if (row == 16)
                {
                    return LegacyCalibri12BoldWhiteBlue;
                }
                return column == 2
                    ? LegacyCalibri12BoldRed
                    : LegacyCalibri12Bold;
            })
            : null;

    // step1-2：沒有配對傳票建立人員時，B13 的提示句用紅字。
    private static WorkingPaperAppearanceProfile? PreparerSummaryAppearance(bool missingCreatedByMapping) =>
        missingCreatedByMapping
            ? new((row, column) =>
                row == 13 && column == 2
                    ? LegacyCalibri12BoldRed
                    : null)
            : null;

    // step4-1 整張工作表都沒有框線，包含資料範圍以外、範本留下來的預留格子。
    private static WorkingPaperAppearanceProfile HighRiskVoucherDetailAppearance() =>
        new(static (_, _) => Borderless);

    // 欄位資訊頁 TB 與 GL 的表頭格。整欄自動調整欄寬由寫出工作表的程式另外處理。
    private static WorkingPaperAppearanceProfile FieldInfoAppearance(uint glHeaderRow) =>
        new((row, column) =>
        {
            if (row == 3 && column is >= 1 and <= 5)
            {
                return FormalFieldInfoAppearance.TbHeader;
            }
            return row == glHeaderRow && column is >= 1 and <= 5
                ? FormalFieldInfoAppearance.GlHeader
                : null;
        });

    // 週末、假日與補班日的表頭。依條件自動調整欄寬由寫出工作表的程式另外處理。
    private static WorkingPaperAppearanceProfile CalendarBaseAppearance(
        int holidayCount,
        bool hasMakeupDays)
    {
        var hasHolidays = holidayCount > 0;
        var makeupHeaderRow = checked((uint)holidayCount + 12U);
        return new((row, column) =>
        {
            if (row == 1 && column is >= 1 and <= 2)
            {
                return LegacyCalibri12Bold;
            }
            if (hasHolidays && row == 10 && column is >= 1 and <= 3)
            {
                return LegacyCalibri12Bold;
            }
            return hasMakeupDays
                   && row == makeupHeaderRow
                   && column is >= 1 and <= 2
                ? LegacyCalibri12Bold
                : null;
        });
    }

    // 續頁的那一段沒有日子時第 1 列是提示句，照舊版不加粗。
    private static WorkingPaperAppearanceProfile CalendarContinuationAppearance(
        CalendarDayType type,
        bool hasDays) =>
        new((row, column) =>
            hasDays
            && row == 1
            && column >= 1
            && column <= (type == CalendarDayType.Holiday ? 3U : 2U)
                ? LegacyCalibri12Bold
                : null);

    // 科目配對資訊頁的表頭，有科目配對時才套用。自動調整欄寬由寫出工作表的程式另外處理。
    private static WorkingPaperAppearanceProfile? AccountMappingAppearance(bool hasMappings) =>
        hasMappings
            ? new((row, column) =>
                row == 1 && column is >= 1 and <= 3
                    ? LegacyCalibri12Bold
                    : null)
            : null;
}
