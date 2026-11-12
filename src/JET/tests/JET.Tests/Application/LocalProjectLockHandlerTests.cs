using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 本地案件跨執行個體互斥的 Application 驗收。以兩個真 <see cref="HandlerTestHost"/> 共用同一 projects root，
/// 模擬兩個程序同時開啟同案；oracle 是錯誤碼
/// project_locked 的語意。
/// </summary>
public sealed class LocalProjectLockHandlerTests
{
    // 這句的用字已定案。
    private const string ExternalProcessLockMessage =
        "此案件已在另一個 JET 視窗中開啟，請先在該視窗離開案件後再試一次。";

    [Theory]
    [InlineData("sqlite")]
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
        // 這句的用字已定案，核對整句。
        Assert.Equal(ExternalProcessLockMessage, held.Message);

        await owner.DispatchAsync("project.releaseLock");
        var loaded = await contender.DispatchAsync("project.load", LoadPayload(projectId));
        Assert.Equal(projectId, loaded.GetProperty("project").GetProperty("projectId").GetString());

        await contender.DispatchAsync("project.releaseLock");
    }

    [Theory]
    [InlineData("sqlite")]
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
        Assert.Equal(projectId, SingleProjectId(await contender.DispatchAsync("project.listLocal"), projectId));

        await owner.DispatchAsync("project.releaseLock");
        var deleted = await contender.DispatchAsync("project.delete", LoadPayload(projectId));
        Assert.True(deleted.GetProperty("ok").GetBoolean());
        Assert.False(Directory.Exists(projectDirectory));
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task Delete_CaseVariantLocalProject_UsesCanonicalIdForSessionAndProviderCache(string provider)
    {
        // State Transition：Windows 路徑大小寫不敏感；payload casing 不得分裂 session 或 provider cache 身分。
        using var root = new TempProjectRoot();
        using var host = new HandlerTestHost(projectsRootPath: root.Path);
        var projectId = $"Local-Lock-{provider}-Canonical-{Guid.NewGuid():N}";
        var caseVariant = projectId.ToLowerInvariant();

        await host.DispatchAsync("project.create", CreatePayload(projectId, provider));

        var deleted = await host.DispatchAsync("project.delete", LoadPayload(caseVariant));

        Assert.Equal(projectId, deleted.GetProperty("projectId").GetString());
        var noSession = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.saveProgress", """{ "currentStep": 2 }"""));
        Assert.Equal(JetErrorCodes.NoActiveProject, noSession.Code);

        // 同名重建 SQLite 案件，驗證刪案後的同名新案件照常建立資料庫檔。
        await host.DispatchAsync("project.create", CreatePayload(projectId, provider));
        Assert.True(File.Exists(Path.Combine(root.Path, projectId, DatabaseFileName(provider))));
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task Load_ResumeAssemblyFails_DoesNotPublishSessionOrLastOpenedAndReleasesLock(string provider)
    {
        using var root = new TempProjectRoot();
        var projectId = NewProjectId(provider, "resume-failure");
        var projectDirectory = Path.Combine(root.Path, projectId);

        using (var creator = new HandlerTestHost(projectsRootPath: root.Path))
        {
            await creator.DispatchAsync("project.create", CreatePayload(projectId, provider));
        }

        // 同上：以唯讀 project.json 讓載入在取鎖之後失敗。
        var projectJson = Path.Combine(projectDirectory, JetProjectFolder.ProjectJsonFileName);
        File.SetAttributes(projectJson, FileAttributes.ReadOnly);

        using var failingHost = new HandlerTestHost(projectsRootPath: root.Path);
        try
        {
            var loadError = await Assert.ThrowsAsync<JetActionException>(
                () => failingHost.DispatchAsync("project.load", LoadPayload(projectId)));
            Assert.Equal(JetErrorCodes.FileReadError, loadError.Code);

            var sessionError = await Assert.ThrowsAsync<JetActionException>(
                () => failingHost.DispatchAsync("project.saveProgress", """{ "currentStep": 2 }"""));
            Assert.Equal(JetErrorCodes.NoActiveProject, sessionError.Code);

            var listed = SingleProject(await failingHost.DispatchAsync("project.listLocal"), projectId);
            Assert.Equal(JsonValueKind.Null, listed.GetProperty("lastOpenedUtc").ValueKind);
        }
        finally
        {
            File.SetAttributes(projectJson, FileAttributes.Normal);
        }

        using var retryingHost = new HandlerTestHost(projectsRootPath: root.Path);
        var retried = await retryingHost.DispatchAsync("project.load", LoadPayload(projectId));
        Assert.Equal(projectId, retried.GetProperty("project").GetProperty("projectId").GetString());

        await retryingHost.DispatchAsync("project.releaseLock");
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task Load_SameProjectReloadFails_PreservesPreviouslyHeldLock(string provider)
    {
        using var root = new TempProjectRoot();
        using var owner = new HandlerTestHost(projectsRootPath: root.Path);
        using var contender = new HandlerTestHost(projectsRootPath: root.Path);
        var projectId = NewProjectId(provider, "reload-failure");
        // 壞掉的報告清單不會擋載入，所以用唯讀的 project.json 讓載入在取鎖之後、
        // 寫回 LastOpened 時失敗；測的是「取鎖後失敗」的鎖行為。
        var projectJson = Path.Combine(root.Path, projectId, JetProjectFolder.ProjectJsonFileName);

        await owner.DispatchAsync("project.create", CreatePayload(projectId, provider));
        await owner.DispatchAsync("project.load", LoadPayload(projectId));
        File.SetAttributes(projectJson, FileAttributes.ReadOnly);
        try
        {
            var reloadError = await Assert.ThrowsAsync<JetActionException>(
                () => owner.DispatchAsync("project.load", LoadPayload(projectId)));
            Assert.Equal(JetErrorCodes.FileReadError, reloadError.Code);

            var held = await Assert.ThrowsAsync<JetActionException>(
                () => contender.DispatchAsync("project.load", LoadPayload(projectId)));
            Assert.Equal(JetErrorCodes.ProjectLocked, held.Code);
        }
        finally
        {
            // 斷言失敗時也要還原屬性，否則 TempProjectRoot 清不掉資料夾，會蓋掉真正的失敗原因。
            File.SetAttributes(projectJson, FileAttributes.Normal);
        }

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
          "projectName": "{{projectId}}",
          "projectCode": "LOCAL-LOCK",
          "clientName": "本地互斥測試",
          "operatorId": "auditor",
          "periodStart": "2025-01-01",
          "periodEnd": "2025-12-31",
          "databaseProvider": "{{provider}}"
        }
        """;

    private static string LoadPayload(string projectId) =>
        $$"""{ "projectId": "{{projectId}}" }""";

    private static string DatabaseFileName(string provider) =>
        JetProjectFolder.DatabaseFileName;

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
