using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.AuditCore;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyAuditParityJourneyTests
{
    private static IReadOnlyList<string> FullActionOrder =>
        LegacyAuditParityJourneyContract.FullActionOrder;

    [Theory]
    [InlineData((int)LegacyAuditParityCheckpoint.Profile, 0)]
    // 科目配對檔在確認配對之後才匯入（原因見 LegacyAuditParityJourney），所以它不算在匯入這一段，
    // 而是算在確認配對這一段。
    [InlineData((int)LegacyAuditParityCheckpoint.Import, 6)]
    [InlineData((int)LegacyAuditParityCheckpoint.Mapping, 9)]
    [InlineData((int)LegacyAuditParityCheckpoint.Validate, 11)]
    [InlineData((int)LegacyAuditParityCheckpoint.Prescreen, 14)]
    [InlineData((int)LegacyAuditParityCheckpoint.Filter, 19)]
    [InlineData((int)LegacyAuditParityCheckpoint.Export, 21)]
    public async Task SyntheticJourney_StopsAtCheckpointAndUsesFormalActionPrefix(
        int checkpointValue,
        int expectedActionCount)
    {
        var checkpoint = (LegacyAuditParityCheckpoint)checkpointValue;
        using var fixture = SyntheticJourneyFixture.Create();
        using var host = new HandlerTestHost();

        var result = await LegacyAuditParityJourney.RunAsync(
            host,
            fixture.Input,
            LegacyAuditParityProvider.Sqlite,
            checkpoint);

        Assert.Equal(checkpoint, result.CompletedCheckpoint);
        Assert.Equal(FullActionOrder.Take(expectedActionCount), result.ActionNames);
        Assert.All(result.ActionNames, action => Assert.Contains(action, FullActionOrder));

        if (checkpoint == LegacyAuditParityCheckpoint.Export)
        {
            Assert.Equal(Enum.GetValues<LegacyReportKind>(), result.Reports.Select(report => report.Kind));
            Assert.All(result.Reports, report => Assert.True(File.Exists(report.FullPath)));
            Assert.True(result.ValidationCaptured);
            Assert.True(result.PrescreenCaptured);
            Assert.True(result.FilterCaptured);
            Assert.True(result.HighRiskSummaryCaptured);
            Assert.NotNull(result.Observation);
            var serialized = JsonSerializer.Serialize(result);
            Assert.DoesNotContain(fixture.Input.ClientName, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(
                fixture.Input.GlSources[0].FilePath,
                serialized,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                fixture.Input.FilterScenarios.GetRawText(),
                serialized,
                StringComparison.Ordinal);
        }
    }

    // 第一次流程匯出六份報告之後，在同一個案件、同一個測試主機上
    // 依序重新開啟、重新確認 GL 配對、重新匯入 GL，每段都重新匯出六份報告。合成資料沒有舊版標準底稿，
    // 改拿第一次 JET 匯出的副本當基準，用私人案件同一個比較器與政策比；合成資料沒有內容裁決，傳空清單。
    // 情境的列數與張數、資料可靠性抽樣的母體與筆數也要和第一次相同。這段程式因此不靠私人資料也能驗證。
    [Theory]
    [InlineData((int)LegacyAuditParityProvider.Sqlite)]
    public async Task SyntheticJourney_FollowUpsAfterExport_ReproduceFirstReportsAndCounts(int providerValue)
    {
        var provider = (LegacyAuditParityProvider)providerValue;
        using var fixture = SyntheticJourneyFixture.Create();

        var completedFollowUps = await PrivateCaseRunScope.RunAsync(async (scope, token) =>
        {
            var followUpsDone = new List<LegacyAuditParityFollowUp>();
            var first = await LegacyAuditParityJourney.RunAsync(
                scope.Host,
                fixture.Input,
                provider,
                LegacyAuditParityCheckpoint.Export,
                cancellationToken: token,
                afterSuccessfulJourneyBeforeCleanup: async completed =>
                {
                    // 後一段的匯出會覆蓋同名報告，所以每段都先複製一份再往下做。
                    var baseline = CopyReports(scope, completed, segmentIndex: 0);
                    for (var index = 0; index < PrivateCaseAcceptanceRunner.FollowUps.Count; index++)
                    {
                        var followUp = PrivateCaseAcceptanceRunner.FollowUps[index];
                        var segment = await LegacyAuditParityJourney.RunFollowUpAsync(
                            scope.Host,
                            fixture.Input,
                            provider,
                            completed.ProjectId!,
                            followUp,
                            token);

                        Assert.Equal(
                            LegacyAuditParityJourneyContract.FollowUpActionOrder(followUp),
                            LegacyAuditParityJourneyContract.NormalizePaginatedActionOrder(segment.ActionNames));
                        Assert.Equal(Enum.GetValues<LegacyReportKind>(), segment.Reports.Select(report => report.Kind).Order());
                        var comparison = PrivateCaseReportComparator.Compare(
                            PrivateCaseReportPairSet.CreateFromJetBaseline(
                                baseline,
                                CopyReports(scope, segment, segmentIndex: index + 1)),
                            PrivateCaseAcceptancePolicy.Current,
                            []);
                        Assert.True(comparison.Passed, DescribeFailedReports(followUp, comparison));
                        Assert.Equal(
                            completed.ScenarioVerificationFacts!.Counts,
                            segment.ScenarioVerificationFacts!.Counts);
                        Assert.Equal(completed.InfVerificationFacts, segment.InfVerificationFacts);
                        if (followUp == LegacyAuditParityFollowUp.Reimport)
                        {
                            Assert.Equal(completed.GlImportedRowCount, segment.GlImportedRowCount);
                        }
                        if (followUp != LegacyAuditParityFollowUp.Reopen)
                        {
                            Assert.Equal(completed.GlProjectedRowCount, segment.GlProjectedRowCount);
                        }
                        followUpsDone.Add(followUp);
                    }
                });

            Assert.Equal(LegacyAuditParityCheckpoint.Export, first.CompletedCheckpoint);
            return followUpsDone;
        });

        Assert.Equal(PrivateCaseAcceptanceRunner.FollowUps, completedFollowUps);
    }

    private static PrivateCaseActualReportSet CopyReports(
        PrivateCaseRunScope scope,
        LegacyAuditParityJourneyResult segment,
        int segmentIndex) =>
        PrivateCaseActualReportSet.Create(
            segment.Reports,
            Path.GetFullPath(segment.ProjectId!, scope.Host.ProjectsRoot),
            scope.InputWorkspace,
            segmentIndex: segmentIndex);

    private static string DescribeFailedReports(
        LegacyAuditParityFollowUp followUp,
        PrivateCaseReportComparisonResult comparison) =>
        $"{followUp}: "
        + string.Join(
            ",",
            comparison.Reports
                .Where(static report => !report.Passed)
                .Select(static report =>
                    $"{report.Kind}[content={report.BlockingContentDifferenceCount};"
                    + string.Join("+", report.BlockingContentDifferences.Select(static difference =>
                        $"{difference.Dimension}:{difference.DifferenceCount}"))
                    + $";appearance={report.UnclassifiedAppearanceDifferenceCount};"
                    + string.Join("+", report.UnclassifiedAppearanceDifferences.Select(static difference =>
                        $"{difference.ScopeKind}.{difference.Property}:{difference.DifferenceCount}"))
                    + "]"));

    [Fact]
    public async Task SyntheticJourney_ProjectCreatePinsExplicitTestOnlySampleSeed()
    {
        using var fixture = SyntheticJourneyFixture.Create();
        using var host = new HandlerTestHost();

        var result = await LegacyAuditParityJourney.RunAsync(
            host,
            fixture.Input,
            LegacyAuditParityProvider.Sqlite,
            LegacyAuditParityCheckpoint.Import);

        Assert.NotNull(result.ProjectId);
        var projectPath = Path.Combine(host.ProjectsRoot, result.ProjectId, "project.json");
        var project = JsonNode.Parse(File.ReadAllText(projectPath))!.AsObject();
        Assert.Equal(fixture.Input.SampleSeed, project["sampleSeed"]!.GetValue<long>());
        Assert.Equal(
            JetAuditProgram.CurrentInfSamplingAlgorithmVersion,
            project["sampleSeedVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task SyntheticJourney_DecidedNotProvidedAuthorizedPreparer_SkipsFictitiousImport()
    {
        using var fixture = SyntheticJourneyFixture.Create();
        using var host = new HandlerTestHost();
        var input = fixture.Input with { AuthorizedPreparerFile = null };

        var result = await LegacyAuditParityJourney.RunAsync(
            host,
            input,
            LegacyAuditParityProvider.Sqlite,
            LegacyAuditParityCheckpoint.Import);

        Assert.Equal(
            new[]
            {
                "project.create",
                "import.gl.fromFile",
                "import.tb.fromFile",
                "import.holiday.fromFile",
                "import.makeupDay.fromFile",
            },
            result.ActionNames);
        Assert.DoesNotContain("import.authorizedPreparer.fromFile", result.ActionNames);
    }

    [Fact]
    public void JourneyRunAsync_DoesNotOwnWorkspaceRetentionSwitch()
    {
        var method = typeof(LegacyAuditParityJourney).GetMethod(
            nameof(LegacyAuditParityJourney.RunAsync),
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Legacy parity journey entry point was not found.");

        Assert.DoesNotContain("keepOutputs", method.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public async Task SyntheticJourney_MissingReferenceFileFailsWithFieldIdWithoutRawPath()
    {
        using var fixture = SyntheticJourneyFixture.Create();
        using var host = new HandlerTestHost();
        var removedPath = fixture.Input.AuthorizedPreparerFile!.FilePath;
        File.Delete(removedPath);

        var error = await Assert.ThrowsAsync<LegacyAuditParityJourneyCompletenessException>(() =>
            LegacyAuditParityJourney.RunAsync(
                host,
                fixture.Input,
                LegacyAuditParityProvider.Sqlite,
                LegacyAuditParityCheckpoint.Import));

        Assert.Equal("authorizedPreparerFile", error.FieldId);
        Assert.DoesNotContain(removedPath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Input.ClientName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyInput_ToStringDoesNotRenderPayloadOrPaths()
    {
        using var fixture = SyntheticJourneyFixture.Create();

        var rendered = fixture.Input.ToString();

        Assert.Contains("case-A", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Input.ClientName, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Input.GlSources[0].FilePath, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Input.FilterScenarios.GetRawText(), rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyInput_JsonSerializationDoesNotRenderPayloadOrPaths()
    {
        using var fixture = SyntheticJourneyFixture.Create();

        var rendered = JsonSerializer.Serialize(fixture.Input);

        Assert.Equal("{}", rendered);
        Assert.DoesNotContain(fixture.Input.ClientName, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Input.GlSources[0].FilePath, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Input.FilterScenarios.GetRawText(), rendered, StringComparison.Ordinal);
    }

    internal sealed class SyntheticJourneyFixture : IDisposable
    {
        private readonly List<string> _paths;

        private SyntheticJourneyFixture(LegacyAuditParityJourneyInput input, List<string> paths)
        {
            Input = input;
            _paths = paths;
        }

        internal LegacyAuditParityJourneyInput Input { get; }

        internal static SyntheticJourneyFixture Create()
        {
            var paths = new List<string>();
            var glPath = Add(paths, TestWorkbookBuilder.WriteWorkbook(sheet =>
            {
                var headers = new[]
                {
                    "傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱",
                    "摘要", "建立人員", "金額", "借方旗標",
                };
                for (var column = 0; column < headers.Length; column++)
                {
                    sheet.Cell(1, column + 1).Value = headers[column];
                }

                sheet.Cell(2, 1).Value = "SYN-001";
                sheet.Cell(2, 2).Value = "2025-03-05";
                sheet.Cell(2, 3).Value = "2025-03-05";
                sheet.Cell(2, 4).Value = "1101";
                sheet.Cell(2, 5).Value = "合成資產";
                sheet.Cell(2, 6).Value = "合成調整";
                sheet.Cell(2, 7).Value = "synthetic-user";
                sheet.Cell(2, 8).Value = 100m;
                sheet.Cell(2, 9).Value = "1";

                sheet.Cell(3, 1).Value = "SYN-001";
                sheet.Cell(3, 2).Value = "2025-03-05";
                sheet.Cell(3, 3).Value = "2025-03-05";
                sheet.Cell(3, 4).Value = "4101";
                sheet.Cell(3, 5).Value = "合成收入";
                sheet.Cell(3, 6).Value = "合成調整";
                sheet.Cell(3, 7).Value = "synthetic-user";
                sheet.Cell(3, 8).Value = 100m;
                sheet.Cell(3, 9).Value = "0";
            }));

            var tbPath = Add(paths, TestWorkbookBuilder.WriteWorkbook(sheet =>
            {
                sheet.Cell(1, 1).Value = "科目代號";
                sheet.Cell(1, 2).Value = "科目名稱";
                sheet.Cell(1, 3).Value = "變動金額";
                sheet.Cell(2, 1).Value = "1101";
                sheet.Cell(2, 2).Value = "合成資產";
                sheet.Cell(2, 3).Value = 100m;
                sheet.Cell(3, 1).Value = "4101";
                sheet.Cell(3, 2).Value = "合成收入";
                sheet.Cell(3, 3).Value = -100m;
            }));

            var accountMappingPath = Add(paths, TestWorkbookBuilder.WriteWorkbook(sheet =>
            {
                sheet.Cell(1, 1).Value = "GL_NUMBER";
                sheet.Cell(1, 2).Value = "GL_NAME";
                sheet.Cell(1, 3).Value = "STANDARDIZED_ACCOUNT_NAME";
                sheet.Cell(2, 1).Value = "1101";
                sheet.Cell(2, 2).Value = "合成資產";
                sheet.Cell(2, 3).Value = "Cash";
                sheet.Cell(3, 1).Value = "4101";
                sheet.Cell(3, 2).Value = "合成收入";
                sheet.Cell(3, 3).Value = "Revenue";
            }));

            var authorizedPreparerPath = Add(paths, TestWorkbookBuilder.WriteWorkbook(sheet =>
            {
                sheet.Cell(1, 1).Value = "AUTHORIZED_PREPARER";
                sheet.Cell(2, 1).Value = "synthetic-user";
            }));

            var holidayPath = Add(paths, TestWorkbookBuilder.WriteWorkbook(sheet =>
            {
                sheet.Cell(1, 2).Value = "Holiday Table";
                sheet.Cell(2, 1).Value = "Date_of_Holiday";
                sheet.Cell(2, 2).Value = "Holiday_Name";
                sheet.Cell(2, 3).Value = "IS_Holiday";
                sheet.Cell(3, 1).Value = new DateTime(2025, 1, 1);
                sheet.Cell(3, 2).Value = "synthetic holiday";
                sheet.Cell(3, 3).Value = "Y";
            }));

            var makeupDayPath = Add(paths, TestWorkbookBuilder.WriteWorkbook(sheet =>
            {
                sheet.Cell(1, 2).Value = "Makeup Day Table";
                sheet.Cell(2, 1).Value = "Date_of_MakeUpday";
                sheet.Cell(2, 2).Value = "MakeUpDay_Desc";
                sheet.Cell(3, 1).Value = new DateTime(2025, 2, 8);
                sheet.Cell(3, 2).Value = "synthetic makeup day";
            }));

            var scenarios = JsonSerializer.SerializeToElement(new object[]
            {
                new
                {
                    name = "合成條件",
                    rationale = "合成 journey",
                    groups = new object[]
                    {
                        new
                        {
                            join = "AND",
                            conditions = new object[]
                            {
                                new { join = "AND", type = "fieldValue", field = "description", @operator = "contains", values = new[] { "合成調整" } },
                            },
                        },
                    },
                },
            });

            var input = new LegacyAuditParityJourneyInput(
                CaseAlias: "case-A",
                ProjectCode: "SYNTHETIC-PARITY",
                ClientName: "synthetic parity entity",
                OperatorId: "synthetic-operator",
                PeriodStart: "2025-01-01",
                PeriodEnd: "2025-12-31",
                PrepStartDate: "2025-12-31",
                SampleSeed: 48_271,
                GlSources: [new LegacyAuditParityImportSource(glPath, "synthetic-gl.xlsx")],
                GlImportMode: "replace",
                GlMapping: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["voucherNumber"] = "傳票號碼",
                    ["glDate"] = "傳票日期",
                    ["approvalDate"] = "核准日期",
                    ["accountNumber"] = "科目代號",
                    ["accountName"] = "科目名稱",
                    ["description"] = "摘要",
                    ["createdBy"] = "建立人員",
                    ["amount"] = "金額",
                    ["drCr"] = "借方旗標",
                    ["drCrDebitCode"] = "1",
                    // Explicit two-code contract; this synthetic workbook uses 1/0.
                    ["drCrCreditCode"] = "0",
                },
                GlAmountMode: "flag",
                TbSources: [new LegacyAuditParityImportSource(tbPath, "synthetic-tb.xlsx")],
                TbImportMode: "replace",
                TbMapping: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["accountNumber"] = "科目代號",
                    ["accountName"] = "科目名稱",
                    ["changeAmount"] = "變動金額",
                },
                TbChangeMode: "direct",
                AccountMappingFile: new LegacyAuditParityReferenceFile(
                    accountMappingPath,
                    "synthetic-account-mapping.xlsx"),
                AuthorizedPreparerFile: new LegacyAuditParityReferenceFile(
                    authorizedPreparerPath,
                    "synthetic-authorized-preparer.xlsx"),
                HolidayFile: new LegacyAuditParityReferenceFile(
                    holidayPath,
                    "synthetic-holiday.xlsx"),
                MakeupDayFile: new LegacyAuditParityReferenceFile(
                    makeupDayPath,
                    "synthetic-makeup-day.xlsx"),
                FilterScenarios: scenarios,
                LegacyFilterScenarioIds: [LegacyFilterScenarioId.FromOrdinal(1)],
                ObservationCase: LegacyParityCase.CaseA);
            return new SyntheticJourneyFixture(input, paths);
        }

        public void Dispose()
        {
            foreach (var path in _paths)
            {
                TestWorkbookBuilder.Delete(path);
            }
        }

        private static string Add(List<string> paths, string path)
        {
            paths.Add(path);
            return path;
        }
    }
}
