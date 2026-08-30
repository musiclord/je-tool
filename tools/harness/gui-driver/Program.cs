namespace Jet.GuiDriver;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        DriverOptions options;
        try
        {
            options = DriverOptions.Parse(args);
        }
        catch (DriverUsageException exception)
        {
            ManifestWriter.WriteUsageSummary(exception.Code);
            return 3;
        }
        catch
        {
            ManifestWriter.WriteUsageSummary("invalid_arguments");
            return 3;
        }

        var outcome = await GuiRunner.ExecuteAsync(options).ConfigureAwait(false);
        try
        {
            ManifestWriter.Write(options.ManifestPath, outcome);
        }
        catch (Exception exception)
        {
            outcome.Status = "infrastructure_error";
            outcome.ExitCode = 4;
            outcome.ErrorCode = "manifest_write_failed";
            outcome.ErrorType = exception.GetType().Name;
        }

        ManifestWriter.WriteSummary(outcome);
        return outcome.ExitCode;
    }
}
