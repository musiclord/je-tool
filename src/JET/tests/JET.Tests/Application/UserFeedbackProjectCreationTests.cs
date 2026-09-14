using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

public sealed class UserFeedbackProjectCreationTests
{
    [Fact]
    public async Task Create_AllowsBlankOptionalMetadataAndUsesCurrentWindowsShortName()
    {
        using var host = new HandlerTestHost(principalName: "EXAMPLE\\current.user");

        var created = await host.DispatchAsync(
            "project.create",
            """
            {
              "caseName": "只填案件名稱",
              "periodStart": "2026-01-01",
              "periodEnd": "2026-12-31"
            }
            """);

        var projectId = created.GetProperty("projectId").GetString()!;
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(host.ProjectsRoot, projectId, "project.json")));

        Assert.Equal(string.Empty, document.RootElement.GetProperty("projectCode").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("entityName").GetString());
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
              "caseName": "不能冒名的案件",
              "projectCode": "OPTIONAL-001",
              "entityName": "選填客戶",
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
}
