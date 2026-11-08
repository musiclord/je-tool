using System.Linq;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// system.whoAmI 的純 Application 單元測試。驗收契約來源：docs/action-contract-manifest.md 的 system.whoAmI 列。
/// 只保留 SQLite 之後，身分就是本機的 Windows 帳號，不再向線上使用者目錄取編號，也沒有編號的離線快取
/// （裁定 2026-11-06-08）。
/// </summary>
public sealed class SystemWhoAmIHandlerTests
{
    private static readonly CurrentPrincipal Principal = new("CONTOSO\\alice");

    [Fact]
    public async Task WhoAmI_ReportsQualifiedPrincipalAndShortName()
    {
        var data = await InvokeAsync();

        Assert.Equal("CONTOSO\\alice", data.GetProperty("principal").GetString());
        Assert.Equal("alice", data.GetProperty("shortName").GetString());
    }

    [Fact]
    public async Task WhoAmI_ResponseKeys_AreExactlyPrincipalAndShortName()
    {
        var data = await InvokeAsync();

        var keys = data.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "principal", "shortName" }, keys);
    }

    private static async Task<JsonElement> InvokeAsync()
    {
        var handler = new SystemWhoAmIHandler(Principal);
        using var payload = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
