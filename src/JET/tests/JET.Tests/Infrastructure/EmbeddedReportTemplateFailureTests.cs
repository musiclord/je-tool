using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;
using static JET.Tests.Infrastructure.WorkingPaperWriterTestSupport;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 內嵌正式報表範本的失敗邊界。
/// oracle：缺失或無效的 xlsx 一律回報 file_read_error，且 writer 在報告暫存
/// 階段失敗時不得取代既有正式檔或 manifest。
/// </summary>
public sealed class EmbeddedReportTemplateFailureTests
{
    private const string ProjectId = "內嵌範本失敗邊界案件";
    private const string ValidationResultId = "11111111111111111111111111111111";

    [Fact]
    public async Task MissingEmbeddedTemplate_ReportsFileReadError()
    {
        using var templates = new TemporaryDirectory();
        var catalog = new ReportTemplateCatalog(templates.Path);
        await using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            FillValidationTemplateAsync(catalog, output));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        // 訊息不寫「可能」，只寫審計員接下來能做什麼。
        Assert.Equal("無法讀取 JET 內附的範本「ValidationReport.xlsx」。請重新下載 JET 後再匯出。", exception.Message);
    }

    [Fact]
    public async Task CorruptedEmbeddedTemplate_ReportsFileReadError()
    {
        using var templates = new TemporaryDirectory();
        await File.WriteAllBytesAsync(
            System.IO.Path.Combine(templates.Path, ReportTemplateCatalog.Validation),
            "not-an-xlsx"u8.ToArray(),
            CancellationToken.None);
        var catalog = new ReportTemplateCatalog(templates.Path);
        await using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            FillValidationTemplateAsync(catalog, output));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        // 訊息不寫「可能」，只寫審計員接下來能做什麼。
        Assert.Equal("無法填入 JET 內附的範本「ValidationReport.xlsx」。請重新下載 JET 後再匯出。", exception.Message);
    }

    [Fact]
    public async Task DuplicateWorksheetName_ReportsFileReadErrorBeforeGeneratingContent()
    {
        using var templates = new TemporaryDirectory();
        var destination = System.IO.Path.Combine(
            templates.Path,
            ReportTemplateCatalog.Validation);
        File.Copy(
            System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "Templates",
                ReportTemplateCatalog.Validation),
            destination);
        using (var archive = ZipFile.Open(destination, ZipArchiveMode.Update))
        {
            var entry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("xl/workbook.xml"));
            XDocument workbook;
            using (var input = entry.Open())
            {
                workbook = XDocument.Load(input);
            }
            var sheets = workbook.Descendants()
                .Single(element => element.Name.LocalName == "sheets");
            sheets.Add(new XElement(sheets.Elements().First()));
            entry.Delete();
            var replacement = archive.CreateEntry("xl/workbook.xml");
            using var output = replacement.Open();
            workbook.Save(output);
        }

        var catalog = new ReportTemplateCatalog(templates.Path);
        await using var result = new MemoryStream();
        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            FillValidationTemplateAsync(catalog, result));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
    }

    [Fact]
    public async Task CaseInsensitiveDuplicateWorksheetName_ReportsFileReadError()
    {
        using var templates = new TemporaryDirectory();
        var destination = CopyValidationTemplate(templates.Path);
        RewriteWorkbookXml(destination, workbook =>
        {
            var sheets = workbook.Descendants()
                .Single(element => element.Name.LocalName == "sheets");
            var duplicate = new XElement(sheets.Elements().First());
            duplicate.SetAttributeValue("name", "validationreport");
            sheets.Add(duplicate);
        });

        var catalog = new ReportTemplateCatalog(templates.Path);
        await using var result = new MemoryStream();
        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            FillValidationTemplateAsync(catalog, result));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
    }

    [Fact]
    public async Task WorksheetWithoutRelationshipId_ReportsFileReadError()
    {
        using var templates = new TemporaryDirectory();
        var destination = CopyValidationTemplate(templates.Path);
        RewriteWorkbookXml(destination, workbook =>
        {
            XNamespace relationships =
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            var firstSheet = workbook.Descendants()
                .First(element => element.Name.LocalName == "sheet");
            Assert.NotNull(firstSheet.Attribute(relationships + "id"));
            firstSheet.Attribute(relationships + "id")!.Remove();
        });

        var catalog = new ReportTemplateCatalog(templates.Path);
        await using var result = new MemoryStream();
        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            FillValidationTemplateAsync(catalog, result));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
    }

    [Fact]
    public async Task CancellationRequestedAtDirectFillBoundary_StopsTemplateFill()
    {
        var catalog = ReportTemplateCatalog.Default;
        await using var result = new MemoryStream();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReportTemplatePackage.FillDirectStreamingAsync(
                catalog,
                ReportTemplateCatalog.Validation,
                result,
                ["ValidationReport", MappingMetadataFormat.WorksheetName, WorkingPaperSheetCatalog.Step13],
                (editor, cancellationToken) =>
                {
                    Assert.NotNull(editor);
                    cancellation.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult<IReadOnlyList<SheetStat>>([]);
                },
                cancellation.Token));
    }

    [Fact]
    public async Task MissingTemplateDuringReplacement_PreservesExistingReportAndManifest()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectDirectory = System.IO.Path.GetFullPath(folder.GetProjectDirectory(ProjectId));
        Directory.CreateDirectory(projectDirectory);
        var store = new ProjectReportStore(folder);
        var sourceRefs = new ReportSourceRefs(ValidationResultId: ValidationResultId);
        var originalBytes = "existing-validation-report"u8.ToArray();
        var original = await store.WriteAsync(
            ProjectId,
            new ReportWriteRequest(
                ReportKind.ValidationReport,
                sourceRefs,
                (output, cancellationToken) =>
                    output.WriteAsync(originalBytes, cancellationToken).AsTask()),
            CancellationToken.None);
        var reportPath = System.IO.Path.Combine(projectDirectory, original.RelativeFileName);
        var manifestPath = System.IO.Path.Combine(
            projectDirectory,
            ProjectReportStore.ManifestFileName);
        var reportBefore = await File.ReadAllBytesAsync(reportPath, CancellationToken.None);
        var manifestBefore = await File.ReadAllBytesAsync(manifestPath, CancellationToken.None);

        using var templates = new TemporaryDirectory();
        var missingCatalog = new ReportTemplateCatalog(templates.Path);
        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(
            ProjectId,
            new ReportWriteRequest(
                ReportKind.ValidationReport,
                sourceRefs,
                async (output, cancellationToken) =>
                {
                    await FillValidationTemplateAsync(
                        missingCatalog,
                        output,
                        cancellationToken);
                }),
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.Equal(
            reportBefore,
            await File.ReadAllBytesAsync(reportPath, CancellationToken.None));
        Assert.Equal(
            manifestBefore,
            await File.ReadAllBytesAsync(manifestPath, CancellationToken.None));
        var current = Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None));
        Assert.Equal(original.ReportId, current.ReportId);
        Assert.Equal(original.Bytes, current.Bytes);
        Assert.Equal(ReportFileState.AsPublished, current.FileState);
    }

    // 範本工作表的預設欄寬只有受保護工作表的續頁用得到。範本缺這個值時，沒有續頁的底稿照常匯出。
    [Fact]
    public async Task WorkingPaperTemplateWithoutDefaultColumnWidth_ExportsWhenNoProtectedSheetContinues()
    {
        using var templates = new TemporaryDirectory();
        CopyWorkingPaperTemplateWithoutDefaultColumnWidth(templates.Path);
        using var host = new HandlerTestHost();
        var project = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ConditionColumnScenarioPayload());
        var context = await CurrentContextForAsync(host, project.ProjectId);
        await using var stream = new MemoryStream();

        await WriteWithCurrentPlanAsync(
            host,
            BuildWriter(host, templatesOverride: new ReportTemplateCatalog(templates.Path)),
            stream,
            context,
            CancellationToken.None);

        using var document = OpenWorkingPaper(stream.ToArray());
        var sheets = WorksheetsByName(document);
        Assert.All(
            new[] { Step1Sheet, Step12Sheet, Step3Sheet, Step4Sheet },
            name => Assert.NotNull(sheets[name].Elements<SheetProtection>().SingleOrDefault()));
    }

    // 受保護的工作表有續頁時，續頁的欄設定要用這個值；範本缺了就回報範本無法填入。
    [Fact]
    public async Task WorkingPaperTemplateWithoutDefaultColumnWidth_ReportsFileReadErrorWhenAProtectedSheetContinues()
    {
        using var templates = new TemporaryDirectory();
        CopyWorkingPaperTemplateWithoutDefaultColumnWidth(templates.Path);
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationProjectAsync(host);
        await ImportContinuationReferencesAsync(host);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var context = await CurrentContextForAsync(host, projectId);
        await using var stream = new MemoryStream();

        var exception = await Assert.ThrowsAsync<JetActionException>(() => WriteWithCurrentPlanAsync(
            host,
            BuildWriter(
                host,
                new WorkingPaperWriterOptions(25),
                templatesOverride: new ReportTemplateCatalog(templates.Path)),
            stream,
            context,
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.Equal("無法填入 JET 內附的範本「WorkingPaper.xlsx」。請重新下載 JET 後再匯出。", exception.Message);
        Assert.Contains("缺少預設欄寬", Assert.IsType<InvalidDataException>(exception.InnerException).Message);
    }

    private static void CopyWorkingPaperTemplateWithoutDefaultColumnWidth(string destinationDirectory)
    {
        var destination = System.IO.Path.Combine(destinationDirectory, ReportTemplateCatalog.WorkingPaper);
        File.Copy(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Templates", ReportTemplateCatalog.WorkingPaper),
            destination);
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Update);
        var worksheetEntries = archive.Entries
            .Where(entry => entry.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)
                && entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .Select(entry => entry.FullName)
            .ToArray();
        var removed = 0;
        foreach (var name in worksheetEntries)
        {
            var entry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry(name));
            XDocument worksheet;
            using (var input = entry.Open())
            {
                worksheet = XDocument.Load(input);
            }
            foreach (var attribute in worksheet.Descendants()
                         .Where(element => element.Name.LocalName == "sheetFormatPr")
                         .Select(element => element.Attribute("defaultColWidth"))
                         .OfType<XAttribute>()
                         .ToArray())
            {
                attribute.Remove();
                removed++;
            }
            entry.Delete();
            using var output = archive.CreateEntry(name).Open();
            worksheet.Save(output);
        }
        Assert.True(removed > 0, "範本應該有預設欄寬可以拿掉。");
    }

    private static Task<ExportStats> FillValidationTemplateAsync(
        ReportTemplateCatalog catalog,
        Stream output,
        CancellationToken cancellationToken = default)
        => ReportTemplatePackage.FillDirectStreamingAsync(
            catalog,
            ReportTemplateCatalog.Validation,
            output,
            ["ValidationReport", MappingMetadataFormat.WorksheetName, WorkingPaperSheetCatalog.Step13],
            static (_, _) => throw new Xunit.Sdk.XunitException(
                "範本驗證失敗前不應啟動 direct writer。"),
            cancellationToken);

    private static string CopyValidationTemplate(string destinationDirectory)
    {
        var destination = System.IO.Path.Combine(
            destinationDirectory,
            ReportTemplateCatalog.Validation);
        File.Copy(
            System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "Templates",
                ReportTemplateCatalog.Validation),
            destination);
        return destination;
    }

    private static void RewriteWorkbookXml(
        string workbookPath,
        Action<XDocument> rewrite)
    {
        using var archive = ZipFile.Open(workbookPath, ZipArchiveMode.Update);
        var entry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("xl/workbook.xml"));
        XDocument workbook;
        using (var input = entry.Open())
        {
            workbook = XDocument.Load(input);
        }
        rewrite(workbook);
        entry.Delete();
        var replacement = archive.CreateEntry("xl/workbook.xml");
        using var output = replacement.Open();
        workbook.Save(output);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jet-embedded-template-failure-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // 測試暫存資料由 OS best effort 清理；不掩蓋原始 assertion。
            }
        }
    }
}
