using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;
using static JET.Tests.Application.WorkpaperExportTestSupport;

namespace JET.Tests.Application;

/// <summary>WorkingPaper 匯出：情境位置為空、重複或不存在時拒絕。</summary>
public sealed class WorkpaperExportScenarioPositionTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public async Task Export_InvalidScenarioPositions_IsRejected(string variant)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        int[] positions = variant switch
        {
            "empty" => [],
            "duplicate" => [1, 1],
            _ => [99]
        };

        var error = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workpaperStream", Payload(prepared, positions)));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
    }
}
