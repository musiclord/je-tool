using Xunit;

namespace JET.Tests.Architecture;

public sealed class PrescreenCriteriaDirectTemplateArchitectureTests
{
    [Fact]
    public void ProductionWriters_UseStreamingDirectFillWithoutGeneratedWorkbookMerge()
    {
        var sources = new[]
        {
            ReadProduct(
                "Infrastructure",
                "Export",
                "LegacyReportWriter.Prescreen.cs"),
            ReadProduct(
                "Infrastructure",
                "Export",
                "LegacyReportWriter.Criteria.cs")
        };

        Assert.All(sources, source =>
        {
            Assert.Contains(
                "ReportTemplatePackage.FillDirectStreamingAsync",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "ReportTemplatePackage.FillAsync",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "TemplateWorkbookMerger",
                source,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void PrescreenDetailReads_AreGatedOnlyByFinalizedPlanEmitDecision()
    {
        var source = ReadProduct(
            "Infrastructure",
            "Export",
            "LegacyReportWriter.Prescreen.cs");
        var detailPath = Slice(
            source,
            "private async Task EmitPrescreenRuleFromPlanAsync(",
            // 切片終點是下一個方法，範圍只有明細寫出這一段。
            "private async Task EmitPreparerSummaryAsync(");

        Assert.DoesNotContain(
            "GetCountsAsync",
            source,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(source, "prescreenPages.GetPageAsync("));
        AssertAppearsInOrder(
            detailPath,
            "var detail = plan.RequireDetail(kind);",
            "if (!detail.Emit)",
            "return;",
            "await EmitRawPagedSheetsAsync(",
            "prescreenPages.GetPageAsync(");
        Assert.DoesNotContain(
            "RowHitCount",
            detailPath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SummaryOnly",
            detailPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CriteriaDetailReads_UseSelectedScenarioVoucherPopulationWithoutLegacyGate()
    {
        var source = ReadProduct(
            "Infrastructure",
            "Export",
            "LegacyReportWriter.Criteria.cs");
        var writerSource = ReadProduct(
            "Infrastructure",
            "Export",
            "LegacyReportWriter.cs");
        // 明細頁有一欄 Final_TAG，讀明細的函式連同每一列是否符合條件一起回傳，
        // 切段的起點是這個回傳型別。
        var exportPath = Slice(
            source,
            "private async Task<IReadOnlyList<SheetStat>> FillCriteriaTemplateAsync(",
            "private async Task<PageResult<CriteriaVoucherRow>> ReadCriteriaVoucherRowsAsync(");
        var detailPath = Slice(
            source,
            "private async Task<PageResult<CriteriaVoucherRow>> ReadCriteriaVoucherRowsAsync(",
            "private static void EditCriteriaSummary(");

        AssertAppearsInOrder(
            detailPath,
            "var population = new GlPopulationContext(",
            "highRiskSummaryRows.GetPageAsync(");
        Assert.Contains(
            "[scenarioPosition]",
            detailPath,
            StringComparison.Ordinal);

        // Legacy JET Step4 applies the <= 1,000,000 gate only
        // to EntireColumn.AutoFit. It must never truncate or suppress detail reads.
        Assert.Equal(1, CountOccurrences(writerSource, "1_000_000"));
        AssertAppearsInOrder(
            exportPath,
            "await EmitRawPagedSheetsAsync(",
            "ReadCriteriaVoucherRowsAsync(",
            "RawSheetAutoFitMode.LegacyFirstTwentyWhenAtMostOneMillion,");
        // 10 點明細頁統一 15 點，列高由 LegacyReportStyles.ExportDatabaseRowHeight 提供；
        // 守衛核對的是這一段呼叫順序。
        AssertAppearsInOrder(
            writerSource,
            "var page = await fetchPage(",
            "spool.Append(values);",
            "spool.Complete();",
            "var resolvedAutoFitMode = ResolveRawSheetAutoFitMode(",
            "autoFitMode,",
            "spooledRows);",
            "OpenRawSheet(",
            "resolvedAutoFitMode,",
            "headerStyles,",
            "appearanceMode == RawSheetAppearanceMode.ExportDatabase ? LegacyReportStyles.ExportDatabaseRowHeight : null);");
        Assert.Contains(
            "spooledRows <= LegacyCriteriaAutoFitMaximumRows",
            writerSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RowHitCount",
            exportPath,
            StringComparison.Ordinal);

        foreach (var legacyGate in new[]
                 {
                     "1_000_000",
                     "1,000,000",
                     "10_000",
                     "10,000"
                 })
        {
            Assert.DoesNotContain(
                legacyGate,
                detailPath,
                StringComparison.Ordinal);
        }
        Assert.DoesNotContain("Take(", detailPath, StringComparison.Ordinal);
    }

    [Fact]
    public void PrescreenHandler_ExecutesFinalizedPlanThroughPlannedWriter()
    {
        var handler = ReadTypeSource(
            "ExportPrescreenReportHandler",
            "Application/Handlers/ExportReportHandlers.cs");
        var lifecycle = Slice(
            handler,
            "var unfinalizedReportPlan = JetAuditProgram.Plan(",
            "var request = new ReportWriteRequest(");

        // 報告寫出器只有一個正式入口，handler 不依型別挑路徑；確認它把 Finalize 後的計畫交給寫出器。
        Assert.Contains(
            "writer.WriteAsync(",
            handler,
            StringComparison.Ordinal);
        Assert.Contains(
            "prescreenReportPlan,",
            handler,
            StringComparison.Ordinal);
        // planning port 不是 handler 欄位，而是從作用中案件資料庫組取出的區域變數
        // prescreenReportPlanningFactsPort；另外確認它確實取自資料庫組的 PrescreenReportPlanningFacts。
        Assert.Contains(
            "prescreenReportPlanningFactsPort",
            lifecycle,
            StringComparison.Ordinal);
        Assert.Matches(
            @"IPrescreenReportPlanningFactsPort prescreenReportPlanningFactsPort =\s+repositories\.PrescreenReportPlanningFacts;",
            handler);
        // AuditCore 不提供只轉手給 port 的 ExecuteAsync；中間一項是直接呼叫 planning port，
        // 仍要求 Plan、執行、Finalize 依序出現。
        AssertAppearsInOrder(
            lifecycle,
            "JetAuditProgram.Plan(",
            "prescreenReportPlanningFactsPort.ExecuteAsync(",
            "JetAuditProgram.Finalize(");
        Assert.Equal(
            3,
            CountOccurrences(lifecycle, "unfinalizedReportPlan"));
    }

    private static void AssertAppearsInOrder(
        string source,
        params string[] fragments)
    {
        var offset = 0;
        foreach (var fragment in fragments)
        {
            var index = source.IndexOf(fragment, offset, StringComparison.Ordinal);
            Assert.True(
                index >= 0,
                $"找不到預期 source fragment：{fragment}");
            offset = index + fragment.Length;
        }
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while (true)
        {
            var index = source.IndexOf(value, offset, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            offset = index + value.Length;
        }
    }

    private static string Slice(
        string source,
        string startMarker,
        string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 source 起點：{startMarker}");
        var end = source.IndexOf(
            endMarker,
            start + startMarker.Length,
            StringComparison.Ordinal);
        Assert.True(end > start, $"找不到 source 終點：{endMarker}");
        return source[start..end];
    }

    private static string ReadTypeSource(string typeName, string relativePath)
    {
        var source = File.ReadAllText(
            Path.Combine(
                new[] { JetRoot(), "JET" }
                    .Concat(relativePath.Split('/'))
                    .ToArray()));
        var marker = $"public sealed class {typeName}";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 production handler '{typeName}'。");
        var next = source.IndexOf(
            "public sealed class ",
            start + marker.Length,
            StringComparison.Ordinal);

        return next < 0 ? source[start..] : source[start..next];
    }

    private static string ReadProduct(params string[] segments) =>
        File.ReadAllText(
            Path.Combine(new[] { JetRoot(), "JET" }.Concat(segments).ToArray()));

    private static string JetRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
