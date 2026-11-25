using Xunit;

namespace JET.Tests.Architecture;

public sealed class ReportExportOwnershipTests
{

    private static readonly System.Reflection.Assembly ProductionAssembly =
        typeof(global::JET.Domain.JetActionException).Assembly;

    [Theory]
    [InlineData(
        "ExportValidationReportsHandler",
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

        // AuditCore 不提供只轉手給 port 的 ExecuteAsync，handler 直接呼叫
        // ReportExecutionPort.ExecuteAsync。這裡檢查這個呼叫，
        // 並要求 Plan、建立 port、執行與 Finalize 依序出現，守住同一個 AuditCore 生命週期。
        AssertAppearsInOrder(
            handler,
            "JetAuditProgram.Plan(",
            "new ReportExecutionPort(reportStore,",
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
        // 範本是工作檔，不走報告 plan 與報告 store，也不進 manifest。
        var handler = ReadTypeSource(
            "ExportAccountMappingTemplateHandler",
            "Application/Handlers/ExportAccountMappingTemplateHandler.cs");

        Assert.DoesNotContain("JetAuditProgram.Plan(", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("ReportExecutionPort", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("IReportStore", handler, StringComparison.Ordinal);
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
            "JET.Application.ReportExecutionPort",
            "JET.Domain.IInfReportWriter",
            "JET.Domain.ICriteriaSelectionReportWriter"
        };

        foreach (var name in expected)
        {
            var type = ProductionAssembly.GetType(name);

            Assert.NotNull(type);
            Assert.False(type!.IsVisible);
        }
    }

    [Fact]
    public void ExportTemporaryFiles_OpenOnlyThroughExportSpoolFile()
    {
        // 匯出放在 Windows 暫存資料夾、關閉即刪的暫存檔，只能從同一個地方建立與放掉：
        // 放掉時不丟出新的錯誤、檔案一定關閉刪除，這個做法只寫在一處。
        var product = ProductSources.ProductRoot;
        var openers = Directory
            .EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(product, file).Replace('\\', '/'))
            .Where(relative => relative.Split('/')[0] is not ("bin" or "obj"))
            .Where(relative => File.ReadAllText(Path.Combine(product, relative))
                .Contains("FileOptions.DeleteOnClose", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Infrastructure/Export/ExportSpoolFile.cs" }, openers);
    }

    [Fact]
    public void OpenXmlWriterCreate_IsCalledOnlyByWorkingPaperWriter()
    {
        // 報告的工作表經 FormalWorkbookArchive.OpenWorksheetWriter 寫出，寫法和 Excel 存檔時相同。
        // OpenXmlWriter.Create 寫出的 XML 帶 BOM、主要命名空間一律加前綴 x，所以產品程式不直接用它。
        // 工作底稿的輸出維持原本的寫法，續頁與改寫封面這兩處照舊使用。
        var product = ProductSources.ProductRoot;
        var callers = Directory
            .EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(product, file).Replace('\\', '/'))
            .Where(relative => relative.Split('/')[0] is not ("bin" or "obj"))
            .Where(relative => File.ReadAllText(Path.Combine(product, relative))
                .Contains("OpenXmlWriter.Create(", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "Infrastructure/Export/WorkingPaperWriter.Template.cs",
                "Infrastructure/Export/WorkingPaperWriter.cs"
            },
            callers);
    }

    [Fact]
    public void WorksheetContentStream_IsOpenedOnlyForWorkingPaperWriters()
    {
        // FormalWorkbookArchive.OpenWorksheetContent 回傳工作表內容的原始串流，寫進去的 XML 不經過 SpreadsheetXmlWriter：
        // 不會數共用字串的格數，也不保證沒有 BOM 與前綴。報告的工作表一律用 OpenWorksheetWriter，
        // 只有維持原本寫法的工作底稿直接開這個串流：續頁，以及在範本工作表上加列的 overlay。
        // 一般的 XmlWriter.Create 也一樣，只有 SpreadsheetXmlWriter 本身與底稿的 overlay 使用。
        var product = ProductSources.ProductRoot;
        var sources = Directory
            .EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(product, file).Replace('\\', '/'))
            .Where(relative => relative.Split('/')[0] is not ("bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .Select(relative => (Path: relative, Text: File.ReadAllText(Path.Combine(product, relative))))
            .ToArray();
        var plainXmlWriter = new System.Text.RegularExpressions.Regex(
            @"(?<![A-Za-z0-9_])XmlWriter\.Create\(",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        Assert.Equal(
            new[]
            {
                "Infrastructure/Export/DirectTemplateWorksheetOverlay.cs",
                "Infrastructure/Export/WorkingPaperWriter.cs"
            },
            sources
                .Where(source => source.Text.Contains("FormalWorkbookArchive.OpenWorksheetContent(", StringComparison.Ordinal))
                .Select(source => source.Path)
                .ToArray());
        Assert.Equal(
            new[]
            {
                "Infrastructure/Export/DirectTemplateWorksheetOverlay.cs",
                "Infrastructure/Export/SpreadsheetXmlWriter.cs"
            },
            sources
                .Where(source => plainXmlWriter.IsMatch(source.Text))
                .Select(source => source.Path)
                .ToArray());
    }

    [Fact]
    public void AuditCoreReportPort_DoesNotCarryContentWriterDelegates()
    {
        var source = ReadProduct("AuditCore", "ReportExportProgram.cs");

        Assert.DoesNotContain("ReportWriteRequest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReportContentWriter", source, StringComparison.Ordinal);
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
