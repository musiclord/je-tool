using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 建立案件時，案件代號與客戶名稱可以留白；建案者記目前 Windows 使用者的全名，沒有全名時記帳號短名，
/// 不採用請求帶來的 operatorId。
/// </summary>
public sealed class ProjectCreatorNameTests
{
    [Fact]
    public async Task Create_AllowsBlankOptionalMetadataAndUsesCurrentWindowsShortName()
    {
        using var host = new HandlerTestHost(principalName: "EXAMPLE\\current.user");

        var created = await host.DispatchAsync(
            "project.create",
            """
            {
              "projectName": "只填案件名稱",
              "periodStart": "2026-01-01",
              "periodEnd": "2026-12-31"
            }
            """);

        var projectId = created.GetProperty("projectId").GetString()!;
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(host.ProjectsRoot, projectId, "project.json")));

        Assert.Equal(string.Empty, document.RootElement.GetProperty("projectCode").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("clientName").GetString());
        Assert.Equal("current.user", document.RootElement.GetProperty("operatorId").GetString());
    }

    [Fact]
    public async Task Create_IgnoresCallerSuppliedOperatorAndUsesCurrentWindowsShortName()
    {
        using var host = new HandlerTestHost(principalName: "EXAMPLE\\current.user");

        var created = await host.DispatchAsync(
            "project.create",
            """
            {
              "projectName": "不能冒名的案件",
              "projectCode": "OPTIONAL-001",
              "clientName": "選填客戶",
              "operatorId": "someone.else",
              "periodStart": "2026-01-01",
              "periodEnd": "2026-12-31"
            }
            """);

        var projectId = created.GetProperty("projectId").GetString()!;
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(host.ProjectsRoot, projectId, "project.json")));

        Assert.Equal("current.user", document.RootElement.GetProperty("operatorId").GetString());
    }

    [Fact]
    public async Task Create_StoresUserFullNameAsCreatorAndIgnoresCallerSuppliedOperator()
    {
        using var host = new HandlerTestHost(
            principalName: "EXAMPLE\\current.user",
            userFullName: "合成, 審計員 (TW/0000)");

        var created = await host.DispatchAsync(
            "project.create",
            """
            {
              "projectName": "記使用者全名的案件",
              "operatorId": "someone.else",
              "periodStart": "2026-01-01",
              "periodEnd": "2026-12-31"
            }
            """);

        var projectId = created.GetProperty("projectId").GetString()!;
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(host.ProjectsRoot, projectId, "project.json")));
        Assert.Equal("合成, 審計員 (TW/0000)", document.RootElement.GetProperty("operatorId").GetString());

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal("合成, 審計員 (TW/0000)", loaded.GetProperty("project").GetProperty("operatorId").GetString());
    }
}
