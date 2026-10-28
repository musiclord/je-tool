using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 第二個流程「正式底稿匯出」（export.workpaperStream）的固定答案測試：階段十一 B14 到 B20，每個分支至少一個方法，
/// 合成資料固定、預期值寫死。走完整 action 的案例 SQLite 與 DuckDB 各跑一次；只碰案件資料夾的改名與清單更新，
/// 直接對報告檔儲存測，資料庫種類無關，只跑一次。
/// 已有測試的分支不重複：請求驗證在 WorkpaperExportRequestValidationTests，取消清暫存檔在 WorkpaperExportCancellationTests，
/// 驗證報告那一組的改名還原在 Batch8ArtifactBatchPublicationTests。
/// </summary>
public sealed class WorkpaperExportBranchTests
{
    private const string TwoScenarios = """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]},{"name":"合成貸方","rationale":"固定答案","groups":[{"rules":[{"type":"drCrOnly","drCr":"credit"}]}]}]}""";
    private const string ScenarioListOutOfDate = "畫面上的情境清單與已儲存的內容不一致，本次未產生。請重新開啟案件後再操作。";
    private const string ReportCannotBeWritten = "報告檔無法寫入。請先關閉 Excel 或其他開啟檔案的程式，並確認檔案不是唯讀且有寫入權限，再重新匯出。";
    private const string StoreProjectId = "phase11-workpaper-store";

    // ── B14 版本不符就回錯誤、不寫檔 ───────────────────────────────────────────

