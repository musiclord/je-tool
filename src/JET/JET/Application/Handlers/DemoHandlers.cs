using System.Text.Json;

namespace JET.Application;

public sealed class ProjectLoadDemoHandler : IApplicationActionHandler
{
    public string Action => "project.loadDemo";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var data = DemoDataFactory.Create();

        return Task.FromResult<object?>(new
        {
            project = new
            {
                caseName = data.CaseName,
                projectCode = data.ProjectCode,
                entityName = data.EntityName,
                operatorId = data.OperatorId,
                periodStart = data.PeriodStart,
                periodEnd = data.PeriodEnd,
                lastPeriodStart = data.LastPeriodStart
            },
            gl = new
            {
                fileName = data.GlFileName,
                rowCount = data.GlRows.Count,
                amountMode = data.GlAmountMode,
                mapping = data.GlMapping
            },
            tb = new
            {
                fileName = data.TbFileName,
                rowCount = data.TbRows.Count,
                changeMode = data.TbChangeMode,
                mapping = data.TbMapping
            },
            holidays = data.Holidays,
            makeupDays = data.MakeupDays,
            demoScenario = DemoDataFactory.BuildDemoScenario()
        });
    }
}

public sealed class DemoExportGlFileHandler(IDemoFileWriter writer) : IApplicationActionHandler
{
    public string Action => "demo.exportGlFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var exported = await Task.Run(
            () => writer.WriteGlAsync(DemoDataFactory.Create(), cancellationToken),
            cancellationToken);

        return new { filePath = exported.FilePath, fileName = exported.FileName };
    }
}

public sealed class DemoExportTbFileHandler(IDemoFileWriter writer) : IApplicationActionHandler
{
    public string Action => "demo.exportTbFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var exported = await Task.Run(
            () => writer.WriteTbAsync(DemoDataFactory.Create(), cancellationToken),
            cancellationToken);

        return new { filePath = exported.FilePath, fileName = exported.FileName };
    }
}

public sealed class DemoExportAccountMappingFileHandler(IDemoFileWriter writer) : IApplicationActionHandler
{
    public string Action => "demo.exportAccountMappingFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var exported = await Task.Run(
            () => writer.WriteAccountMappingAsync(DemoDataFactory.Create(), cancellationToken),
            cancellationToken);

        return new { filePath = exported.FilePath, fileName = exported.FileName };
    }
}

public sealed class DemoExportAuthorizedPreparerFileHandler(IDemoFileWriter writer) : IApplicationActionHandler
{
    public string Action => "demo.exportAuthorizedPreparerFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var exported = await Task.Run(
            () => writer.WriteAuthorizedPreparerAsync(DemoDataFactory.Create(), cancellationToken),
            cancellationToken);

        return new { filePath = exported.FilePath, fileName = exported.FileName };
    }
}
