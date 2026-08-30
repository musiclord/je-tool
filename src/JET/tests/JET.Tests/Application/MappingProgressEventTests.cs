using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

public sealed class MappingProgressEventTests
{
    [Fact]
    public async Task DemoProjection_PublishesFinalProgressForGlAndTb_WithBatchTotals()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var events = host.PublishedEvents
            .Where(item => item.EventName == "mapping.progress")
            .Select(item => JsonSerializer.SerializeToElement(
                item.Payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .ToArray();

        Assert.Equal(2, events.Length);
        var gl = events.Single(item => item.GetProperty("kind").GetString() == "gl");
        var tb = events.Single(item => item.GetProperty("kind").GetString() == "tb");
        Assert.Equal(context.Demo.GetProperty("gl").GetProperty("rowCount").GetInt64(), gl.GetProperty("rowsProcessed").GetInt64());
        Assert.Equal(gl.GetProperty("rowsProcessed").GetInt64(), gl.GetProperty("totalRows").GetInt64());
        Assert.Equal(context.Demo.GetProperty("tb").GetProperty("rowCount").GetInt64(), tb.GetProperty("rowsProcessed").GetInt64());
        Assert.Equal(tb.GetProperty("rowsProcessed").GetInt64(), tb.GetProperty("totalRows").GetInt64());
    }
}
