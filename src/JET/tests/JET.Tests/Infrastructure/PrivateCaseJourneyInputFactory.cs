using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using JET.Domain;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseJourneyInputFailure
{
    InvalidInput,
    PrivateFileUnavailable,
    WorkspaceRejected,
}

internal sealed class PrivateCaseJourneyInputException : InvalidOperationException
{
    internal PrivateCaseJourneyInputException(PrivateCaseJourneyInputFailure failure)
        : base($"無法準備私人案件的執行輸入（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseJourneyInputFailure Failure { get; }
}

internal sealed record PrivateCaseJourneyImportSource(
    [property: JsonIgnore] string FilePath,
    [property: JsonIgnore] string FileName,
    [property: JsonIgnore] string WorksheetName)
{
    public override string ToString() => "private case journey source (redacted)";
}

internal sealed record PrivateCaseJourneyReferenceFile(
    [property: JsonIgnore] string FilePath,
    [property: JsonIgnore] string FileName)
{
    public override string ToString() => "private case journey reference (redacted)";
}

internal sealed record PrivateCaseJourneyInput(
    [property: JsonIgnore] string CaseAlias,
    [property: JsonIgnore] string ProjectCode,
    [property: JsonIgnore] string EntityName,
    [property: JsonIgnore] string OperatorId,
    [property: JsonIgnore] string PeriodStart,
    [property: JsonIgnore] string PeriodEnd,
    [property: JsonIgnore] string LastPeriodStart,
    [property: JsonIgnore] long SampleSeed,
    [property: JsonIgnore] IReadOnlyList<PrivateCaseJourneyImportSource> GlSources,
    [property: JsonIgnore] IReadOnlyDictionary<string, string> GlMapping,
    [property: JsonIgnore] string GlAmountMode,
    [property: JsonIgnore] IReadOnlyList<PrivateCaseJourneyImportSource> TbSources,
    [property: JsonIgnore] IReadOnlyDictionary<string, string> TbMapping,
    [property: JsonIgnore] string TbChangeMode,
    [property: JsonIgnore] PrivateCaseJourneyReferenceFile AccountMappingFile,
    [property: JsonIgnore] PrivateCaseJourneyReferenceFile? AuthorizedPreparerFile,
    [property: JsonIgnore] PrivateCaseJourneyReferenceFile HolidayFile,
    [property: JsonIgnore] PrivateCaseJourneyReferenceFile MakeupDayFile,
    [property: JsonIgnore] IReadOnlyList<DateOnly> MakeupDates,
    [property: JsonIgnore] JsonElement FilterScenarios)
{
    public override string ToString() => $"private case journey input ({CaseAlias})";
}

internal sealed record PrivateCasePreparedJourneyInput(
    [property: JsonIgnore] PrivateCaseJourneyInput Input,
    int PrivateSourceCount,
    int GeneratedReferenceCount)
{
    public override string ToString() =>
        $"private case prepared journey input ({Input.CaseAlias})";
}

internal static class PrivateCaseJourneyInputFactory
{
    internal static PrivateCasePreparedJourneyInput Create(
        PrivateCaseManifest manifest,
        string authorizedRootPath,
        PrivateCaseInputWorkspace workspace,
        PrivateCaseFileAccess? fileAccess = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizedRootPath);
        ArgumentNullException.ThrowIfNull(workspace);
        fileAccess ??= new PrivateCaseFileAccess();

