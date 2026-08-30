using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// project.saveProgress 契約測試（manifest「Project Persistence / Host / Dev Actions」）。
/// oracle：manifest 規格——currentStep 為 0–5 的 6 步索引，持久化後由 project.load / project.list 回放。
/// </summary>
public sealed class ProjectSaveProgressHandlerTests
{
    private const string CreatePayload =
        """
        {
          "projectCode": "ENG-2026-010",
          "entityName": "進度保存測試公司",
          "operatorId": "auditor01",
          "periodStart": "2025-01-01",
          "periodEnd": "2025-12-31"
        }
        """;

    [Fact]
    public async Task SaveProgress_PersistsStepForLoadAndListResume()
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;

        var saved = await host.DispatchAsync("project.saveProgress", """{ "currentStep": 4 }""");

        Assert.True(saved.GetProperty("ok").GetBoolean());
        Assert.Equal(4, saved.GetProperty("currentStep").GetInt32());

        // resume 路徑一：project.load 回放保存的位置
        var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");
        Assert.Equal(4, loaded.GetProperty("project").GetProperty("currentStep").GetInt32());

        // resume 路徑二：project.list 摘要同步反映
        var list = await host.DispatchAsync("project.list");
        Assert.Equal(4, list.GetProperty("projects")[0].GetProperty("currentStep").GetInt32());
    }

    [Fact]
    public async Task SaveProgress_AllowsMovingBackward()
    {
        // 與匯入/配對的 AdvanceStep（只前進）不同：使用者導航回較早步驟也要被記錄。
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;

        await host.DispatchAsync("project.saveProgress", """{ "currentStep": 4 }""");
        await host.DispatchAsync("project.saveProgress", """{ "currentStep": 0 }""");

        var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");
        Assert.Equal(0, loaded.GetProperty("project").GetProperty("currentStep").GetInt32());
    }

    // BVA：步驟索引邊界 0 與 5 為合法值（6 步模型，manifest Step Data Outline）。
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task SaveProgress_BoundaryStepIndex_IsAccepted(int step)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var saved = await host.DispatchAsync(
            "project.saveProgress", $$"""{ "currentStep": {{step}} }""");

        Assert.Equal(step, saved.GetProperty("currentStep").GetInt32());
    }

    // BVA：邊界外鄰值 -1 / 6 必須被拒絕。
    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public async Task SaveProgress_OutOfRangeStepIndex_ThrowsInvalidPayload(int step)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.saveProgress", $$"""{ "currentStep": {{step}} }"""));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Contains("currentStep", ex.Message);
    }

    [Fact]
    public async Task SaveProgress_MissingCurrentStep_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.saveProgress", "{}"));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Contains("currentStep", ex.Message);
    }

    [Fact]
    public async Task SaveProgress_NoActiveProject_ThrowsNoActiveProject()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.saveProgress", """{ "currentStep": 1 }"""));

        Assert.Equal(JetErrorCodes.NoActiveProject, ex.Code);
    }
}
