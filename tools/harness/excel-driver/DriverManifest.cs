using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jet.ExcelDriver;

internal sealed class ExcelRunManifest
{
    public int SchemaVersion { get; init; } = 1;

    public string Scenario { get; init; } = ExcelDriverContract.Scenario;

    public required string ReportKind { get; init; }

    public string Status { get; set; } = "infrastructure_error";

    public int ExitCode { get; set; } = DriverExitCodes.InfrastructureError;

    public int TimeoutSeconds { get; init; }

    public string? OfficeVersion { get; set; }

    public ExcelOpenPolicyEvidence OpenPolicy { get; init; } = new();

    public ExcelAssertionEvidence Assertions { get; init; } = new();

    public ExcelProcessEvidence Process { get; init; } = new();

    public ExcelArtifactEvidence Artifacts { get; init; } = new();

    public ExcelCleanupEvidence Cleanup { get; init; } = new();

    public List<ExcelErrorEvidence> Errors { get; init; } = [];

    public PrivateDataEvidence PrivateData { get; init; } = new();

    internal void SetOutcome(string status, int exitCode, string? errorCode = null)
    {
        Status = status;
        ExitCode = exitCode;
        if (!string.IsNullOrWhiteSpace(errorCode)
            && Errors.All(error => !string.Equals(error.Code, errorCode, StringComparison.Ordinal)))
        {
            Errors.Add(new ExcelErrorEvidence { Code = errorCode });
        }
    }
}

internal sealed class ExcelOpenPolicyEvidence
{
    public int UpdateLinks { get; init; } = 0;

    public bool ReadOnly { get; init; } = true;

    public bool IgnoreReadOnlyRecommended { get; init; } = true;

    public bool AddToMru { get; init; }

    public int CorruptLoad { get; init; }

    public bool MacrosDisabled { get; init; } = true;
}

internal sealed class ExcelAssertionEvidence
{
    public bool SourceWorkbookExists { get; set; }

    public bool SourceWorkbookUnchanged { get; set; }

    public bool WorkbookOpened { get; set; }

    public bool WorkbookOpenedReadOnly { get; set; }

    public int ExternalLinkCount { get; set; }

    public long FormulaErrorCountBefore { get; set; }

    public long FormulaErrorCountAfter { get; set; }

    public bool FormulaErrorsIncreased { get; set; }

    public bool FullRecalculationCompleted { get; set; }

    public bool SavedCopyCreated { get; set; }

    public bool SourceWorkbookClosed { get; set; }

    public bool SavedCopyReopened { get; set; }

    public bool SavedCopyOpenedReadOnly { get; set; }

    public bool SavedCopyHasZeroExternalLinks { get; set; }

    public bool SavedCopyClosed { get; set; }

    public bool PdfExported { get; set; }

    public bool PdfParseable { get; set; }
}

internal sealed class ExcelProcessEvidence
{
    public int PreExistingProcessCount { get; set; }

    public bool PreExistingProcessesAllowed { get; init; } = true;

    public bool OwnedProcessVerified { get; set; }

    public bool DeadlineExceeded { get; set; }

    public bool KillAttempted { get; set; }

    public bool KillSucceeded { get; set; }
}

internal sealed class ExcelArtifactEvidence
{
    public long SourceWorkbookBytes { get; set; }

    public long SavedWorkbookBytes { get; set; }

    public long PdfBytes { get; set; }
}

internal sealed class ExcelCleanupEvidence
{
    public bool ApplicationQuitRequested { get; set; }

    public bool OwnedProcessExited { get; set; }

    public bool OutputsRetainedForHarness { get; set; }
}

internal sealed class ExcelErrorEvidence
{
    public required string Code { get; init; }
}

internal sealed class PrivateDataEvidence
{
    public string State { get; init; } = "not_selected";

    public bool PathInspected { get; init; }
}

internal static class ExcelManifestWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true
    };

    internal static void WriteAtomic(string path, ExcelRunManifest manifest)
    {
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine);
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
