using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class JsonFileProjectStoreTests
{
    private static ProjectDocument NewDocument(string? id = null, DateTimeOffset? createdUtc = null) => new(
        id ?? Guid.NewGuid().ToString("N"),
        "ENG-2024-001",
        "範例股份有限公司",
        "auditor01",
        "2024-01-01",
        "2024-12-31",
        "2024-12-31",
        ProjectDocument.DefaultMoneyScale,
        ProjectDocument.DefaultRoundingMode,
        createdUtc ?? DateTimeOffset.UtcNow,
        CurrentStep: 1,
        ProjectDocument.CurrentSchemaVersion,
        // 目前版本建案一定寫入 INF 抽樣種子與版本；缺欄位的文件會被當成舊版案件拒絕。
        SampleSeed: 1_234_567,
        SampleSeedVersion: JetAuditProgram.CurrentInfSamplingAlgorithmVersion);

    [Fact]
    public async Task CreateListFindRoundTrip()
    {
        using var root = new TempProjectRoot();
        var store = new JsonFileProjectStore(new JetProjectFolder(root.Path));

        var older = NewDocument(createdUtc: DateTimeOffset.UtcNow.AddHours(-1));
        var newer = NewDocument(createdUtc: DateTimeOffset.UtcNow);

        await store.CreateAsync(older, CancellationToken.None);
        await store.CreateAsync(newer, CancellationToken.None);

        var listed = await store.ListAsync(CancellationToken.None);
        Assert.Equal(2, listed.Count);
        Assert.Equal(newer.ProjectId, listed[0].ProjectId); // createdUtc desc

        var found = await store.FindAsync(older.ProjectId, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(older.ProjectCode, found.ProjectCode);
        Assert.Equal(older.EntityName, found.EntityName);
        Assert.Equal(older.MoneyScale, found.MoneyScale);
        Assert.Equal(older.RoundingMode, found.RoundingMode);
        Assert.Equal(older.PeriodStart, found.PeriodStart);
        Assert.Equal(older.LastAccountingPeriodDate, found.LastAccountingPeriodDate);
    }

    [Fact]
    public async Task CreateAsync_WhenCanceledDuringStaging_LeavesNoPublishedFolderOrStagingResidue()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var store = new JsonFileProjectStore(folder);
        var document = NewDocument();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CreateAsync(document, cancellation.Token));

        Assert.False(Directory.Exists(folder.GetProjectDirectory(document.ProjectId)));
        Assert.Empty(Directory.EnumerateDirectories(
            root.Path,
            ".project-create-*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CreateAsync_ConcurrentSameId_PublishesOneCompleteDirectoryWithoutStagingResidue()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var store = new JsonFileProjectStore(folder);
        var projectId = Guid.NewGuid().ToString("N");
        var first = NewDocument(projectId) with { ProjectCode = "SYNTHETIC-FIRST" };
        var second = NewDocument(projectId) with { ProjectCode = "SYNTHETIC-SECOND" };

        var errors = await Task.WhenAll(
            Record.ExceptionAsync(() => store.CreateAsync(first, CancellationToken.None)).AsTask(),
            Record.ExceptionAsync(() => store.CreateAsync(second, CancellationToken.None)).AsTask());

        Assert.Equal(1, errors.Count(error => error is null));
        var collision = Assert.Single(errors.OfType<ProjectStoreCollisionException>());
        Assert.Equal(projectId, collision.ProjectId);
        var published = await store.FindAsync(projectId, CancellationToken.None);
        Assert.NotNull(published);
        Assert.Contains(
            published.ProjectCode,
            new[] { "SYNTHETIC-FIRST", "SYNTHETIC-SECOND" });
        var publishedFiles = Directory.GetFiles(folder.GetProjectDirectory(projectId));
        Assert.Single(publishedFiles);
        Assert.Equal(JetProjectFolder.ProjectJsonFileName, Path.GetFileName(publishedFiles[0]));
        Assert.Empty(Directory.EnumerateDirectories(
            root.Path,
            ".project-create-*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CalendarImportedMarker_RoundTripsThroughProjectDocument()
    {
        using var root = new TempProjectRoot();
        var store = new JsonFileProjectStore(new JetProjectFolder(root.Path));
        var document = NewDocument() with { CalendarImported = true };

        await store.CreateAsync(document, CancellationToken.None);

        var found = await store.FindAsync(document.ProjectId, CancellationToken.None);
        Assert.NotNull(found);
        Assert.True(found.CalendarImported);
    }

    [Fact]
    public async Task CorruptJson_SkippedByList_FindReportsFileReadError()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var store = new JsonFileProjectStore(folder);

        var good = NewDocument();
        await store.CreateAsync(good, CancellationToken.None);

        var corruptId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(corruptId));
        await File.WriteAllTextAsync(folder.GetProjectJsonPath(corruptId), "{ not valid json");

        var listed = await store.ListAsync(CancellationToken.None);
        Assert.Single(listed);
        Assert.Equal(good.ProjectId, listed[0].ProjectId);

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => store.FindAsync(corruptId, CancellationToken.None));
        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
    }

    [Theory]
    [InlineData("\"broken\"")]
    [InlineData("999999999999999999999999999999")]
    public async Task CorruptSampleSeedFormatOrLength_FindReportsExplicitSeedError(string corruptJsonValue)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var store = new JsonFileProjectStore(folder);
        var document = NewDocument() with { SampleSeed = 7, SampleSeedVersion = 2 };
        await store.CreateAsync(document, CancellationToken.None);
        var path = folder.GetProjectJsonPath(document.ProjectId);
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(
            path,
            json.Replace("\"sampleSeed\": 7", $"\"sampleSeed\": {corruptJsonValue}"));

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            store.FindAsync(document.ProjectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        // 使用者 2026-10-14 第三輪勾選頁確認 R3-51（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。 不寫鍵名與種子值。
        Assert.Equal(
            $"案件「{document.ProjectId}」設定檔中的抽樣設定已損壞。為避免改變抽樣結果，JET 不會重新產生，請從備份還原案件資料夾。",
            exception.Message);
    }

    [Theory]
    [InlineData("sampleSeedVersion", null)]
    [InlineData("sampleSeedVersion", 1)]
    [InlineData("databaseProvider", null)]
    public async Task LegacyProjectJson_FindRejectsAsOldProject_ListSkipsIt(
        string propertyName,
        int? legacyValue)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var store = new JsonFileProjectStore(folder);
        var good = NewDocument();
        var legacy = NewDocument();
        await store.CreateAsync(good, CancellationToken.None);
        await store.CreateAsync(legacy, CancellationToken.None);
        var path = folder.GetProjectJsonPath(legacy.ProjectId);
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.True(node.ContainsKey(propertyName));
        if (legacyValue is null)
        {
            // 舊版 JET 建立的 project.json 沒有這個欄位。
            node.Remove(propertyName);
        }
        else
        {
            node[propertyName] = legacyValue.Value;
        }
        await File.WriteAllTextAsync(path, node.ToJsonString());

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            store.FindAsync(legacy.ProjectId, CancellationToken.None));
        Assert.Equal(JetErrorCodes.InvalidProjectSchema, exception.Code);
        // 使用者 2026-10-14 第三輪勾選頁確認 R3-51（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。 不寫檔名與缺了哪個設定。
        Assert.Equal(
            $"案件「{legacy.ProjectId}」是舊版 JET 建立的，目前版本無法讀取。請用目前版本重新建立案件，再重新匯入資料。",
            exception.Message);
        Assert.DoesNotContain(propertyName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("請用目前版本重新建立案件", exception.Message, StringComparison.Ordinal);

        var listed = await store.ListAsync(CancellationToken.None);
        Assert.Equal(good.ProjectId, Assert.Single(listed).ProjectId);
    }

    [Fact]
    public async Task Save_OpenReaderKeepsOriginalSnapshot_AndPathContainsCompleteReplacement()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var store = new JsonFileProjectStore(folder);
        var original = NewDocument();
        await store.CreateAsync(original, CancellationToken.None);
        var path = folder.GetProjectJsonPath(original.ProjectId);

        await using var openReader = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(openReader, leaveOpen: true);

        var replacement = original with { EntityName = "原子取代後的公司" };
        await store.SaveAsync(replacement, CancellationToken.None);

        openReader.Position = 0;
        var readerSnapshot = await reader.ReadToEndAsync(CancellationToken.None);
        var currentPathContent = await File.ReadAllTextAsync(path, CancellationToken.None);
        Assert.Contains(original.EntityName, readerSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(replacement.EntityName, readerSnapshot, StringComparison.Ordinal);
        Assert.Contains(replacement.EntityName, currentPathContent, StringComparison.Ordinal);
        Assert.DoesNotContain(original.EntityName, currentPathContent, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(folder.GetProjectDirectory(original.ProjectId), "*.tmp"));
    }

    /// <summary>
    /// 2026-10-11 交錯操作檢查：切換步驟寫入 project.json（project.saveProgress）的同時，
    /// 資料預覽讀 project.json（query.dataPreview，併行 action），讀取被誤報成「無法讀取或解析」。
    /// 寫入型 action 由 dispatcher 序列化，但唯讀查詢可和它們同時執行，所以讀寫必須互不干擾。
    /// </summary>
    [Fact]
    public async Task FindDuringConcurrentSaves_AlwaysReturnsACompleteDocument()
    {
        using var root = new TempProjectRoot();
        var store = new JsonFileProjectStore(new JetProjectFolder(root.Path));
        var original = NewDocument();
        await store.CreateAsync(original, CancellationToken.None);
        var names = new[] { "交錯寫入甲公司", "交錯寫入乙公司" };
        using var stop = new CancellationTokenSource();

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 400; i++)
            {
                await store.SaveAsync(original with { EntityName = names[i % 2], CurrentStep = i % 6 }, CancellationToken.None);
            }

            stop.Cancel();
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                var found = await store.FindAsync(original.ProjectId, CancellationToken.None);
                Assert.NotNull(found);
                Assert.Contains(found.EntityName, names.Append(original.EntityName));
                reads++;
            }

            return reads;
        })).ToArray();

        await writer;
        var totalReads = (await Task.WhenAll(readers)).Sum();
        Assert.True(totalReads > 0);
    }

    /// <summary>
    /// 換檔不是一步完成：<c>File.Replace</c> 進行中，正式檔名會有一小段時間不存在。
    /// 2026-10-12 的完整測試裡，上面的交錯讀寫測試失敗過一次：讀取在等寫入完成之前就判斷檔案在不在，把存在的案件讀成找不到。
    /// 這裡拿寫入用的同一把鎖、把檔案暫時移開，固定重現換到一半的狀態，不靠時機碰運氣。
    /// </summary>
    [Fact]
    public async Task Find_WhileASaveIsSwappingTheFile_WaitsAndReturnsTheDocument()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var store = new JsonFileProjectStore(folder);
        var original = NewDocument();
        await store.CreateAsync(original, CancellationToken.None);

        var found = await WhileSwappingAsync(
            folder,
            original.ProjectId,
            () => store.FindAsync(original.ProjectId, CancellationToken.None));

        Assert.NotNull(found);
        Assert.Equal(original.EntityName, found.EntityName);
    }

    /// <summary>
    /// 案件清單也一樣：正在儲存的案件不能因為換檔的一瞬間而從清單上消失；
    /// 名稱合法、卻沒有 project.json 的資料夾仍然不列為案件。
    /// </summary>
    [Fact]
    public async Task List_WhileASaveIsSwappingTheFile_KeepsTheProjectListed()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var store = new JsonFileProjectStore(folder);
        var original = NewDocument();
        await store.CreateAsync(original, CancellationToken.None);
        Directory.CreateDirectory(folder.GetProjectDirectory(Guid.NewGuid().ToString("N")));

        var listed = await WhileSwappingAsync(
            folder,
            original.ProjectId,
            () => store.ListAsync(CancellationToken.None));

        Assert.Equal(original.ProjectId, Assert.Single(listed).ProjectId);
    }

    /// <summary>
    /// 拿著寫入用的同一把鎖、把 project.json 暫時移開，在這段期間開始 <paramref name="action"/>；
    /// 放回檔案、放開鎖之後再等它完成。讀取若不等鎖，會在檔案移開時就直接回傳。
    /// </summary>
    private static async Task<T> WhileSwappingAsync<T>(
        JetProjectFolder folder,
        string projectId,
        Func<Task<T>> action)
    {
        var path = folder.GetProjectJsonPath(projectId);
        var aside = path + ".swapping";
        var gate = JsonFileProjectStore.GateFor(path);
        await gate.WaitAsync(CancellationToken.None);
        Task<T> pending;
        try
        {
            File.Move(path, aside);
            pending = action();
            File.Move(aside, path);
        }
        finally
        {
            gate.Release();
        }

        return await pending;
    }

    /// <summary>
    /// 另一個程式（例如另一個 JET 視窗的案件清單、備份或防毒軟體）短暫開著 project.json、沒有允許刪除時，
    /// 換檔會遇到共用違規；對方很快關檔，儲存應在短暫重試後完成，不要直接告訴審計員檔案被鎖住。
    /// </summary>
    [Fact]
    public async Task Save_WhenAnotherReaderBrieflyHoldsTheFile_SucceedsAfterTheReaderCloses()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var store = new JsonFileProjectStore(folder);
        var original = NewDocument();
        await store.CreateAsync(original, CancellationToken.None);
        var path = folder.GetProjectJsonPath(original.ProjectId);

        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(60);
            await holder.DisposeAsync();
        });
        var replacement = original with { EntityName = "對方關檔後寫入的公司" };

        await store.SaveAsync(replacement, CancellationToken.None);
        await release;

        var found = await store.FindAsync(original.ProjectId, CancellationToken.None);
        Assert.Equal(replacement.EntityName, found!.EntityName);
        Assert.Empty(Directory.GetFiles(folder.GetProjectDirectory(original.ProjectId), "*.tmp"));
    }

    [Fact]
    public async Task Find_OrphanedTemporaryFileExists_LoadsIntactOfficialDocument()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var store = new JsonFileProjectStore(folder);
        var document = NewDocument();
        await store.CreateAsync(document, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(folder.GetProjectDirectory(document.ProjectId), ".project-json-interrupted.tmp"),
            "{\"projectId\":",
            CancellationToken.None);

        var found = await store.FindAsync(document.ProjectId, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(document.ProjectId, found.ProjectId);
        Assert.Equal(document.EntityName, found.EntityName);
    }

    [Fact]
    public async Task InvalidProjectId_FindReturnsNull()
    {
        using var root = new TempProjectRoot();
        var store = new JsonFileProjectStore(new JetProjectFolder(root.Path));

        Assert.Null(await store.FindAsync(@"..\..\evil", CancellationToken.None));
        Assert.Null(await store.FindAsync("not-a-guid", CancellationToken.None));
    }
}
