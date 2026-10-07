using Xunit;

namespace JET.Tests.Architecture;

public sealed class ReportExportOwnershipTests
{
    [Fact]
    public void K15_CalendarTemplates_AreKeepExistingWorkFilesNotReports()
    {
        var handler = ReadTypeSource("ExportCalendarTemplatesHandler", "Application/Handlers/ExportCalendarTemplatesHandler.cs");
        Assert.Contains("ProjectWorkFileWriter.WriteAsync(", handler, StringComparison.Ordinal);
        Assert.Contains("onlyIfMissing: true", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("ReportArtifactExecutionPort", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Import", handler, StringComparison.Ordinal);
    }

    private static readonly System.Reflection.Assembly ProductionAssembly =
        typeof(global::JET.Domain.JetActionException).Assembly;

    [Theory]
    [InlineData(
        "ExportValidationArtifactsHandler",
        "Application/Handlers/ExportReportHandlers.cs")]
    [InlineData(
        "ExportPrescreenReportHandler",
        "Application/Handlers/ExportReportHandlers.cs")]
    [InlineData(
        "ExportCriteriaSelectionReportHandler",
        "Application/Handlers/ExportReportHandlers.cs")]
    public void ProductionReportHandler_UsesTypedLifecycle(
        string handlerName,
        string relativePath)
    {
        var handler = ReadTypeSource(handlerName, relativePath);

        // 2026-10-02 起 AuditCore 不再提供只轉手給 port 的 ExecuteAsync，handler 直接呼叫
        // ReportArtifactExecutionPort.ExecuteAsync。原本檢查 JetAuditProgram.ExecuteAsync( 的斷言改成檢查這個呼叫，
        // 並要求 Plan、建立 port、執行與 Finalize 依序出現，守住同一個 AuditCore 生命週期。
        AssertAppearsInOrder(
            handler,
            "JetAuditProgram.Plan(",
            "new ReportArtifactExecutionPort(artifactStore,",
            ".ExecuteAsync(plan, cancellationToken);",
            "JetAuditProgram.Finalize(plan, facts)");
    }

    private static void AssertAppearsInOrder(string source, params string[] fragments)
    {
        var offset = 0;
        foreach (var fragment in fragments)
        {
            var index = source.IndexOf(fragment, offset, StringComparison.Ordinal);
            Assert.True(index >= 0, $"找不到預期 source fragment：{fragment}");
            offset = index + fragment.Length;
        }
    }

    [Fact]
    public void AccountMappingTemplateHandler_WritesWorkFileOutsideReportLifecycle()
    {
        // 2026-09-02 裁定：範本是工作檔，不走報告 plan／artifact store，也不進 manifest。
        var handler = ReadTypeSource(
            "ExportAccountMappingTemplateHandler",
            "Application/Handlers/ExportAccountMappingTemplateHandler.cs");

        Assert.DoesNotContain("JetAuditProgram.Plan(", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("ReportArtifactExecutionPort", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("IReportArtifactStore", handler, StringComparison.Ordinal);
        Assert.Contains("ProjectWorkFileWriter.WriteAsync(", handler, StringComparison.Ordinal);
        Assert.Contains("ProjectFileNames.AccountMappingTemplate(", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedReportExportSurface_StaysInternal()
    {
        var expected = new[]
        {
            "JET.AuditCore.ReportExportRequest",
            "JET.AuditCore.ReportExportPlan",
            "JET.AuditCore.ReportExportFacts",
            "JET.AuditCore.ReportExportResult",
            "JET.AuditCore.IReportExportFactsPort",
            "JET.Application.ReportArtifactExecutionPort",
            "JET.Domain.ITypedCriteriaSelectionReportWriter"
        };

        foreach (var name in expected)
        {
            var type = ProductionAssembly.GetType(name);

            Assert.NotNull(type);
            Assert.False(type!.IsVisible);
        }
    }

    [Fact]
    public void AuditCoreReportPort_DoesNotCarryContentWriterDelegates()
    {
        var source = ReadProduct("AuditCore", "ReportExportProgram.cs");

        Assert.DoesNotContain("ReportArtifactWriteRequest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReportArtifactContentWriter", source, StringComparison.Ordinal);
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
