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
        Assert.Contains("INF 抽樣種子", exception.Message, StringComparison.Ordinal);
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
        Assert.Contains("舊版 JET 建立的案件", exception.Message, StringComparison.Ordinal);
        Assert.Contains(propertyName, exception.Message, StringComparison.Ordinal);
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
