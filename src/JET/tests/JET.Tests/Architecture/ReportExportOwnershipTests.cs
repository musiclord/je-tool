using Xunit;

namespace JET.Tests.Architecture;

public sealed class ReportExportOwnershipTests
{
    private static readonly System.Reflection.Assembly ProductionAssembly =
        typeof(global::JET.Domain.JetActionException).Assembly;

    [Theory]
    [InlineData(
        "ExportValidationArtifactsHandler",
        "Application/Handlers/ExportReportHandlers.cs")]
    [InlineData(
        "ExportAccountMappingTemplateHandler",
        "Application/Handlers/ExportAccountMappingTemplateHandler.cs")]
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

        Assert.Contains("JetAuditProgram.Plan(", handler, StringComparison.Ordinal);
        Assert.Contains("JetAuditProgram.ExecuteAsync(", handler, StringComparison.Ordinal);
        Assert.Contains("JetAuditProgram.Finalize(", handler, StringComparison.Ordinal);
        Assert.Contains(
            "new ReportArtifactExecutionPort(artifactStore,",
            handler,
            StringComparison.Ordinal);
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
