using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class WorkpaperWriter
{
    // Legacy Step5_Export_Excel_TW appearance registry:
    // BAS 9033-9764 / ISM 10412-11146. These component patches deliberately
    // derive from each template/generated prototype instead of replacing its
    // border, number format, alignment, or protection with a whole-cell style.
    private const string LegacyRed = "FFFF0000"; // BAS 9036/9102/9207/9452; ISM 10415/10481/10586/10834.
    private const string LegacyWhite = "FFFFFFFF"; // BAS 9103 / ISM 10482.
    private const string LegacyBlue = "FF0066FF"; // BAS 9104/9453 / ISM 10483/10835.

    // Calibri 12 bold blocks: BAS 9099-9101,9601-9603,9641-9643,
    // 9686-9688,9739-9741 / ISM 10478-10480,10983-10985,
    // 11023-11025,11068-11070,11121-11123.
    private static readonly WorkbookStylePatch LegacyCalibri12Bold = new(
        FontName: "Calibri",
        FontSize: 12D,
        Bold: true);

    // Red Calibri 12 warnings: BAS 9033-9036,9102,9204-9207 /
    // ISM 10412-10415,10481,10583-10586.
    private static readonly WorkbookStylePatch LegacyCalibri12BoldRed = new(
        FontName: "Calibri",
        FontSize: 12D,
        Bold: true,
        FontColorArgb: LegacyRed);

    // White-on-blue balance header: BAS 9103-9104 / ISM 10482-10483.
    private static readonly WorkbookStylePatch LegacyCalibri12BoldWhiteBlue = new(
        FontName: "Calibri",
        FontSize: 12D,
        Bold: true,
        FontColorArgb: LegacyWhite,
        FillForegroundArgb: LegacyBlue);

    private static readonly WorkbookStylePatch Borderless = new(Borderless: true);

    private sealed record WorkpaperAppearanceProfile(
        Func<uint, uint, WorkbookStylePatch?> PatchForCell);

    // Completeness warning: BAS 9033-9036 / ISM 10412-10415.
    private static WorkpaperAppearanceProfile? Step1Appearance(bool hasDifferences) =>
        hasDifferences
            ? new((row, column) =>
                column == 2 && row is >= 15 and <= 17
                    ? LegacyCalibri12BoldRed
                    : null)
            : null;

    // Unbalanced-voucher block: BAS 9099-9104 / ISM 10478-10483.
    private static WorkpaperAppearanceProfile? Step11Appearance(bool hasUnbalancedDocuments) =>
        hasUnbalancedDocuments
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

    // Missing creator-summary fallback: BAS 9204-9207 / ISM 10583-10586.
    private static WorkpaperAppearanceProfile? Step12Appearance(bool missingCreateByMapping) =>
        missingCreateByMapping
            ? new((row, column) =>
                row == 13 && column == 2
                    ? LegacyCalibri12BoldRed
                    : null)
            : null;

    // User-approved final schema: the entire step4-1 worksheet is borderless,
    // including retained template placeholders outside the emitted data range.
    private static WorkpaperAppearanceProfile Step41Appearance() =>
        new(static (_, _) => Borderless);

    // FieldInfo version/TB/GL cells: BAS 9449-9525 / ISM 10831-10907.
    // Its EntireColumn.AutoFit is BAS 9575 / ISM 10957 and is emitted by the writer.
    private static WorkpaperAppearanceProfile FieldInfoAppearance(uint glHeaderRow) =>
        new((row, column) =>
        {
            if (row == 1 && column == 1)
            {
                return FormalFieldInfoAppearance.Version;
            }
            if (row == 3 && column is >= 1 and <= 5)
            {
                return FormalFieldInfoAppearance.TbHeader;
            }
            return row == glHeaderRow && column is >= 1 and <= 5
                ? FormalFieldInfoAppearance.GlHeader
                : null;
        });

    // Weekend/holiday/make-up headers: BAS 9601-9688 / ISM 10983-11070.
    // Conditional AutoFit: BAS 9669,9712 / ISM 11051,11094; emitted by the writer.
    private static WorkpaperAppearanceProfile CalendarBaseAppearance(
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

    private static WorkpaperAppearanceProfile CalendarContinuationAppearance(
        CalendarDayType type) =>
        new((row, column) =>
            row == 1
            && column >= 1
            && column <= (type == CalendarDayType.Holiday ? 3U : 2U)
                ? LegacyCalibri12Bold
                : null);

    // Account-mapping header/AutoFit: BAS 9739-9764 / ISM 11121-11146.
    private static WorkpaperAppearanceProfile? AccountMappingAppearance(bool hasMappings) =>
        hasMappings
            ? new((row, column) =>
                row == 1 && column is >= 1 and <= 3
                    ? LegacyCalibri12Bold
                    : null)
            : null;
}
