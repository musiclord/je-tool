using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using JET.Domain;

namespace JET.Tests.Infrastructure;

internal sealed record LegacyAuditParityPreparedJourneyInput(
    [property: JsonIgnore] LegacyAuditParityJourneyInput Input,
    [property: JsonIgnore] long ExpectedGlImportedRowCount,
    [property: JsonIgnore] long ExpectedTbImportedRowCount)
{
    public override string ToString() =>
        $"legacy audit parity prepared journey input ({Input.CaseAlias})";
}

/// <summary>
/// Converts an ignored, local-only legacy profile into the formal journey input. It deliberately
/// derives import oracles from the profile scan instead of observing journey results.
/// </summary>
internal static class LegacyAuditParityJourneyInputFactory
{
    internal const string GlAmountModeFieldId = "gl-amount-mode";
    internal const string TbChangeModeFieldId = "tb-change-mode";

    private const string AuthorizedPreparerDecisionFieldId =
        LegacyAuditParityProfileBuilder.AuthorizedPreparerDecidedNotProvidedId;
    private const string HolidayFileName = "holiday-input.xlsx";
    private const string MakeupDayFileName = "makeup-day-input.xlsx";

    internal static LegacyAuditParityPreparedJourneyInput Create(
        LegacyAuditParityProfile profile,
        LegacyParityWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(workspace);

        return Create(profile, workspace.Path);
    }

    internal static LegacyAuditParityPreparedJourneyInput Create(
        LegacyAuditParityProfile profile,
        string ownedWorkspacePath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedWorkspacePath);

