using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyAuditParityJourneyInputFactoryTests
{
    [Fact]
    public void Create_CompleteSyntheticProfile_DerivesImportOraclesAndLocalReferenceWorkbooks()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"journey-input-{Guid.NewGuid():N}");
        var profile = CreateProfile(workspace, "case-A", TbChangeMode.DirectChange, includeDecision: true);

        var prepared = LegacyAuditParityJourneyInputFactory.Create(profile, workspace);

        Assert.Equal(5, prepared.ExpectedGlImportedRowCount);
        Assert.Equal(4, prepared.ExpectedTbImportedRowCount);
        Assert.Equal("case-A", prepared.Input.CaseAlias);
        Assert.Equal("PARITY-CASE-A", prepared.Input.ProjectCode);
        Assert.Equal("legacy parity case A", prepared.Input.ClientName);
        Assert.Equal("legacy-parity", prepared.Input.OperatorId);
        Assert.Equal(1_600_001, prepared.Input.SampleSeed);
        Assert.Equal("2031-01-01", prepared.Input.PeriodStart);
        Assert.Equal("2031-12-31", prepared.Input.PeriodEnd);
        Assert.Equal("2031-12-01", prepared.Input.PrepStartDate);
        Assert.Equal("signed", prepared.Input.GlAmountMode);
        Assert.Equal("direct", prepared.Input.TbChangeMode);
        Assert.Equal(new[] { "gl-source-001.xlsx", "gl-source-002.csv" },
            prepared.Input.GlSources.Select(source => source.FileName));
        Assert.Equal("tb-source-001.xlsx", Assert.Single(prepared.Input.TbSources).FileName);
        Assert.Null(prepared.Input.AuthorizedPreparerFile);
        Assert.Equal(profile.AccountMapping.ImportPath, prepared.Input.AccountMappingFile!.FilePath);
        Assert.Equal(JsonValueKind.Array, prepared.Input.FilterScenarios.ValueKind);
        Assert.Equal(
            [LegacyFilterScenarioId.FromOrdinal(1)],
            prepared.Input.LegacyFilterScenarioIds);

        AssertReferenceWorkbookIsOwned(workspace, prepared.Input.HolidayFile!);
        AssertReferenceWorkbookIsOwned(workspace, prepared.Input.MakeupDayFile!);
        using (var holidays = new XLWorkbook(prepared.Input.HolidayFile!.FilePath))
        {
            var sheet = holidays.Worksheet(1);
            Assert.Equal("Date_of_Holiday", sheet.Cell(2, 1).GetString());
            Assert.Equal(profile.HolidayDates.Count + 2, sheet.LastRowUsed()!.RowNumber());
            Assert.Equal(
                profile.HolidayDates[0].ToDateTime(TimeOnly.MinValue),
                sheet.Cell(3, 1).GetDateTime());
        }
        using (var makeupDays = new XLWorkbook(prepared.Input.MakeupDayFile!.FilePath))
        {
            var sheet = makeupDays.Worksheet(1);
            Assert.Equal("Date_of_MakeUpday", sheet.Cell(2, 1).GetString());
            Assert.Equal(profile.MakeupDates.Count + 2, sheet.LastRowUsed()!.RowNumber());
            Assert.Equal(
                profile.MakeupDates[0].ToDateTime(TimeOnly.MinValue),
                sheet.Cell(3, 1).GetDateTime());
        }

        var serialized = JsonSerializer.Serialize(prepared);
        Assert.Equal("{}", serialized);
        Assert.DoesNotContain(profile.GlSources[0].FilePath, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.AccountMapping.ImportPath, serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_CaseBWithoutTbChangeMode_FailsClosedWithoutGuessing()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseB,
            $"journey-input-missing-tb-{Guid.NewGuid():N}");
        var profile = CreateProfile(workspace, "case-B", tbChangeMode: null, includeDecision: true);

        var error = Assert.Throws<LegacyAuditParityJourneyCompletenessException>(() =>
            LegacyAuditParityJourneyInputFactory.Create(profile, workspace));

        Assert.Equal(LegacyAuditParityJourneyInputFactory.TbChangeModeFieldId, error.FieldId);
        Assert.Equal("tb-change-mode", error.FieldId);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "holiday-input.xlsx")));
        Assert.False(File.Exists(Path.Combine(workspace.Path, "makeup-day-input.xlsx")));
    }

    [Fact]
    public void Create_WithoutTypedAuthorizedPreparerDecision_FailsClosed()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"journey-input-missing-decision-{Guid.NewGuid():N}");
        var profile = CreateProfile(workspace, "case-A", TbChangeMode.DirectChange, includeDecision: false);

        var error = Assert.Throws<LegacyAuditParityJourneyCompletenessException>(() =>
            LegacyAuditParityJourneyInputFactory.Create(profile, workspace));

        Assert.Equal(
            LegacyAuditParityProfileBuilder.AuthorizedPreparerDecidedNotProvidedId,
            error.FieldId);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "holiday-input.xlsx")));
        Assert.False(File.Exists(Path.Combine(workspace.Path, "makeup-day-input.xlsx")));
    }

    private static LegacyAuditParityProfile CreateProfile(
        LegacyParityWorkspace workspace,
        string alias,
        TbChangeMode? tbChangeMode,
        bool includeDecision)
    {
        var glXlsxPath = Path.Combine(workspace.Path, "synthetic-ledger-part-1.xlsx");
        WriteRows(glXlsxPath, "LedgerPart1", 2);
        var glCsvPath = Path.Combine(workspace.Path, "synthetic-ledger-part-2.csv");
        File.WriteAllText(glCsvPath, "synthetic-column\nrow-1\nrow-2\nrow-3\n");
        var tbPath = Path.Combine(workspace.Path, "synthetic-trial-balance.xlsx");
        WriteRows(tbPath, "TrialBalance", 4);
        var accountMappingPath = Path.Combine(workspace.Path, "synthetic-account-mapping.xlsx");
        using (var accountMapping = new XLWorkbook())
        {
            var sheet = accountMapping.AddWorksheet("AccountMapping");
            sheet.Cell(1, 1).Value = "GL_NUMBER";
            sheet.Cell(1, 2).Value = "GL_NAME";
            sheet.Cell(1, 3).Value = "STANDARDIZED_ACCOUNT_NAME";
            sheet.Cell(2, 1).Value = "synthetic-account";
            sheet.Cell(2, 2).Value = "synthetic account name";
            sheet.Cell(2, 3).Value = AccountMappingCategories.Cash;
            accountMapping.SaveAs(accountMappingPath);
        }

        var glMapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.VoucherNumber] = "synthetic-column",
        };
        var tbMapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TbMappingKeys.AccountNumber] = "synthetic-column",
        };
        var scenarios = new[]
        {
            JsonSerializer.SerializeToElement(new
            {
                name = "synthetic scenario",
                rationale = "synthetic rationale",
                groups = new[]
                {
                    new
                    {
                        join = "AND",
                        conditions = new[]
                        {
                            // 舊式關鍵字條件已拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），合成情境改用「傳票摘要」的「包含任一文字」。
                            // 這次改的是測試資料，不是因為測試失敗。
                            new
                            {
                                join = "AND",
                                type = "fieldValue",
                                field = "description",
                                @operator = "contains",
                                values = new[] { "synthetic" },
                            },
                        },
                    },
                },
            }),
        };
        IReadOnlyList<LegacyDecidedNotProvidedInput> decidedInputs = includeDecision
            ? [new LegacyDecidedNotProvidedInput(
                LegacyAuditParityProfileBuilder.AuthorizedPreparerDecidedNotProvidedId,
                LegacyAuditParityProfileBuilder.NonAuthorizedPreparerRuleSlug)]
            : [];

        return new LegacyAuditParityProfile(
            alias,
            new DateOnly(2031, 1, 1),
            new DateOnly(2031, 12, 31),
            new DateOnly(2031, 12, 1),
            [
                new LegacyParityTabularSource(glXlsxPath, "LedgerPart1", 2),
                new LegacyParityTabularSource(glCsvPath, null, 3),
            ],
            [new LegacyParityTabularSource(tbPath, "TrialBalance", 4)],
            glMapping,
            ResolutionSources(glMapping),
            GlAmountMode.SignedAmount,
            tbMapping,
            ResolutionSources(tbMapping),
            tbChangeMode,
            2,
            new LegacyAccountMappingProfile(accountMappingPath, "AccountMapping", 1),
            [new DateOnly(2031, 1, 1), new DateOnly(2031, 2, 28)],
            [new DateOnly(2031, 2, 8)],
            3,
            scenarios,
            new Dictionary<LegacyReportKind, string>(),
            [new LegacyScenarioCount(Position: 1, VoucherCount: 0, RowCount: 0)],
            [],
            decidedInputs);
    }

    private static IReadOnlyDictionary<string, LegacyMappingResolutionSource> ResolutionSources(
        IReadOnlyDictionary<string, string> mapping) =>
        mapping.Keys.ToDictionary(
            key => key,
            _ => LegacyMappingResolutionSource.WorkingPaperExplicitMapping,
            StringComparer.Ordinal);

    private static void WriteRows(string path, string sheetName, int rowCount)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(sheetName);
        sheet.Cell(1, 1).Value = "synthetic-column";
        for (var index = 0; index < rowCount; index++)
        {
            sheet.Cell(index + 2, 1).Value = $"row-{index + 1}";
        }
        workbook.SaveAs(path);
    }

    private static void AssertReferenceWorkbookIsOwned(
        LegacyParityWorkspace workspace,
        LegacyAuditParityReferenceFile reference)
    {
        var root = Path.GetFullPath(workspace.Path) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(reference.FilePath);
        Assert.StartsWith(root, path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
        Assert.Equal(".xlsx", Path.GetExtension(path), ignoreCase: true);
    }
}
