using System.Linq;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// calendar.setNonWorkingDays 與 project.load 的非工作日契約(manifest)。
/// canonical 編碼 .NET DayOfWeek 週日=0…週六=6;未設定 → 預設週六、週日。
/// </summary>
public sealed class CalendarNonWorkingDaysHandlerTests
{
    private const string CreatePayload =
        """
        {
          "projectCode": "ENG-2024-001",
          "entityName": "範例股份有限公司",
          "operatorId": "auditor01",
          "periodStart": "2024-01-01",
          "periodEnd": "2024-12-31",
          "lastPeriodStart": "2024-12-31"
        }
        """;

    private static int[] CalendarNonWorkingDays(System.Text.Json.JsonElement loaded) =>
        loaded.GetProperty("importState").GetProperty("calendar")
            .GetProperty("nonWorkingDays").EnumerateArray().Select(e => e.GetInt32()).ToArray();

    private static System.Text.Json.JsonElement CalendarState(System.Text.Json.JsonElement loaded) =>
        loaded.GetProperty("importState").GetProperty("calendar");

    // 新專案未設定 → project.load 回預設週日(0)、週六(6)。
    [Fact]
    public async Task ProjectLoad_FreshProject_ReturnsDefaultSatSun()
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;

        var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");

        Assert.Equal(new[] { 0, 6 }, CalendarNonWorkingDays(loaded));
        Assert.False(CalendarState(loaded).GetProperty("calendarImported").GetBoolean());
        Assert.False(CalendarState(loaded).GetProperty("nonWorkingDaysConfigured").GetBoolean());
    }

    // 設定週五(5)、週六(6)→ 去重排序後回傳,且 project.load 反映。
    [Fact]
    public async Task SetNonWorkingDays_FridaySaturday_PersistsAndReflectsInLoad()
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;

        var set = await host.DispatchAsync("calendar.setNonWorkingDays", """{ "days": [6, 5, 5] }""");
        Assert.Equal(new[] { 5, 6 },
            set.GetProperty("nonWorkingDays").EnumerateArray().Select(e => e.GetInt32()).ToArray());

        var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");
        Assert.Equal(new[] { 5, 6 }, CalendarNonWorkingDays(loaded));
        Assert.False(CalendarState(loaded).GetProperty("calendarImported").GetBoolean());
        Assert.True(CalendarState(loaded).GetProperty("nonWorkingDaysConfigured").GetBoolean());
    }

    [Fact]
    public async Task SetNonWorkingDays_SundayOnly_PersistsExactPayloadAndReflectsInLoad()
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;

        var set = await host.DispatchAsync(
            "calendar.setNonWorkingDays",
            """{ "days": [0] }""");
        Assert.Equal(
            new[] { 0 },
            set.GetProperty("nonWorkingDays").EnumerateArray().Select(e => e.GetInt32()).ToArray());

        var projectPath = Path.Combine(host.ProjectsRoot, projectId, "project.json");
        using var persisted = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(projectPath));
        Assert.Equal(
            new[] { 0 },
            persisted.RootElement.GetProperty("nonWorkingDays")
                .EnumerateArray().Select(e => e.GetInt32()).ToArray());

        var loaded = await host.DispatchAsync(
            "project.load",
            $$"""{ "projectId": "{{projectId}}" }""");
        Assert.Equal(new[] { 0 }, CalendarNonWorkingDays(loaded));
    }

    // 空集合(整週工作日)是合法設定。
    [Fact]
    public async Task SetNonWorkingDays_Empty_PersistsEmpty()
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;

        var set = await host.DispatchAsync("calendar.setNonWorkingDays", """{ "days": [] }""");

        Assert.Empty(set.GetProperty("nonWorkingDays").EnumerateArray());

        var loaded = await host.DispatchAsync(
            "project.load",
            $$"""{ "projectId": "{{projectId}}" }""");
        Assert.Empty(CalendarNonWorkingDays(loaded));
        Assert.True(CalendarState(loaded).GetProperty("nonWorkingDaysConfigured").GetBoolean());
    }

    // 負向:越界值 7 → invalid_payload。
    [Fact]
    public async Task SetNonWorkingDays_OutOfRange_ReturnsInvalidPayload()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("calendar.setNonWorkingDays", """{ "days": [0, 7] }"""));
        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
    }
}
