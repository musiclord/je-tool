using System.Text.RegularExpressions;

namespace Jet.ExcelDriver;

internal sealed class ExcelRunner(
    IExcelProcessCatalog processCatalog,
    IPdfInspector pdfInspector)
{
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex OfficeVersionPattern = new(
        "^[0-9]{1,3}(?:\\.[0-9]{1,5}){0,3}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal ExcelRunManifest Run(OwnedExcelRun run)
    {
        var manifest = new ExcelRunManifest
        {
            ReportKind = run.Options.ReportKind,
            TimeoutSeconds = run.Options.TimeoutSeconds
        };
        manifest.Assertions.SourceWorkbookExists = true;
        manifest.Artifacts.SourceWorkbookBytes = run.SourceWorkbookBytes;

        IExcelAutomationSession? automation = null;
        IExcelDeadline? deadline = null;
        ExcelProcessIdentity? ownedIdentity = null;
        try
        {
            var baseline = processCatalog.CaptureSnapshot();
            manifest.Process.PreExistingProcessCount = baseline.Count;
            var activationStartedUtc = DateTimeOffset.UtcNow;
            automation = new LateBoundExcelAutomation();
            manifest.OfficeVersion = SanitizeOfficeVersion(automation.OfficeVersion);
            ownedIdentity = processCatalog.ResolveOwnedProcess(
                automation.ApplicationHwnd,
                activationStartedUtc,
                baseline);
            manifest.Process.OwnedProcessVerified = true;
            deadline = new ExactProcessDeadline(
                processCatalog,
                ownedIdentity.Value,
                TimeSpan.FromSeconds(run.Options.TimeoutSeconds));

            var policy = WorkbookOpenPolicy.SafeReadOnly;
            automation.OpenWorkbook(run.SourceWorkbookPath, policy);
            manifest.Assertions.WorkbookOpened = true;
            manifest.Assertions.WorkbookOpenedReadOnly = automation.IsWorkbookReadOnly;
            Require(manifest.Assertions.WorkbookOpenedReadOnly, "source_not_read_only");

            manifest.Assertions.ExternalLinkCount = automation.ExternalLinkCount;
            Require(manifest.Assertions.ExternalLinkCount == 0, "external_links_present");
            manifest.Assertions.FormulaErrorCountBefore = automation.CountFormulaErrors();
            Require(manifest.Assertions.FormulaErrorCountBefore >= 0, "formula_inventory_invalid");

            automation.CalculateFullRebuild();
            manifest.Assertions.FullRecalculationCompleted = true;
            RejectIfTimedOut(deadline);
            manifest.Assertions.FormulaErrorCountAfter = automation.CountFormulaErrors();
            Require(manifest.Assertions.FormulaErrorCountAfter >= 0, "formula_inventory_invalid");
            manifest.Assertions.FormulaErrorsIncreased =
                manifest.Assertions.FormulaErrorCountAfter > manifest.Assertions.FormulaErrorCountBefore;
            Require(!manifest.Assertions.FormulaErrorsIncreased, "formula_errors_increased");

            automation.SaveCopyAs(run.SavedWorkbookPath);
            manifest.Assertions.SavedCopyCreated = File.Exists(run.SavedWorkbookPath);
            manifest.Artifacts.SavedWorkbookBytes = manifest.Assertions.SavedCopyCreated
                ? new FileInfo(run.SavedWorkbookPath).Length
                : 0;
            Require(
                manifest.Assertions.SavedCopyCreated && manifest.Artifacts.SavedWorkbookBytes > 0,
                "saved_copy_missing");

            automation.ExportFirstPagePdf(run.PdfPath);
            manifest.Assertions.PdfExported = File.Exists(run.PdfPath);
            var pdf = pdfInspector.Inspect(run.PdfPath);
            manifest.Artifacts.PdfBytes = pdf.ByteCount;
            manifest.Assertions.PdfParseable = pdf.Parseable;
            Require(manifest.Assertions.PdfExported && pdf.Parseable && pdf.ByteCount > 0, "pdf_invalid");

            automation.CloseWorkbook();
            manifest.Assertions.SourceWorkbookClosed = true;
            automation.OpenWorkbook(run.SavedWorkbookPath, policy);
            manifest.Assertions.SavedCopyReopened = true;
            manifest.Assertions.SavedCopyOpenedReadOnly = automation.IsWorkbookReadOnly;
            Require(manifest.Assertions.SavedCopyOpenedReadOnly, "saved_copy_not_read_only");
            manifest.Assertions.SavedCopyHasZeroExternalLinks = automation.ExternalLinkCount == 0;
            Require(manifest.Assertions.SavedCopyHasZeroExternalLinks, "saved_copy_external_links_present");
            automation.CloseWorkbook();
            manifest.Assertions.SavedCopyClosed = true;

            manifest.Assertions.SourceWorkbookUnchanged = run.IsSourceWorkbookUnchanged();
            Require(manifest.Assertions.SourceWorkbookUnchanged, "source_workbook_changed");
            manifest.SetOutcome("passed", DriverExitCodes.Passed);
        }
        catch (ExcelUnavailableException)
        {
            manifest.SetOutcome("blocked", DriverExitCodes.Blocked, "excel_unavailable");
        }
        catch (ExcelProcessOwnershipException)
        {
            manifest.SetOutcome("blocked", DriverExitCodes.Blocked, "process_ownership_unverified");
        }
        catch (ExcelWorkbookOpenException)
        {
            manifest.SetOutcome("blocked", DriverExitCodes.Blocked, "workbook_open_blocked");
        }
        catch (ExcelDriverBlockedException exception)
        {
            manifest.SetOutcome("blocked", DriverExitCodes.Blocked, exception.Code);
        }
        catch (ExcelDriverAssertionException exception)
        {
            manifest.SetOutcome("failed", DriverExitCodes.Failed, exception.Code);
        }
        catch (Exception)
        {
            manifest.SetOutcome("infrastructure_error", DriverExitCodes.InfrastructureError, "excel_driver_exception");
        }
        finally
        {
            var quitRequested = CleanupAutomation(automation, ownedIdentity is not null);
            manifest.Cleanup.ApplicationQuitRequested = quitRequested;

            if (deadline is not null)
            {
                try
                {
                    deadline.Dispose();
                }
                catch (Exception)
                {
                    manifest.SetOutcome(
                        "infrastructure_error",
                        DriverExitCodes.InfrastructureError,
                        "deadline_cleanup_failed");
                }

                manifest.Process.DeadlineExceeded = deadline.TimedOut;
                if (deadline.TimedOut)
                {
                    manifest.Process.KillAttempted = deadline.ProcessTerminated;
                    manifest.Process.KillSucceeded = deadline.ProcessTerminated;
                    manifest.SetOutcome("blocked", DriverExitCodes.Blocked, "deadline_exceeded");
                }
            }

            manifest.Cleanup.OwnedProcessExited = CleanupOwnedProcess(ownedIdentity, manifest.Process);
            if (ownedIdentity is not null && !manifest.Cleanup.OwnedProcessExited)
            {
                manifest.SetOutcome(
                    "infrastructure_error",
                    DriverExitCodes.InfrastructureError,
                    "owned_excel_cleanup_failed");
            }

            try
            {
                manifest.Assertions.SourceWorkbookUnchanged = run.IsSourceWorkbookUnchanged();
                if (!manifest.Assertions.SourceWorkbookUnchanged)
                {
                    manifest.SetOutcome(
                        "infrastructure_error",
                        DriverExitCodes.InfrastructureError,
                        "source_workbook_changed");
                }
            }
            catch (Exception)
            {
                manifest.SetOutcome(
                    "infrastructure_error",
                    DriverExitCodes.InfrastructureError,
                    "source_workbook_recheck_failed");
            }

            manifest.Cleanup.OutputsRetainedForHarness = Directory.Exists(run.OutputRoot);
        }

        return manifest;
    }

    private static string? SanitizeOfficeVersion(string? value) =>
        value is not null && OfficeVersionPattern.IsMatch(value) ? value : null;

    private static void Require(bool condition, string code)
    {
        if (!condition)
        {
            throw new ExcelDriverAssertionException(code);
        }
    }

    private static void RejectIfTimedOut(IExcelDeadline deadline)
    {
        if (deadline.TimedOut)
        {
            throw new ExcelDriverBlockedException("deadline_exceeded");
        }
    }

    private static bool CleanupAutomation(IExcelAutomationSession? automation, bool ownershipVerified)
    {
        if (automation is null)
        {
            return false;
        }

        var quitRequested = false;
        if (ownershipVerified)
        {
            try
            {
                automation.CloseWorkbook();
            }
            catch (Exception)
            {
            }

            try
            {
                automation.Quit();
                quitRequested = true;
            }
            catch (Exception)
            {
            }
        }

        try
        {
            automation.Dispose();
        }
        catch (Exception)
        {
        }

        return quitRequested;
    }

    private bool CleanupOwnedProcess(ExcelProcessIdentity? identity, ExcelProcessEvidence evidence)
    {
        if (identity is null)
        {
            return true;
        }

        try
        {
            if (processCatalog.WaitForExitExact(identity.Value, GracefulExitTimeout))
            {
                return true;
            }

            evidence.KillAttempted = true;
            evidence.KillSucceeded = processCatalog.TryKillExact(identity.Value);
            return evidence.KillSucceeded
                && processCatalog.WaitForExitExact(identity.Value, GracefulExitTimeout);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
