using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;
using static JET.Tests.Application.WorkingPaperExportTestSupport;

namespace JET.Tests.Application;

/// <summary>
/// WorkingPaper 匯出：情境位置重複或不存在時拒絕。一個情境都沒勾選時照樣產生底稿，
/// 空的情境清單不拒絕，由 WorkingPaperWithoutScenarioTests 檢查產出的底稿。
/// </summary>
public sealed class WorkingPaperExportScenarioPositionTests
{
    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public async Task Export_InvalidScenarioPositions_IsRejected(string variant)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        int[] positions = variant switch
        {
            "duplicate" => [1, 1],
            _ => [99]
        };

        var error = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("export.workingPaperStream", Payload(prepared, positions)));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
    }
}
