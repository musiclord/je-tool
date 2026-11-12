using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class WorkingPaperWriter
{
    // Legacy JET Step5_Export_Excel_TW appearance registry. These component patches deliberately
    // derive from each template/generated prototype instead of replacing its
    // border, number format, alignment, or protection with a whole-cell style.
    private const string LegacyRed = "FFFF0000";
    private const string LegacyWhite = "FFFFFFFF";
    private const string LegacyBlue = "FF0066FF";

    // Calibri 12 bold blocks.
    private static readonly WorkbookStylePatch LegacyCalibri12Bold = new(
        FontName: "Calibri",
        FontSize: 12D,
        Bold: true);

    // Red Calibri 12 warnings.
    private static readonly WorkbookStylePatch LegacyCalibri12BoldRed = new(
        FontName: "Calibri",
        FontSize: 12D,
        Bold: true,
        FontColorArgb: LegacyRed);

    // White-on-blue balance header.
    private static readonly WorkbookStylePatch LegacyCalibri12BoldWhiteBlue = new(
        FontName: "Calibri",
        FontSize: 12D,
        Bold: true,
        FontColorArgb: LegacyWhite,
        FillForegroundArgb: LegacyBlue);

    private static readonly WorkbookStylePatch Borderless = new(Borderless: true);

    private sealed record WorkingPaperAppearanceProfile(
        Func<uint, uint, WorkbookStylePatch?> PatchForCell);

    // Completeness warning.
    private static WorkingPaperAppearanceProfile? Step1Appearance(bool hasDifferences) =>
        hasDifferences
            ? new((row, column) =>
                column == 2 && row is >= 15 and <= 17
                    ? LegacyCalibri12BoldRed
                    : null)
            : null;

    // Unbalanced-voucher block.
    private static WorkingPaperAppearanceProfile? Step11Appearance(bool hasUnbalancedVouchers) =>
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

    // Missing preparer-summary fallback.
    private static WorkingPaperAppearanceProfile? Step12Appearance(bool missingCreatedByMapping) =>
        missingCreatedByMapping
            ? new((row, column) =>
                row == 13 && column == 2
                    ? LegacyCalibri12BoldRed
                    : null)
            : null;

    // The entire step4-1 worksheet is borderless,
    // including retained template placeholders outside the emitted data range.
    private static WorkingPaperAppearanceProfile Step41Appearance() =>
        new(static (_, _) => Borderless);

    // FieldInfo TB/GL header cells. Its EntireColumn.AutoFit is emitted by the writer.
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

    // Weekend/holiday/make-up headers. Conditional AutoFit is emitted by the writer.
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

    // Account-mapping header/AutoFit.
    private static WorkingPaperAppearanceProfile? AccountMappingAppearance(bool hasMappings) =>
        hasMappings
            ? new((row, column) =>
                row == 1 && column is >= 1 and <= 3
                    ? LegacyCalibri12Bold
                    : null)
            : null;
}
