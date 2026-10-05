using System.Text.Json;
using JET.Application;
using JET.Bridge;

namespace Jet.BrowserHost;

/// <summary>
/// 案件目錄是空的時，經正式 dispatcher 建立兩個合成示範案件。資料只來自 DemoDataFactory 與
/// demo.export* 寫出的合成活頁簿；流程比照 AgentGuiTest 的 seed，但只用公開 action。
/// </summary>
internal static class DemoSeeder
{
    public const string FilterReadyProject = "合成示範-篩選就緒";
    public const string MappingPendingProject = "合成示範-待配對";

    public static async Task EnsureSeededAsync(ActionDispatcher dispatcher, string projectsRoot)
    {
        if (Directory.EnumerateFileSystemEntries(projectsRoot).Any())
        {
            Console.WriteLine("[seed] 沿用既有的合成案件；要重建請以 --reset 啟動。");
            return;
        }

        await SeedAsync(dispatcher, FilterReadyProject, "DEMO-FILTER", throughValidation: true).ConfigureAwait(false);
        await SeedAsync(dispatcher, MappingPendingProject, "DEMO-MAPPING", throughValidation: false).ConfigureAwait(false);
    }

    private static async Task SeedAsync(
        ActionDispatcher dispatcher,
        string caseName,
        string projectCode,
        bool throughValidation)
    {
        Console.WriteLine($"[seed] 建立 {caseName} …");
        try
        {
            var demo = DemoDataFactory.Create();
            await DispatchAsync(dispatcher, "project.create", new
            {
                caseName,
                projectCode,
                entityName = demo.EntityName,
                periodStart = demo.PeriodStart,
                periodEnd = demo.PeriodEnd,
                lastPeriodStart = demo.LastPeriodStart,
                databaseProvider = "sqlite",
            }).ConfigureAwait(false);
            await ImportDemoFileAsync(dispatcher, "demo.exportGlFile", "import.gl.fromFile").ConfigureAwait(false);
            await ImportDemoFileAsync(dispatcher, "demo.exportTbFile", "import.tb.fromFile").ConfigureAwait(false);

            if (throughValidation)
            {
                await ImportDemoFileAsync(
                    dispatcher, "demo.exportAccountMappingFile", "import.accountMapping.fromFile").ConfigureAwait(false);
                await ImportDemoFileAsync(
                    dispatcher, "demo.exportAuthorizedPreparerFile", "import.authorizedPreparer.fromFile",
                    sourceColumn: "姓名").ConfigureAwait(false);
                await DispatchAsync(dispatcher, "import.holiday", new { dates = demo.Holidays }).ConfigureAwait(false);
                await DispatchAsync(dispatcher, "import.makeupDay", new { dates = demo.MakeupDays }).ConfigureAwait(false);
                await DispatchAsync(dispatcher, "mapping.commit.gl", new
                {
                    mapping = demo.GlMapping,
                    amountMode = demo.GlAmountMode,
                    rdeFields = Array.Empty<object>(),
                }).ConfigureAwait(false);
                await DispatchAsync(dispatcher, "mapping.commit.tb", new
                {
                    mapping = demo.TbMapping,
                    changeMode = demo.TbChangeMode,
                }).ConfigureAwait(false);
                var validation = await DispatchAsync(dispatcher, "validate.run", new { }).ConfigureAwait(false);
                await DispatchAsync(dispatcher, "export.validationArtifacts", new
                {
                    runId = validation.GetProperty("resultRef").GetProperty("runId").GetString(),
                }).ConfigureAwait(false);
                var prescreen = await DispatchAsync(dispatcher, "prescreen.run", new { }).ConfigureAwait(false);
                await DispatchAsync(dispatcher, "export.prescreenReport", new
                {
                    runId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString(),
                }).ConfigureAwait(false);
            }

            // currentStep 是 0 起算的步驟索引：2 是欄位配對，4 是進階條件篩選。
            await DispatchAsync(dispatcher, "project.saveProgress", new
            {
                currentStep = throughValidation ? 4 : 2,
            }).ConfigureAwait(false);
            await DispatchAsync(dispatcher, "project.releaseLock", new { }).ConfigureAwait(false);
            Console.WriteLine($"[seed] {caseName} 完成。");
        }
        catch (Exception exception)
        {
            var error = JetWebMessageBridge.ToErrorDto(exception);
            Console.WriteLine($"[seed] {caseName} 建立失敗 code={error.Code}: {error.Message}");
        }
    }

    private static async Task ImportDemoFileAsync(
        ActionDispatcher dispatcher, string exportAction, string importAction, string? sourceColumn = null)
    {
        var file = await DispatchAsync(dispatcher, exportAction, new { }).ConfigureAwait(false);
        await DispatchAsync(dispatcher, importAction, new
        {
            filePath = file.GetProperty("filePath").GetString(),
            fileName = file.GetProperty("fileName").GetString(),
            // 2026-10-04 第 3 批 L12 裁定 sourceColumn 必填；授權名單指定示範檔的姓名欄。
            sourceColumn,
        }).ConfigureAwait(false);
    }

    private static async Task<JsonElement> DispatchAsync(ActionDispatcher dispatcher, string action, object payload)
    {
        var result = await dispatcher.DispatchAsync(
            action,
            JsonSerializer.SerializeToElement(payload),
            CancellationToken.None).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result);
    }
}
