using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using JET.Tests.TestInfrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed partial class SixReportWorkflowJourneyTests
{
    // Excel 路線把這個變數設成本次執行的資料夾，再用原生 Excel 開啟這裡寫出的六份工作簿。
    private const string EvidenceDirectoryVariable = "JET_SIX_REPORT_EVIDENCE_DIR";

    private static readonly string[] EvidenceKinds =
    [
        "accountMapping",
        "criteriaSelectionReport",
        "infReport",
        "prescreenReport",
        "validationReport",
        "workingPaper",
    ];

    /// <summary>
    /// 走完五份報告與科目配對範本工作檔的旅程，把六份工作簿以種類命名複製到佐證資料夾，
    /// 並寫出 evidence-manifest.json，記下每份的大小、SHA-256 與工作表名稱。
    /// Excel 路線設定佐證資料夾時，另用電腦上的 Office 建立 .xls、.mdb 與 .accdb 檔，核對匯入流程；
    /// 其他路線沒有設定，工作簿寫在測試主機的資料夾裡，只核對匯出的內容。
    /// </summary>
    [Fact]
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
    public async Task AccountMappingHandoff_ExportsSixWorkbooksWithManifest()
    {
        var requestedDirectory = Environment.GetEnvironmentVariable(EvidenceDirectoryVariable);
        using var host = new HandlerTestHost();
        var journey = await RunAccountMappingHandoffAsync(host);
        var evidenceDirectory = string.IsNullOrWhiteSpace(requestedDirectory)
            ? Path.Combine(host.ProjectsRoot, "six-report-evidence")
            : requestedDirectory;
        Assert.True(
            Path.IsPathFullyQualified(evidenceDirectory),
            $"{EvidenceDirectoryVariable} must be an absolute path.");
        if (!string.IsNullOrWhiteSpace(requestedDirectory))
        {
            await NativeAccessImportAcceptance.VerifyAsync(evidenceDirectory);
        }

        ExportEvidence(journey, Path.Combine(host.ProjectsRoot, journey.ProjectId), evidenceDirectory);

        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(evidenceDirectory, "evidence-manifest.json")));
        Assert.Equal(journey.ProjectId, manifest.RootElement.GetProperty("projectId").GetString());
        var artifacts = manifest.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.Equal(EvidenceKinds, artifacts.Select(artifact => artifact.GetProperty("kind").GetString()).ToArray());
        Assert.All(artifacts, artifact =>
        {
            var path = Path.Combine(evidenceDirectory, "workbooks", artifact.GetProperty("evidenceFileName").GetString()!);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(bytes.LongLength, artifact.GetProperty("bytes").GetInt64());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                artifact.GetProperty("sha256").GetString());
            Assert.NotEmpty(artifact.GetProperty("sheets").EnumerateArray());
        });
    }

    private static void ExportEvidence(
        AccountMappingHandoffJourney journey,
        string projectDirectory,
        string evidenceDirectory)
    {
        var workbookDirectory = Path.Combine(evidenceDirectory, "workbooks");
        if (Directory.Exists(workbookDirectory) || File.Exists(workbookDirectory))
        {
            throw new InvalidOperationException(
                $"Evidence workbook directory already exists: {workbookDirectory}");
        }

        Directory.CreateDirectory(workbookDirectory);
        // 六份工作簿是五份報告加上科目配對範本工作檔，範本以 accountMapping 為種類名。
        var sources = journey.Reports
            .Select(report => (
                Kind: report.GetProperty("kind").GetString()!,
                FileName: report.GetProperty("fileName").GetString()!,
                SourcePath: Path.Combine(projectDirectory, report.GetProperty("fileName").GetString()!)))
            .Append((
                Kind: "accountMapping",
                FileName: Path.GetFileName(journey.AccountMappingTemplatePath),
                SourcePath: journey.AccountMappingTemplatePath))
            .OrderBy(source => source.Kind, StringComparer.Ordinal);
        var evidence = sources
            .Select(source =>
            {
                var evidenceFileName = $"{source.Kind}.xlsx";
                var evidencePath = Path.Combine(workbookDirectory, evidenceFileName);
                File.Copy(source.SourcePath, evidencePath);

                using var stream = File.OpenRead(evidencePath);
                var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                using var workbook = new XLWorkbook(evidencePath);
                return new
                {
                    kind = source.Kind,
                    sourceFileName = source.FileName,
                    evidenceFileName,
                    bytes = stream.Length,
                    sha256,
                    sheets = workbook.Worksheets.Select(sheet => sheet.Name).ToArray()
                };
            })
            .ToArray();

        File.WriteAllText(
            Path.Combine(evidenceDirectory, "evidence-manifest.json"),
            JsonSerializer.Serialize(
                new
                {
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    projectId = journey.ProjectId,
                    artifacts = evidence
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }
}
