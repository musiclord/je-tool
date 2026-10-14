using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9ProjectRecoveryTests
{
    [Theory]
    [InlineData("project.listLocal")]
    [InlineData("project.list")]
    public async Task CorruptLocalDocument_RemainsVisibleWithoutInventingMetadata_AndHealthyCasesStillLoad(string action)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """{"caseName":"batch9-broken","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        await host.DispatchAsync("project.create", """{"caseName":"batch9-healthy","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        var path = Path.Combine(host.ProjectsRoot, "batch9-broken", "project.json");
        const string corrupt = "{not-valid-synthetic-json";
        await File.WriteAllTextAsync(path, corrupt);
        var response = await host.DispatchAsync(action);
        var rows = response.GetProperty("projects").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        var broken = Assert.Single(rows.Where(row => row.GetProperty("projectId").GetString() == "batch9-broken"));
        Assert.Equal(JsonValueKind.Null, broken.GetProperty("databaseProvider").ValueKind);
        Assert.Equal(JetErrorCodes.FileReadError, broken.GetProperty("loadError").GetProperty("code").GetString());
        var message = broken.GetProperty("loadError").GetProperty("message").GetString()!;
        // 使用者 2026-10-14 第三輪勾選頁確認 R3-50（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。 句子不寫檔名，改寫「設定檔」。
        Assert.Contains("設定檔無法讀取", message);
        Assert.DoesNotContain("project.json", message);
        Assert.Contains("備份", message);
        Assert.DoesNotContain(host.ProjectsRoot, message);
        Assert.DoesNotContain(corrupt, message);
        Assert.False(broken.TryGetProperty("periodStart", out _));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(path));
        var healthy = await host.DispatchAsync("project.load", """{"projectId":"batch9-healthy"}""");
        Assert.Equal("batch9-healthy", healthy.GetProperty("project").GetProperty("projectId").GetString());
    }

    [Theory]
    [InlineData("2026-01-01", "2025-12-31")]
    [InlineData("not-a-date", "2025-12-31")]
    public async Task ExistingInvalidPeriod_HasARecoveryMessageBeforeChangingSession(string start, string end)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """{"caseName":"batch9-invalid-period","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        var path = Path.Combine(host.ProjectsRoot, "batch9-invalid-period", "project.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["periodStart"] = start; document["periodEnd"] = end;
        var saved = document.ToJsonString();
        await File.WriteAllTextAsync(path, saved);
        await host.DispatchAsync("project.create", """{"caseName":"batch9-keep-active","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        var exception = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("project.load", """{"projectId":"batch9-invalid-period"}"""));
        Assert.Equal(JetErrorCodes.InvalidProjectSchema, exception.Code);
        Assert.Contains("查核期間", exception.Message);
        Assert.Contains("另建案件", exception.Message);
        Assert.Equal(saved, await File.ReadAllTextAsync(path));
        var unchangedSession = await host.DispatchAsync("project.update");
        Assert.Equal("batch9-keep-active", unchangedSession.GetProperty("project").GetProperty("projectId").GetString());
    }
}
