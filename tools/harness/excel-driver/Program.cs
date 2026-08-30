namespace Jet.ExcelDriver;

internal static class Program
{
    [STAThread]
    internal static int Main(string[] args)
    {
        DriverOptions options;
        try
        {
            options = DriverOptions.Parse(args);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            return DriverExitCodes.InfrastructureError;
        }

        ExcelRunManifest manifest;
        try
        {
            var run = OwnedExcelRun.Open(options);
            manifest = new ExcelRunner(
                new WindowsExcelProcessCatalog(),
                new PdfStructureInspector()).Run(run);
        }
        catch (Exception)
        {
            manifest = new ExcelRunManifest
            {
                ReportKind = options.ReportKind,
                TimeoutSeconds = options.TimeoutSeconds
            };
            manifest.SetOutcome(
                "infrastructure_error",
                DriverExitCodes.InfrastructureError,
                "owned_input_invalid");
        }

        try
        {
            ExcelManifestWriter.WriteAtomic(options.ManifestPath, manifest);
        }
        catch (Exception)
        {
            return DriverExitCodes.InfrastructureError;
        }

        return manifest.ExitCode;
    }
}