    [Theory]
    [InlineData("sqlite", "run")]
    [InlineData("duckdb", "run")]
    [InlineData("sqlite", "revision")]
    [InlineData("duckdb", "revision")]
    [InlineData("sqlite", "position")]
    [InlineData("duckdb", "position")]
    public async Task Mismatch_IsRejectedWithTheFixedReason_AndWritesNoFile(string provider, string mismatch)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, provider);
        var folder = Folder(host, prepared.Id);
        var payload = mismatch switch
        {
            "run" => Payload(prepared, [1, 2], runId: new string('a', 32)),
            "revision" => Payload(prepared, [1, 2], revision: "stale"),
            _ => Payload(prepared, [1, 99])
        };

        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("export.workpaperStream", payload));

        Assert.Equal(mismatch == "position" ? JetErrorCodes.InvalidPayload : JetErrorCodes.StaleResult, error.Code);
        Assert.Equal(mismatch == "run"
            ? "這份報告依據的資料驗證結果已不是目前的結果，請重新執行資料驗證後再產生。"
            : ScenarioListOutOfDate, error.Message);
        Assert.Empty(Workpapers(folder));
        Assert.Empty(TemporaryFiles(folder));
        Assert.False(File.Exists(ManifestPath(folder)));
        Assert.Equal(1, await HitsAsync(host, prepared.Id, provider, 1));
        Assert.Equal(1, await HitsAsync(host, prepared.Id, provider, 2));
        Assert.Empty(await TbImportBranchTests.ReadAuditAsync(host, prepared.Id, provider));
    }

    // ── B15 只重算這次選取的情境 ───────────────────────────────────────────────

    /// <summary>命中先清空並標成失效：只選第一個情境時只有它重算，案件仍是失效；全選之後才回到目前版本。</summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OnlySelectedScenarios_AreRecomputed_TheCaseStaysStaleUntilAllAreSelected(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, provider);
        await ExecuteAsync(host, prepared.Id, provider,
            "DELETE FROM result_filter_run; UPDATE config_result_stale_state SET filter_stale = 1 WHERE singleton = 1;");

        var partial = await ExportAsync(host, prepared, [1]);

        Assert.False(partial.GetProperty("filterResultsCurrent").GetBoolean());
        Assert.Equal([1], partial.GetProperty("artifact").GetProperty("sourceRef").GetProperty("scenarioPositions").EnumerateArray().Select(position => position.GetInt32()));
        Assert.Equal(1, await HitsAsync(host, prepared.Id, provider, 1));
        Assert.Equal(0, await HitsAsync(host, prepared.Id, provider, 2));
        Assert.Equal(1, await FilterStaleAsync(host, prepared.Id, provider));

        var full = await ExportAsync(host, prepared, [1, 2]);

        Assert.True(full.GetProperty("filterResultsCurrent").GetBoolean());
        Assert.Equal([1, 2], full.GetProperty("artifact").GetProperty("sourceRef").GetProperty("scenarioPositions").EnumerateArray().Select(position => position.GetInt32()));
        Assert.Equal(1, await HitsAsync(host, prepared.Id, provider, 2));
        Assert.Equal(0, await FilterStaleAsync(host, prepared.Id, provider));
        Assert.Equal(2, Workpapers(Folder(host, prepared.Id)).Length);
    }

    // ── B16 寫暫存檔途中失敗或取消 ─────────────────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CancelledWhileWritingSheets_LeavesNoFile_AndKeepsThePriorVersion(string provider)
    {
        using var source = new CancellationTokenSource();
        var publisher = new WorkpaperExportTestSupport.ArmableCancelOnWorkpaperPhase(source, "writingSheet");
        using var host = new HandlerTestHost(eventPublisher: publisher);
        var prepared = await PrepareAsync(host, provider);
        var folder = Folder(host, prepared.Id);
        var first = await ExportAsync(host, prepared, [1, 2]);
        var priorPath = first.GetProperty("artifact").GetProperty("fullPath").GetString()!;
        var priorBytes = await File.ReadAllBytesAsync(priorPath);
        var manifestBytes = await File.ReadAllBytesAsync(ManifestPath(folder));
        publisher.Arm();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportAsync(host, prepared, [1, 2], source.Token));

        Assert.Equal([Path.GetFileName(priorPath)], Workpapers(folder).Select(path => Path.GetFileName(path)));
        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(priorPath));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(ManifestPath(folder)));
        Assert.Empty(TemporaryFiles(folder));
        Assert.Single(await TbImportBranchTests.ReadAuditAsync(host, prepared.Id, provider));
    }

    /// <summary>寫入途中出錯：暫存檔清掉，前一版底稿與清單都不動。</summary>
    [Fact]
    public async Task WriterFails_LeavesNoFile_AndKeepsThePriorVersion()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = CreateStore(root);
        var prior = await store.WriteAsync(StoreProjectId, Request(Bytes(1, 2)), CancellationToken.None);
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(StoreProjectId, Request(async (output, cancellationToken) =>
        {
            await output.WriteAsync(new byte[] { 9 }, cancellationToken);
            throw new InvalidOperationException("synthetic writer failure");
        }), CancellationToken.None));

        Assert.Equal([prior.RelativeFileName], Directory.GetFiles(directory, "*.xlsx").Select(path => Path.GetFileName(path)));
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(directory, prior.RelativeFileName)));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName)));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        var listed = Assert.Single(await store.ListAsync(StoreProjectId, CancellationToken.None));
        Assert.Equal(prior.ArtifactId, listed.ArtifactId);
        Assert.Equal(ReportArtifactFileState.AsPublished, listed.FileState);
    }

    // ── B17 改名成新版本時不覆寫同名底稿 ─────────────────────────────────────

    /// <summary>檔名決定之後、改名之前，資料夾裡出現同名檔：不覆寫，請審計員重新匯出拿新檔名。</summary>
    [Fact]
    public async Task SameNameAppearsBeforePublication_IsRefusedWithTheFixedReason_AndTheForeignFileStays()
    {
        using var root = new TempProjectRoot();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 10, 25, 1, 2, 3, TimeSpan.Zero));
        var (store, directory) = CreateStore(root, time: time);
        var foreign = Path.Combine(directory, VersionFileName(time.Now));
        var publisher = (IReportArtifactPublishingStore)store;

        var error = await Assert.ThrowsAsync<JetActionException>(() => publisher.WriteWithPublishingAsync(
            StoreProjectId, Request(Bytes(1, 2)), _ => File.WriteAllBytes(foreign, [7, 8, 9]), CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.Equal("同名工作底稿已存在，原檔已保留。請重新匯出，JET 會使用新的版本檔名。", error.Message);
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(foreign));
        Assert.Empty(await store.ListAsync(StoreProjectId, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task SecondVersionInTheSameSecond_GetsASequenceSuffix_AndKeepsTheFirst()
    {
        using var root = new TempProjectRoot();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 10, 25, 1, 2, 3, TimeSpan.Zero));
        var (store, directory) = CreateStore(root, time: time);

        var first = await store.WriteAsync(StoreProjectId, Request(Bytes(1)), CancellationToken.None);
        var second = await store.WriteAsync(StoreProjectId, Request(Bytes(2)), CancellationToken.None);

        Assert.Equal(VersionFileName(time.Now), first.RelativeFileName);
        Assert.Equal(VersionFileName(time.Now, sequence: 2), second.RelativeFileName);
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(directory, first.RelativeFileName)));
        Assert.Equal(new byte[] { 2 }, await File.ReadAllBytesAsync(Path.Combine(directory, second.RelativeFileName)));
        Assert.Equal([first.ArtifactId, second.ArtifactId],
            (await store.ListAsync(StoreProjectId, CancellationToken.None)).Select(artifact => artifact.ArtifactId));
    }

    // ── B18 改名或更新報告清單失敗會嘗試還原 ─────────────────────────────────

    [Fact]
    public async Task RenameFails_NothingIsPublished_AndThePriorVersionStays()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = CreateStore(root);
        var prior = await store.WriteAsync(StoreProjectId, Request(Bytes(1, 2)), CancellationToken.None);
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName));
        var failing = CreateStore(root, moveFile: (source, destination, overwrite) =>
        {
            if (IsWorkpaper(destination)) throw new UnauthorizedAccessException("synthetic rename failure");
            File.Move(source, destination, overwrite);
        }).Store;

        var error = await Assert.ThrowsAsync<JetActionException>(() => failing.WriteAsync(StoreProjectId, Request(Bytes(5, 6)), CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.Equal(ReportCannotBeWritten, error.Message);
        await AssertOnlyThePriorVersionAsync(store, directory, prior, manifestBytes);
    }

    /// <summary>底稿已改名成功、清單卻更新失敗：新檔刪回去，清單維持原樣。</summary>
    [Fact]
    public async Task ManifestUpdateFails_TheNewVersionIsRemovedAgain()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = CreateStore(root);
        var prior = await store.WriteAsync(StoreProjectId, Request(Bytes(1, 2)), CancellationToken.None);
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName));
        var failing = CreateStore(root, moveFile: (source, destination, overwrite) =>
        {
            if (IsManifest(destination)) throw new IOException("synthetic manifest failure");
            File.Move(source, destination, overwrite);
        }).Store;

        var error = await Assert.ThrowsAsync<JetActionException>(() => failing.WriteAsync(StoreProjectId, Request(Bytes(5, 6)), CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.Equal(ReportCannotBeWritten, error.Message);
        await AssertOnlyThePriorVersionAsync(store, directory, prior, manifestBytes);
    }

    /// <summary>連還原都失敗（新檔被別的程式開著刪不掉）：句子改說新檔仍保留，兩個原因都留在例外裡。</summary>
    [Fact]
    public async Task RestoreFails_TheNewVersionIsKept_AndTheMessageSaysSo()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = CreateStore(root);
        var prior = await store.WriteAsync(StoreProjectId, Request(Bytes(1, 2)), CancellationToken.None);
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName));
        FileStream? held = null;
        var failing = CreateStore(root, moveFile: (source, destination, overwrite) =>
        {
            if (IsManifest(destination)) throw new IOException("synthetic manifest failure");
            File.Move(source, destination, overwrite);
            if (IsWorkpaper(destination)) held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None);
        }).Store;

        try
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => failing.WriteAsync(StoreProjectId, Request(Bytes(5, 6)), CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Equal("檔案還原未完成，請先關閉報告再重試；本次新檔仍保留。", error.Message);
            var causes = Assert.IsType<AggregateException>(error.InnerException).InnerExceptions;
            Assert.Equal(2, causes.Count);
            Assert.Equal("synthetic manifest failure", causes[0].Message);
            Assert.IsAssignableFrom<IOException>(causes[1]);
        }
        finally
        {
            held?.Dispose();
        }

        var files = Directory.GetFiles(directory, "*.xlsx").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, files.Length);
        Assert.Contains(prior.RelativeFileName, files);
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(directory, prior.RelativeFileName)));
        Assert.Equal(new byte[] { 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(directory, files.Single(name => name != prior.RelativeFileName))));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName)));
        Assert.Equal(prior.ArtifactId, Assert.Single(await store.ListAsync(StoreProjectId, CancellationToken.None)).ArtifactId);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    // ── B19 發布後寫 report.publish 操作紀錄 ─────────────────────────────────

    /// <summary>每個版本各留一筆；工作底稿每次都是新檔，汰換數永遠是 0。</summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task EachVersion_WritesOneReportPublishRecord_WithoutCountingAReplacement(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, provider);
        var record = $"{ProjectAuditOperations.ReportPublish}|{ProjectAuditTargetTypes.ReportCatalog}|workingPaper|1|0";

        var first = await ExportAsync(host, prepared, [1, 2]);
        Assert.Empty(first.GetProperty("warnings").EnumerateArray());
        Assert.Equal([record], await TbImportBranchTests.ReadAuditAsync(host, prepared.Id, provider));

        var second = await ExportAsync(host, prepared, [1]);
        Assert.Empty(second.GetProperty("warnings").EnumerateArray());
        Assert.Equal([record, record], await TbImportBranchTests.ReadAuditAsync(host, prepared.Id, provider));
        Assert.Equal(2, Workpapers(Folder(host, prepared.Id)).Length);
    }

    // ── B20 改名前最後一次檢查取消 ─────────────────────────────────────────────

    /// <summary>暫存檔已寫完、通知「即將發布」時取消：不改名、不更新清單、不寫操作紀錄；已重算的命中留著。</summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CancelledAtTheLastCheckBeforeRename_PublishesNothing_AndKeepsThePriorVersion(string provider)
    {
        using var source = new CancellationTokenSource();
        var publisher = new WorkpaperExportTestSupport.ArmableCancelOnWorkpaperPhase(source, "publishingArtifact");
        using var host = new HandlerTestHost(eventPublisher: publisher);
        var prepared = await PrepareAsync(host, provider);
        var folder = Folder(host, prepared.Id);
        var first = await ExportAsync(host, prepared, [1, 2]);
        var priorPath = first.GetProperty("artifact").GetProperty("fullPath").GetString()!;
        var priorBytes = await File.ReadAllBytesAsync(priorPath);
        var manifestBytes = await File.ReadAllBytesAsync(ManifestPath(folder));
        var eventStart = publisher.WorkpaperEvents.Count;
        publisher.Arm();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportAsync(host, prepared, [1, 2], source.Token));

        Assert.Equal("publishingArtifact", publisher.WorkpaperEvents[^1].GetProperty("phase").GetString());
        Assert.True(publisher.WorkpaperEvents.Count > eventStart);
        Assert.Equal([Path.GetFileName(priorPath)], Workpapers(folder).Select(path => Path.GetFileName(path)));
        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(priorPath));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(ManifestPath(folder)));
        Assert.Empty(TemporaryFiles(folder));
        Assert.Single(await TbImportBranchTests.ReadAuditAsync(host, prepared.Id, provider));
        Assert.Equal(1, await HitsAsync(host, prepared.Id, provider, 1));
        Assert.Equal(1, await HitsAsync(host, prepared.Id, provider, 2));
    }

    /// <summary>取消在改名的瞬間才送到：最後一次檢查已經過了，這次發布照常完成。</summary>
    [Fact]
    public async Task CancelRequestedDuringTheRename_IsTooLate_ThePublicationCompletes()
    {
        using var root = new TempProjectRoot();
        using var cancellation = new CancellationTokenSource();
        var (store, directory) = CreateStore(root);
        var prior = await store.WriteAsync(StoreProjectId, Request(Bytes(1, 2)), CancellationToken.None);
        var late = (IReportArtifactPublishingStore)CreateStore(root, moveFile: (source, destination, overwrite) =>
        {
            if (IsWorkpaper(destination)) cancellation.Cancel();
            File.Move(source, destination, overwrite);
        }).Store;

        var published = await late.WriteWithPublishingAsync(StoreProjectId, Request(Bytes(5, 6)), _ => { }, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(new byte[] { 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(directory, published.RelativeFileName)));
        Assert.Equal([prior.ArtifactId, published.ArtifactId],
            (await store.ListAsync(StoreProjectId, CancellationToken.None)).Select(artifact => artifact.ArtifactId));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    // ── 佈置：完整 action ────────────────────────────────────────────────────────

    /// <summary>GL 兩筆、驗證與預篩選各跑一次，存兩個情境（借方、貸方各命中一筆）；同一種資料庫只建一次範本。</summary>
    private static async Task<Batch9ArtifactStateTests.Prepared> PrepareAsync(HandlerTestHost host, string provider)
    {
        var copied = await CaseTemplates.CopyIntoAsync(host, $"phase11-workpaper:{provider}", async templateHost =>
        {
            var prepared = await Batch9ArtifactStateTests.PrepareAsync(templateHost, provider, fromScratch: true);
            var saved = await templateHost.DispatchAsync("filter.commit", TwoScenarios);
            prepared = prepared with { Revision = saved.GetProperty("resultRef").GetProperty("revision").GetString()! };
            return (prepared.Id, prepared);
        });
        return copied.State;
    }

    private static string Payload(Batch9ArtifactStateTests.Prepared prepared, int[] positions, string? runId = null, string? revision = null) =>
        JsonSerializer.Serialize(new
        {
            validationRunId = runId ?? prepared.ValidationRunId,
            scenarioRevision = revision ?? prepared.Revision,
            scenarioPositions = positions
        });

    private static Task<JsonElement> ExportAsync(
        HandlerTestHost host, Batch9ArtifactStateTests.Prepared prepared, int[] positions, CancellationToken cancellationToken = default) =>
        host.DispatchAsync("export.workpaperStream", Payload(prepared, positions), cancellationToken);

    private static string Folder(HandlerTestHost host, string projectId) => Path.Combine(host.ProjectsRoot, projectId);

    private static string ManifestPath(string folder) => Path.Combine(folder, ProjectReportArtifactStore.ManifestFileName);

    private static string[] Workpapers(string folder) =>
        WorkpaperExportTestSupport.FindWorkpapers(folder).Order(StringComparer.Ordinal).ToArray();

    private static string[] TemporaryFiles(string folder) => Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories);

    private static Task ExecuteAsync(HandlerTestHost host, string projectId, string provider, string sql) =>
        Batch9ArtifactStateTests.ExecuteAsync(host, projectId, provider, sql);

    private static Task<long> HitsAsync(HandlerTestHost host, string projectId, string provider, int position) =>
        Batch9TransactionBoundaryTests.ScalarAsync(host, projectId, provider,
            $"SELECT COUNT(*) FROM result_filter_run WHERE scenario_position = {position};");

    private static Task<long> FilterStaleAsync(HandlerTestHost host, string projectId, string provider) =>
        Batch9TransactionBoundaryTests.ScalarAsync(host, projectId, provider,
            "SELECT filter_stale FROM config_result_stale_state WHERE singleton = 1;");

    // ── 佈置：直接對報告檔儲存 ──────────────────────────────────────────────────

    private static (ProjectReportArtifactStore Store, string Directory) CreateStore(
        TempProjectRoot root, Action<string, string, bool>? moveFile = null, TimeProvider? time = null)
    {
        var folder = new JetProjectFolder(root.Path);
        var directory = folder.GetProjectDirectory(StoreProjectId);
        Directory.CreateDirectory(directory);
        var store = moveFile is null
            ? new ProjectReportArtifactStore(folder, time)
            : new ProjectReportArtifactStore(folder, moveFile, time);
        return (store, directory);
    }

    /// <summary>和匯出動作送進儲存的請求同樣帶查核起訖日，檔名才會和正式程式一樣含期間。</summary>
    private static ReportArtifactWriteRequest Request(ReportArtifactContentWriter writer) => new(
        ReportArtifactKind.WorkingPaper,
        new ReportArtifactSourceRefs(ValidationRunId: "run-1", ScenarioRevision: "rev-1", ScenarioPositions: [1, 2], FilterDataRevision: "7"),
        writer,
        "2025-01-01",
        "2025-12-31");

    private static ReportArtifactContentWriter Bytes(params byte[] content) =>
        (output, cancellationToken) => output.WriteAsync(content, cancellationToken).AsTask();

    private static string VersionFileName(DateTimeOffset generatedUtc, int? sequence = null) =>
        $"{ProjectFileNames.SafePrefix(StoreProjectId)}_20250101-20251231_WorkingPaper_{generatedUtc.ToLocalTime():yyyyMMdd-HHmmss}"
        + (sequence is null ? "" : $"-{sequence}") + ".xlsx";

    private static bool IsWorkpaper(string destination) => Path.GetFileName(destination).Contains("_WorkingPaper_", StringComparison.Ordinal);

    private static bool IsManifest(string destination) =>
        string.Equals(Path.GetFileName(destination), ProjectReportArtifactStore.ManifestFileName, StringComparison.Ordinal);

    private static async Task AssertOnlyThePriorVersionAsync(
        ProjectReportArtifactStore store, string directory, ReportArtifact prior, byte[] manifestBytes)
    {
        Assert.Equal([prior.RelativeFileName], Directory.GetFiles(directory, "*.xlsx").Select(path => Path.GetFileName(path)));
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(directory, prior.RelativeFileName)));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName)));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Empty(Directory.GetFiles(directory, ".report-artifact-backup-*"));
        var listed = Assert.Single(await store.ListAsync(StoreProjectId, CancellationToken.None));
        Assert.Equal(prior.ArtifactId, listed.ArtifactId);
        Assert.Equal(ReportArtifactFileState.AsPublished, listed.FileState);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now => now;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
