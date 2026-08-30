using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 本地案件跨執行個體互斥的 Application 驗收。以兩個真 <see cref="HandlerTestHost"/> 共用同一 projects root，
/// 模擬兩個程序同時開啟同案；oracle 是 2026-07-14 本地收尾正本批次二、修補場與 manifest 的
/// project_locked 語意。
/// </summary>
public sealed class LocalProjectLockHandlerTests
{
    private const string ExternalProcessLockMessage =
        "此案件正由另一個 JET 執行個體開啟中，請先在該視窗離開案件後再試。";

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Create_LocalProjectHoldsLockUntilOwnerReleases(string provider)
    {
        // State Transition：create 已發布 active session 時就必須持鎖，不能等下一次 project.load 才補取。
        using var root = new TempProjectRoot();
        using var owner = new HandlerTestHost(projectsRootPath: root.Path);
        using var contender = new HandlerTestHost(projectsRootPath: root.Path);
        var projectId = NewProjectId(provider, "create");
        var projectDirectory = Path.Combine(root.Path, projectId);

        await owner.DispatchAsync("project.create", CreatePayload(projectId, provider));

        var loadError = await Record.ExceptionAsync(
            () => contender.DispatchAsync("project.load", LoadPayload(projectId)));
        var deleteError = await Record.ExceptionAsync(
            () => contender.DispatchAsync("project.delete", LoadPayload(projectId)));

        var loadHeld = Assert.IsType<JetActionException>(loadError);
        Assert.Equal(JetErrorCodes.ProjectLocked, loadHeld.Code);
        Assert.Equal(ExternalProcessLockMessage, loadHeld.Message);

        var deleteHeld = Assert.IsType<JetActionException>(deleteError);
        Assert.Equal(JetErrorCodes.ProjectLocked, deleteHeld.Code);
        Assert.Equal(ExternalProcessLockMessage, deleteHeld.Message);
        Assert.True(File.Exists(Path.Combine(projectDirectory, "project.json")));
        Assert.True(File.Exists(Path.Combine(projectDirectory, DatabaseFileName(provider))));

        await owner.DispatchAsync("project.releaseLock");
        var loaded = await contender.DispatchAsync("project.load", LoadPayload(projectId));
        Assert.Equal(projectId, loaded.GetProperty("project").GetProperty("projectId").GetString());

        await contender.DispatchAsync("project.releaseLock");
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Load_SameLocalProjectFromTwoHosts_HoldsUntilOwnerReleases(string provider)
    {
        using var root = new TempProjectRoot();
        using var owner = new HandlerTestHost(projectsRootPath: root.Path);
        using var contender = new HandlerTestHost(projectsRootPath: root.Path);
        var projectId = NewProjectId(provider, "load");

        await owner.DispatchAsync("project.create", CreatePayload(projectId, provider));
        await owner.DispatchAsync("project.load", LoadPayload(projectId));

        var held = await Assert.ThrowsAsync<JetActionException>(
            () => contender.DispatchAsync("project.load", LoadPayload(projectId)));
        Assert.Equal(JetErrorCodes.ProjectLocked, held.Code);
        Assert.Contains("開啟中", held.Message);

        // State Transition：心跳只續持有狀態，不得意外釋放本地檔案鎖。
        await owner.DispatchAsync("project.heartbeat");
        var heldAfterHeartbeat = await Assert.ThrowsAsync<JetActionException>(
            () => contender.DispatchAsync("project.load", LoadPayload(projectId)));
        Assert.Equal(JetErrorCodes.ProjectLocked, heldAfterHeartbeat.Code);

        await owner.DispatchAsync("project.releaseLock");
        var loaded = await contender.DispatchAsync("project.load", LoadPayload(projectId));
        Assert.Equal(projectId, loaded.GetProperty("project").GetProperty("projectId").GetString());

        await contender.DispatchAsync("project.releaseLock");
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Delete_LocalProjectHeldByOtherHost_RejectsWithoutDeletingData(string provider)
    {
        using var root = new TempProjectRoot();
        using var owner = new HandlerTestHost(projectsRootPath: root.Path);
        using var contender = new HandlerTestHost(projectsRootPath: root.Path);
        var projectId = NewProjectId(provider, "delete");
        var projectDirectory = Path.Combine(root.Path, projectId);

        await owner.DispatchAsync("project.create", CreatePayload(projectId, provider));
        await owner.DispatchAsync("project.load", LoadPayload(projectId));

        var held = await Assert.ThrowsAsync<JetActionException>(
            () => contender.DispatchAsync("project.delete", LoadPayload(projectId)));
        Assert.Equal(JetErrorCodes.ProjectLocked, held.Code);
        Assert.True(File.Exists(Path.Combine(projectDirectory, "project.json")));
        Assert.True(File.Exists(Path.Combine(projectDirectory, DatabaseFileName(provider))));
        Assert.Equal(projectId, SingleProjectId(await contender.DispatchAsync("project.list"), projectId));

        await owner.DispatchAsync("project.releaseLock");
        var deleted = await contender.DispatchAsync("project.delete", LoadPayload(projectId));
        Assert.True(deleted.GetProperty("ok").GetBoolean());
        Assert.False(Directory.Exists(projectDirectory));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Delete_CaseVariantLocalProject_UsesCanonicalIdForSessionAndProviderCache(string provider)
    {
        // State Transition：Windows 路徑大小寫不敏感；payload casing 不得分裂 session 或 provider cache 身分。
        using var root = new TempProjectRoot();
        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        var projectId = $"Local-Lock-{provider}-Canonical-{Guid.NewGuid():N}";
        var caseVariant = projectId.ToLowerInvariant();
        var replacementProvider = provider == ProjectDocument.DuckDbDatabaseProvider
            ? ProjectDocument.DefaultDatabaseProvider
            : ProjectDocument.DuckDbDatabaseProvider;

        await host.DispatchAsync("project.create", CreatePayload(projectId, provider));

        var deleted = await host.DispatchAsync("project.delete", LoadPayload(caseVariant));

        Assert.Equal(projectId, deleted.GetProperty("projectId").GetString());
        var noSession = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.saveProgress", """{ "currentStep": 2 }"""));
        Assert.Equal(JetErrorCodes.NoActiveProject, noSession.Code);

        // 同名重建改選另一個本地 provider，必須重讀新 project.json，不得沿用刪案前的 cache。
        await host.DispatchAsync("project.create", CreatePayload(projectId, replacementProvider));
        Assert.True(File.Exists(Path.Combine(root.Path, projectId, DatabaseFileName(replacementProvider))));
        Assert.False(File.Exists(Path.Combine(root.Path, projectId, DatabaseFileName(provider))));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Load_ResumeAssemblyFails_DoesNotPublishSessionOrLastOpenedAndReleasesLock(string provider)
    {
        using var root = new TempProjectRoot();
        var projectId = NewProjectId(provider, "resume-failure");
        var projectDirectory = Path.Combine(root.Path, projectId);

        using (var creator = new HandlerTestHost(projectsRootPath: root.Path))
        {
            await creator.DispatchAsync("project.create", CreatePayload(projectId, provider));
        }

        var artifactManifest = Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName);
        await File.WriteAllTextAsync(artifactManifest, "{");

        using var failingHost = new HandlerTestHost(projectsRootPath: root.Path);
        var loadError = await Assert.ThrowsAsync<JetActionException>(
            () => failingHost.DispatchAsync("project.load", LoadPayload(projectId)));
        Assert.Equal(JetErrorCodes.FileReadError, loadError.Code);

        var sessionError = await Assert.ThrowsAsync<JetActionException>(
            () => failingHost.DispatchAsync("project.saveProgress", """{ "currentStep": 2 }"""));
        Assert.Equal(JetErrorCodes.NoActiveProject, sessionError.Code);

        var listed = SingleProject(await failingHost.DispatchAsync("project.list"), projectId);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("lastOpenedUtc").ValueKind);

        File.Delete(artifactManifest);
        using var retryingHost = new HandlerTestHost(projectsRootPath: root.Path);
        var retried = await retryingHost.DispatchAsync("project.load", LoadPayload(projectId));
        Assert.Equal(projectId, retried.GetProperty("project").GetProperty("projectId").GetString());

        await retryingHost.DispatchAsync("project.releaseLock");
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Load_SameProjectReloadFails_PreservesPreviouslyHeldLock(string provider)
    {
        using var root = new TempProjectRoot();
        using var owner = new HandlerTestHost(projectsRootPath: root.Path);
        using var contender = new HandlerTestHost(projectsRootPath: root.Path);
        var projectId = NewProjectId(provider, "reload-failure");
        var artifactManifest = Path.Combine(
            root.Path,
            projectId,
            ProjectReportArtifactStore.ManifestFileName);

        await owner.DispatchAsync("project.create", CreatePayload(projectId, provider));
        await owner.DispatchAsync("project.load", LoadPayload(projectId));
        await File.WriteAllTextAsync(artifactManifest, "{");

        var reloadError = await Assert.ThrowsAsync<JetActionException>(
            () => owner.DispatchAsync("project.load", LoadPayload(projectId)));
        Assert.Equal(JetErrorCodes.FileReadError, reloadError.Code);

        var held = await Assert.ThrowsAsync<JetActionException>(
            () => contender.DispatchAsync("project.load", LoadPayload(projectId)));
        Assert.Equal(JetErrorCodes.ProjectLocked, held.Code);

        File.Delete(artifactManifest);
        await owner.DispatchAsync("project.releaseLock");
        var loaded = await contender.DispatchAsync("project.load", LoadPayload(projectId));
        Assert.Equal(projectId, loaded.GetProperty("project").GetProperty("projectId").GetString());
        await contender.DispatchAsync("project.releaseLock");
    }

    private static string NewProjectId(string provider, string scenario) =>
        $"local-lock-{provider}-{scenario}-{Guid.NewGuid():N}";

    private static string CreatePayload(string projectId, string provider) =>
        $$"""
        {
          "caseName": "{{projectId}}",
          "projectCode": "LOCAL-LOCK",
          "entityName": "本地互斥測試",
          "operatorId": "auditor",
          "periodStart": "2025-01-01",
          "periodEnd": "2025-12-31",
          "databaseProvider": "{{provider}}"
        }
        """;

    private static string LoadPayload(string projectId) =>
        $$"""{ "projectId": "{{projectId}}" }""";

    private static string DatabaseFileName(string provider) =>
        provider == ProjectDocument.DuckDbDatabaseProvider
            ? DuckDbProjectDatabase.DatabaseFileName
            : JetProjectFolder.DatabaseFileName;

    private static string SingleProjectId(JsonElement listResponse, string projectId) =>
        SingleProject(listResponse, projectId).GetProperty("projectId").GetString()!;

    private static JsonElement SingleProject(JsonElement listResponse, string projectId)
    {
        var matches = listResponse.GetProperty("projects").EnumerateArray()
            .Where(project => project.GetProperty("projectId").GetString() == projectId)
            .ToArray();
        return Assert.Single(matches);
    }
}