        try
        {
            var glSources = CopySources(
                manifest.Gl.Sources,
                PrivateCaseInputRole.Gl,
                authorizedRootPath,
                workspace,
                fileAccess);
            var tbSources = CopySources(
                manifest.Tb.Sources,
                PrivateCaseInputRole.Tb,
                authorizedRootPath,
                workspace,
                fileAccess);
            var accountMapping = CopyReference(
                manifest.ReferenceData.AccountMappingRelativePath,
                PrivateCaseInputRole.AccountMapping,
                authorizedRootPath,
                workspace,
                fileAccess);
            var authorizedPreparer = manifest.ReferenceData.AuthorizedPreparerMode switch
            {
                PrivateCaseAuthorizedPreparerMode.NotProvided => null,
                PrivateCaseAuthorizedPreparerMode.File => CopyReference(
                    manifest.ReferenceData.AuthorizedPreparerRelativePath!,
                    PrivateCaseInputRole.AuthorizedPreparer,
                    authorizedRootPath,
                    workspace,
                    fileAccess),
                _ => throw Error(PrivateCaseJourneyInputFailure.InvalidInput),
            };
            var holiday = CreateCalendarReference(
                workspace,
                PrivateCaseInputRole.Holiday,
                manifest.ReferenceData.HolidayDates,
                WriteHolidayWorkbook);
            var makeupDay = CreateCalendarReference(
                workspace,
                PrivateCaseInputRole.MakeupDay,
                manifest.ReferenceData.MakeupDates,
                WriteMakeupDayWorkbook);

            var project = manifest.Project;
            var input = new PrivateCaseJourneyInput(
                manifest.CaseAlias,
                project.ProjectCode,
                project.EntityName,
                project.OperatorId,
                FormatDate(project.PeriodStart),
                FormatDate(project.PeriodEnd),
                FormatDate(project.LastPeriodStart),
                project.SampleSeed,
                glSources,
                manifest.Gl.Mapping,
                GlAmountModeNames.ToWireName(manifest.Gl.AmountMode),
                tbSources,
                manifest.Tb.Mapping,
                TbChangeModeNames.ToWireName(manifest.Tb.ChangeMode),
                accountMapping,
                authorizedPreparer,
                holiday,
                makeupDay,
                manifest.ReferenceData.MakeupDates,
                JsonSerializer.SerializeToElement(manifest.Scenarios));
            var privateSourceCount = glSources.Count
                + tbSources.Count
                + 1
                + (authorizedPreparer is null ? 0 : 1);
            return new PrivateCasePreparedJourneyInput(
                input,
                privateSourceCount,
                GeneratedReferenceCount: 2);
        }
        catch (PrivateCaseJourneyInputException)
        {
            throw;
        }
        catch (PrivateCaseFileAccessException)
        {
            throw Error(PrivateCaseJourneyInputFailure.PrivateFileUnavailable);
        }
        catch (PrivateCaseInputWorkspaceException)
        {
            throw Error(PrivateCaseJourneyInputFailure.WorkspaceRejected);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            throw Error(PrivateCaseJourneyInputFailure.WorkspaceRejected);
        }
    }

    private static IReadOnlyList<PrivateCaseJourneyImportSource> CopySources(
        IReadOnlyList<PrivateCaseSource> sources,
        PrivateCaseInputRole role,
        string authorizedRootPath,
        PrivateCaseInputWorkspace workspace,
        PrivateCaseFileAccess fileAccess)
    {
        var result = new List<PrivateCaseJourneyImportSource>(sources.Count);
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            using var opened = fileAccess.OpenRead(authorizedRootPath, source.RelativePath);
            var copy = workspace.CopyInput(
                opened,
                role,
                index + 1,
                SafeExtension(source.RelativePath));
            result.Add(new PrivateCaseJourneyImportSource(
                copy.FullPath,
                copy.FileName,
                source.WorksheetName));
        }
        return result;
    }

    private static PrivateCaseJourneyReferenceFile CopyReference(
        string relativePath,
        PrivateCaseInputRole role,
        string authorizedRootPath,
        PrivateCaseInputWorkspace workspace,
        PrivateCaseFileAccess fileAccess)
    {
        using var opened = fileAccess.OpenRead(authorizedRootPath, relativePath);
        var copy = workspace.CopyInput(
            opened,
            role,
            position: 1,
            SafeExtension(relativePath));
        return new PrivateCaseJourneyReferenceFile(copy.FullPath, copy.FileName);
    }

    private static PrivateCaseJourneyReferenceFile CreateCalendarReference(
        PrivateCaseInputWorkspace workspace,
        PrivateCaseInputRole role,
        IReadOnlyList<DateOnly> dates,
        Action<Stream, IReadOnlyList<DateOnly>> write)
    {
        var generated = workspace.CreateGeneratedInput(
            role,
            position: 1,
            ".xlsx",
            stream => write(stream, dates));
        return new PrivateCaseJourneyReferenceFile(generated.FullPath, generated.FileName);
    }

    private static void WriteHolidayWorkbook(
        Stream stream,
        IReadOnlyList<DateOnly> dates)
    {
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
            sheet.Cell(row, 2).Value = $"private-case-holiday-{index + 1:000}";
            sheet.Cell(row, 3).Value = "Y";
        }
        workbook.SaveAs(stream);
    }

    private static void WriteMakeupDayWorkbook(
        Stream stream,
        IReadOnlyList<DateOnly> dates)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("MakeupDay");
        sheet.Cell(1, 2).Value = "Makeup Day Table";
        sheet.Cell(2, 1).Value = "Date_of_MakeUpday";
        sheet.Cell(2, 2).Value = "MakeUpDay_Desc";
        for (var index = 0; index < dates.Count; index++)
        {
            var row = index + 3;
            sheet.Cell(row, 1).Value = dates[index].ToDateTime(TimeOnly.MinValue);
            sheet.Cell(row, 2).Value = $"private-case-makeup-day-{index + 1:000}";
        }
        workbook.SaveAs(stream);
    }

    private static string SafeExtension(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xlsx" => ".xlsx",
            ".csv" => ".csv",
            ".txt" => ".txt",
            _ => throw Error(PrivateCaseJourneyInputFailure.InvalidInput),
        };

    private static string FormatDate(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static PrivateCaseJourneyInputException Error(
        PrivateCaseJourneyInputFailure failure) => new(failure);
}