        try
        {
            return CreateCore(profile, ownedWorkspacePath);
        }
        catch (LegacyAuditParityJourneyCompletenessException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Incomplete("journey-input-factory");
        }
    }

    private static LegacyAuditParityPreparedJourneyInput CreateCore(
        LegacyAuditParityProfile profile,
        string ownedWorkspacePath)
    {
        var identity = profile.Alias switch
        {
            "case-A" => new SafeJourneyIdentity(
                "PARITY-CASE-A",
                "legacy parity case A",
                1_600_001),
            "case-B" => new SafeJourneyIdentity(
                "PARITY-CASE-B",
                "legacy parity case B",
                1_600_002),
            _ => throw Incomplete("case-alias"),
        };

        if (profile.GlAmountMode is not { } glAmountMode)
        {
            throw Incomplete(GlAmountModeFieldId);
        }
        if (profile.TbChangeMode is not { } tbChangeMode)
        {
            throw Incomplete(TbChangeModeFieldId);
        }
        if (!profile.IsRuleNotApplicableByDecision(
                LegacyAuditParityProfileBuilder.NonAuthorizedPreparerRuleSlug))
        {
            throw Incomplete(AuthorizedPreparerDecisionFieldId);
        }
        if (profile.Scenarios.Count == 0)
        {
            throw Incomplete("filter-scenarios");
        }

        var expectedGlRows = ExpectedRows(profile.GlSources, "gl-source-row-count");
        var expectedTbRows = ExpectedRows(profile.TbSources, "tb-source-row-count");
        ValidateReferencePath(profile.AccountMapping.ImportPath, "account-mapping-file");
        if (profile.AccountMapping.RowCount <= 0)
        {
            throw Incomplete("account-mapping-row-count");
        }
        var glSources = CreateSources(profile.GlSources, "gl");
        var tbSources = CreateSources(profile.TbSources, "tb");

        LegacyAuditParityReferenceFile holidayFile;
        LegacyAuditParityReferenceFile makeupDayFile;
        try
        {
            holidayFile = WriteHolidayWorkbook(profile.HolidayDates, ownedWorkspacePath);
            makeupDayFile = WriteMakeupDayWorkbook(profile.MakeupDates, ownedWorkspacePath);
        }
        catch (LegacyAuditParityJourneyCompletenessException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Incomplete("calendar-reference-workbooks");
        }

        var input = new LegacyAuditParityJourneyInput(
            CaseAlias: profile.Alias,
            ProjectCode: identity.ProjectCode,
            EntityName: identity.EntityName,
            OperatorId: "legacy-parity",
            PeriodStart: FormatDate(profile.PeriodStart),
            PeriodEnd: FormatDate(profile.PeriodEnd),
            LastPeriodStart: FormatDate(profile.LastPeriodStart),
            SampleSeed: identity.SampleSeed,
            GlSources: glSources,
            GlImportMode: "replace",
            GlMapping: profile.GlMapping,
            GlAmountMode: GlAmountModeNames.ToWireName(glAmountMode),
            TbSources: tbSources,
            TbImportMode: "replace",
            TbMapping: profile.TbMapping,
            TbChangeMode: TbChangeModeNames.ToWireName(tbChangeMode),
            AccountMappingFile: new LegacyAuditParityReferenceFile(
                profile.AccountMapping.ImportPath,
                "account-mapping-input.xlsx"),
            AuthorizedPreparerFile: null,
            HolidayFile: holidayFile,
            MakeupDayFile: makeupDayFile,
            FilterScenarios: JsonSerializer.SerializeToElement(profile.Scenarios),
            LegacyFilterScenarioIds: LegacyAuditParityFilterScenarioBindings.FromProfile(profile),
            ObservationCase: profile.Alias == "case-A"
                ? LegacyParityCase.CaseA
                : LegacyParityCase.CaseB);

        return new LegacyAuditParityPreparedJourneyInput(input, expectedGlRows, expectedTbRows);
    }

    private static IReadOnlyList<LegacyAuditParityImportSource> CreateSources(
        IReadOnlyList<LegacyParityTabularSource> sources,
        string dataset) =>
        sources.Select((source, index) => new LegacyAuditParityImportSource(
                source.FilePath,
                $"{dataset}-source-{index + 1:000}{SafeExtension(source.FilePath)}",
                source.SheetName))
            .ToArray();

    private static long ExpectedRows(
        IReadOnlyList<LegacyParityTabularSource> sources,
        string fieldId)
    {
        if (sources.Count == 0)
        {
            throw Incomplete(fieldId);
        }

        long total = 0;
        foreach (var source in sources)
        {
            ValidateReferencePath(source.FilePath, fieldId);
            if (source.DataRowCount <= 0)
            {
                throw Incomplete(fieldId);
            }

            try
            {
                total = checked(total + source.DataRowCount);
            }
            catch (OverflowException)
            {
                throw Incomplete(fieldId);
            }
        }

        return total;
    }

    private static LegacyAuditParityReferenceFile WriteHolidayWorkbook(
        IReadOnlyList<DateOnly> dates,
        string workspacePath)
    {
        var path = OwnedOutputPath(workspacePath, HolidayFileName, "holiday-file-output");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Holiday");
        sheet.Cell(1, 2).Value = "Holiday Table";
        sheet.Cell(2, 1).Value = "Date_of_Holiday";
        sheet.Cell(2, 2).Value = "Holiday_Name";
        sheet.Cell(2, 3).Value = "IS_Holiday";
        for (var index = 0; index < dates.Count; index++)
        {
            var row = index + 3;
            sheet.Cell(row, 1).Value = dates[index].ToDateTime(TimeOnly.MinValue);
            sheet.Cell(row, 2).Value = $"legacy-parity-holiday-{index + 1:000}";
            sheet.Cell(row, 3).Value = "Y";
        }
        workbook.SaveAs(path);
        return new LegacyAuditParityReferenceFile(path, HolidayFileName);
    }

    private static LegacyAuditParityReferenceFile WriteMakeupDayWorkbook(
        IReadOnlyList<DateOnly> dates,
        string workspacePath)
    {
        var path = OwnedOutputPath(workspacePath, MakeupDayFileName, "makeup-day-file-output");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("MakeupDay");
        sheet.Cell(1, 2).Value = "Makeup Day Table";
        sheet.Cell(2, 1).Value = "Date_of_MakeUpday";
        sheet.Cell(2, 2).Value = "MakeUpDay_Desc";
        for (var index = 0; index < dates.Count; index++)
        {
            var row = index + 3;
            sheet.Cell(row, 1).Value = dates[index].ToDateTime(TimeOnly.MinValue);
            sheet.Cell(row, 2).Value = $"legacy-parity-makeup-day-{index + 1:000}";
        }
        workbook.SaveAs(path);
        return new LegacyAuditParityReferenceFile(path, MakeupDayFileName);
    }

    private static string OwnedOutputPath(
        string workspacePath,
        string fileName,
        string fieldId)
    {
        if (!Directory.Exists(workspacePath))
        {
            throw Incomplete(fieldId);
        }

        var root = Path.GetFullPath(workspacePath);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Incomplete(fieldId);
        }
        if (File.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Incomplete(fieldId);
        }

        return path;
    }

    private static void ValidateReferencePath(string path, string fieldId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)
                || !Path.IsPathFullyQualified(path)
                || !File.Exists(path))
            {
                throw Incomplete(fieldId);
            }
        }
        catch (LegacyAuditParityJourneyCompletenessException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Incomplete(fieldId);
        }
    }

    private static string SafeExtension(string path)
    {
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".xlsx" => ".xlsx",
                ".csv" => ".csv",
                ".txt" => ".txt",
                _ => throw Incomplete("source-file-extension"),
            };
        }
        catch (LegacyAuditParityJourneyCompletenessException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Incomplete("source-file-extension");
        }
    }

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static LegacyAuditParityJourneyCompletenessException Incomplete(string fieldId) =>
        new(fieldId);

    private sealed record SafeJourneyIdentity(
        string ProjectCode,
        string EntityName,
        long SampleSeed);
}
