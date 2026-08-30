using System.IO.Compression;
using System.Xml.Linq;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 內嵌正式報表範本的失敗邊界。
/// oracle：缺失或無效的 xlsx 一律回報 file_read_error，且 writer 在 artifact staging
/// 階段失敗時不得取代既有正式檔或 manifest。
/// </summary>
public sealed class EmbeddedReportTemplateFailureTests
{
    private const string ProjectId = "內嵌範本失敗邊界案件";
    private const string ValidationRunId = "11111111111111111111111111111111";

    [Fact]
    public async Task MissingEmbeddedTemplate_ReportsFileReadError()
    {
        using var templates = new TemporaryDirectory();
        var catalog = new ReportTemplateCatalog(templates.Path);
        await using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            FillValidationTemplateAsync(catalog, output));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
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
                ["ValidationReport", MappingMetadataFormat.WorksheetName, WorkpaperSheetCatalog.Step13],
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
    public async Task MissingTemplateDuringReplacement_PreservesExistingArtifactAndManifest()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectDirectory = System.IO.Path.GetFullPath(folder.GetProjectDirectory(ProjectId));
        Directory.CreateDirectory(projectDirectory);
        var store = new ProjectReportArtifactStore(folder);
        var sourceRefs = new ReportArtifactSourceRefs(ValidationRunId: ValidationRunId);
        var originalBytes = "existing-validation-report"u8.ToArray();
        var original = await store.WriteAsync(
            ProjectId,
            new ReportArtifactWriteRequest(
                ReportArtifactKind.ValidationReport,
                sourceRefs,
                (output, cancellationToken) =>
                    output.WriteAsync(originalBytes, cancellationToken).AsTask()),
            CancellationToken.None);
        var artifactPath = System.IO.Path.Combine(projectDirectory, original.RelativeFileName);
        var manifestPath = System.IO.Path.Combine(
            projectDirectory,
            ProjectReportArtifactStore.ManifestFileName);
        var artifactBefore = await File.ReadAllBytesAsync(artifactPath, CancellationToken.None);
        var manifestBefore = await File.ReadAllBytesAsync(manifestPath, CancellationToken.None);

        using var templates = new TemporaryDirectory();
        var missingCatalog = new ReportTemplateCatalog(templates.Path);
        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(
            ProjectId,
            new ReportArtifactWriteRequest(
                ReportArtifactKind.ValidationReport,
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
            artifactBefore,
            await File.ReadAllBytesAsync(artifactPath, CancellationToken.None));
        Assert.Equal(
            manifestBefore,
            await File.ReadAllBytesAsync(manifestPath, CancellationToken.None));
        var current = Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None));
        Assert.Equal(original.ArtifactId, current.ArtifactId);
        Assert.Equal(original.Sha256, current.Sha256);
    }

    private static Task<ExportStats> FillValidationTemplateAsync(
        ReportTemplateCatalog catalog,
        Stream output,
        CancellationToken cancellationToken = default)
        => ReportTemplatePackage.FillDirectStreamingAsync(
            catalog,
            ReportTemplateCatalog.Validation,
            output,
            ["ValidationReport", MappingMetadataFormat.WorksheetName, WorkpaperSheetCatalog.Step13],
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
