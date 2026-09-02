using System.Text.Json;
using JET.Bridge;
using Xunit;

namespace JET.Tests.Bridge;

public sealed class BridgeCorrelationContractTests
{
    [Fact]
    public void ErrorEnvelope_ExposesCorrelationIdForPickerSupportExport()
    {
        var envelope = new JetResponseEnvelope(
            "request-1",
            false,
            null,
            new JetErrorDto("artifact_recovery_conflict", "conflict", null),
            "correlation-1");

        var wire = JsonSerializer.SerializeToElement(
            envelope,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.False(wire.GetProperty("ok").GetBoolean());
        Assert.Equal("correlation-1", wire.GetProperty("correlationId").GetString());
        Assert.Equal(
            "artifact_recovery_conflict",
            wire.GetProperty("error").GetProperty("code").GetString());
    }
}
