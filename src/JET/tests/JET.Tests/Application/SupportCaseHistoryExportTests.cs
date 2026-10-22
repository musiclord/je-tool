using System.Runtime.CompilerServices;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 匯出讀案件紀錄兩次：先算摘要，再逐行輸出。寫入器剛好在兩次之間把保留的事件補寫進紀錄時，那筆事件只能出現一次。
/// </summary>
public sealed class SupportCaseHistoryExportTests
{
    private const string Process = "synthetic0001";

    [Fact]
    public async Task UnsavedEventWrittenBetweenTheTwoReads_IsExportedOnce()
    {
        var saved = Entry(1);
        var late = Entry(2);
        var reader = new FakeReader([Line(saved)], [Line(saved), Line(late)]);
        var history = new FakeHistory(reader, new SupportCaseHistoryWriteState([late], 0, null));

        var export = await SupportCaseHistoryExport.ReadAsync(history, new FakeLog(), "p", null, CancellationToken.None);
        var lines = new List<string>();
        await foreach (var line in export.LinesAsync("header", CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(["header", Line(saved), Line(late)], lines);
    }

    [Fact]
    public async Task UnsavedEventStillMissingFromTheRecord_IsExportedAfterTheRecord()
    {
        var saved = Entry(1);
        var late = Entry(2);
        var reader = new FakeReader([Line(saved)], [Line(saved)]);
        var history = new FakeHistory(reader, new SupportCaseHistoryWriteState([late], 0, null));

        var export = await SupportCaseHistoryExport.ReadAsync(history, new FakeLog(), "p", null, CancellationToken.None);
        var lines = new List<string>();
        await foreach (var line in export.LinesAsync("header", CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(["header", Line(saved), Line(late)], lines);
    }

    private static string Line(SupportDiagnosticLogEntry entry) => SupportDiagnosticNdjson.SerializeLine(entry);

    private static SupportDiagnosticLogEntry Entry(long sequence) =>
        new(DateTimeOffset.UnixEpoch, "Information", "JET.Tests", "action.start", "synthetic", $"c{sequence}", "p",
            new Dictionary<string, object?>(StringComparer.Ordinal), null)
        {
            ProcessInstance = Process,
            Sequence = sequence,
        };

    /// <summary>每次列舉回傳下一組內容，模擬兩次讀取之間紀錄又被附加。</summary>
    private sealed class FakeReader(params string[][] reads) : ISupportCaseHistoryReader
    {
        private int _read;

        public long CompleteBytes => 0;

        public long IncompleteTailBytes => 0;

        public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var lines = reads[Math.Min(_read++, reads.Length - 1)];
            foreach (var line in lines)
            {
                await Task.Yield();
                yield return line;
            }
        }
    }

    private sealed class FakeHistory(ISupportCaseHistoryReader reader, SupportCaseHistoryWriteState state) : ISupportCaseHistory
    {
        public void Prepare(string projectId, bool createdNow)
        {
        }

        public Task ForgetAsync(string projectId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ISupportCaseHistoryReader? OpenReader(string projectId) => reader;

        public SupportCaseHistoryWriteState WriteState(string projectId) => state;
    }

    private sealed class FakeLog : ISupportDiagnosticLogStore
    {
        public IReadOnlyList<SupportDiagnosticLogEntry> Snapshot() => [];

        public string? ProcessInstance => Process;
    }
}
