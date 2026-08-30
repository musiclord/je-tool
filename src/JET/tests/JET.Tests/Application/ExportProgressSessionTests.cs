using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class ExportProgressSessionTests
{
    [Fact]
    public void SharedClockAndSheetLocalWriterUpdates_BecomeOneMonotonicCumulativeContract()
    {
        var clock = new ManualTimeProvider();
        var events = new RecordingPublisher();
        var session = new ExportProgressSession(events, CancellationToken.None, clock);

        var validation = session.Start(ReportArtifactKind.ValidationReport);
        clock.AdvanceMilliseconds(7);
        _ = session.Start(ReportArtifactKind.AccountMapping);
        clock.AdvanceMilliseconds(3);
        validation.WriterProgress(new WorkpaperProgress("A", 0, 2));
        clock.AdvanceMilliseconds(2);
        validation.WriterProgress(new WorkpaperProgress("A", 1, 3));
        clock.AdvanceMilliseconds(5);
        validation.WriterProgress(new WorkpaperProgress("B", 2, 4));
        clock.AdvanceMilliseconds(2);
        validation.FinalizingWorkbook();
        clock.AdvanceMilliseconds(2);
        validation.PublishingArtifact();

        Assert.Equal([0L, 7L, 10L, 12L, 17L, 19L, 21L],
            events.Payloads.Select(payload =>
                payload.GetProperty("elapsedMilliseconds").GetInt64()));

        var validationEvents = events.Payloads
            .Where(payload => payload.GetProperty("artifactKind").GetString()
                == ReportArtifactKindValues.ValidationReport)
            .ToArray();
        Assert.Equal(
            ["preparingData", "writingSheet", "writingSheet", "writingSheet", "finalizingWorkbook", "publishingArtifact"],
            validationEvents.Select(payload => payload.GetProperty("phase").GetString()));
        Assert.Equal(
            [0L, 2L, 3L, 7L, 7L, 7L],
            validationEvents.Select(payload => payload.GetProperty("rowsWritten").GetInt64()));
        Assert.Equal(
            [0, 0, 1, 2, 2, 2],
            validationEvents.Select(payload => payload.GetProperty("sheetsCompleted").GetInt32()));
        Assert.Null(validationEvents[0].GetProperty("sheetName").GetString());
        Assert.Null(validationEvents[^1].GetProperty("sheetName").GetString());
    }

    private sealed class RecordingPublisher : IJetEventPublisher
    {
        public List<JsonElement> Payloads { get; } = [];

        public void Publish(string eventName, object payload)
        {
            Assert.Equal("export.progress", eventName);
            Payloads.Add(JsonSerializer.SerializeToElement(
                payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => _timestamp;

        public void AdvanceMilliseconds(long milliseconds) =>
            _timestamp = checked(_timestamp + milliseconds);
    }
}
