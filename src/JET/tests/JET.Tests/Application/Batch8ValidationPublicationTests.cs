using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch8ValidationPublicationTests
{
    [Fact]
    public async Task ExportValidationArtifacts_SecondReportLocked_ThrowsAndPreservesPriorFilesAndCatalog()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V1", "2025-03-05", "A", "合成科目", "一般分錄", "10.00", 1),
            validateForDownstream: true);
        var validation = await host.DispatchAsync("validate.run");
        var payload = JsonSerializer.Serialize(new { runId = validation.GetProperty("resultRef").GetProperty("runId").GetString() });
        var first = await host.DispatchAsync("export.validationArtifacts", payload);
        var artifacts = first.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.Equal(2, artifacts.Length);
        var validationPath = artifacts.Single(item => item.GetProperty("kind").GetString() == "validationReport")
            .GetProperty("fullPath").GetString()!;
        var infPath = artifacts.Single(item => item.GetProperty("kind").GetString() == "infReport")
            .GetProperty("fullPath").GetString()!;
        var validationBytes = await File.ReadAllBytesAsync(validationPath);
        var infBytes = await File.ReadAllBytesAsync(infPath);
        var manifestPath = Path.Combine(Path.GetDirectoryName(validationPath)!, ProjectReportArtifactStore.ManifestFileName);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);

        using (var locked = new FileStream(infPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("export.validationArtifacts", payload));
            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Contains("關閉", error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(validationBytes, await File.ReadAllBytesAsync(validationPath));
        Assert.Equal(infBytes, await File.ReadAllBytesAsync(infPath));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(manifestPath));
        var reloaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var listed = reloaded.GetProperty("reportArtifacts").EnumerateArray().ToArray();
        Assert.Equal(artifacts.Select(item => item.GetProperty("artifactId").GetString()),
            listed.Select(item => item.GetProperty("artifactId").GetString()));
        Assert.All(listed, item => Assert.Equal("asPublished", item.GetProperty("fileState").GetString()));
        Assert.True((await host.DispatchAsync("export.validationArtifacts", payload)).GetProperty("ok").GetBoolean());
    }
}
