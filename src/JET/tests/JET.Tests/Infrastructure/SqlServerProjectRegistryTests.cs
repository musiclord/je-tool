using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// <see cref="SqlServerProjectRegistry"/>（線上專案登記簿）的 Infrastructure 層測試。
/// 全部 [SqlServerFact] 閘控（連 JET_Test，非 Express、≥ 2022；不符即略過）。
/// 隔離策略：每測試用唯一 principal 與唯一 projectId（GUID），故共用 dbo.project_registry/
/// dbo.project_access 的跨測試殘留互不可見；finally 以 UnregisterAsync 清列。
/// oracle：登記簿語意規格（spec §3 表形 ＋ 埠契約）＋ ProjectDocument JSON round-trip。
/// </summary>
public sealed class SqlServerProjectRegistryTests
{
    private const string SingleDb = "JET_Test";

    private static ProjectDocument Doc(string projectId, string code = "PCODE") =>
        new(projectId, code, "受測實體", "op",
            "2024-01-01", "2024-12-31", null,
            ProjectDocument.DefaultMoneyScale, ProjectDocument.DefaultRoundingMode,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            CurrentStep: 1, ProjectDocument.CurrentSchemaVersion,
            ProjectDocument.SqlServerDatabaseProvider);

    /// <summary>取得指向 JET_Test 的 registry；並先確保單庫存在（registry 於其上 bootstrap 自表）。</summary>
    private static async Task<SqlServerProjectRegistry?> TryArrangeAsync()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null)
        {
            return null;
        }

        var options = new SqlServerConnectionOptions(conn, SingleDb);
        await new SqlServerProjectDatabase(options).EnsureDatabaseReadyAsync(CancellationToken.None);
        return new SqlServerProjectRegistry(options);
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [SqlServerFact]
    public async Task Bootstrap_RepeatedCalls_AreIdempotent()
    {
        var registry = await TryArrangeAsync();
        if (registry is null) { return; }

        // 連呼兩次 read 方法：第一次會 bootstrap 建表，第二次走既有表；兩次皆不得拋。
        var unknown = Unique("nobody");
        Assert.False(await registry.ExistsAsync(unknown, CancellationToken.None));
        Assert.False(await registry.ExistsAsync(unknown, CancellationToken.None));
    }

    [SqlServerFact]
    public async Task Register_ThenListVisible_ReturnsProjectForGrantedPrincipal()
    {
        var registry = await TryArrangeAsync();
        if (registry is null) { return; }

        var principal = Unique("user");
        var projectId = Unique("案件");
        try
        {
            await registry.RegisterAsync(Doc(projectId, "ENG-777"), principal, CancellationToken.None);

            var visible = await registry.ListVisibleAsync(principal, CancellationToken.None);

            var match = visible.Where(r => r.Document.ProjectId == projectId).ToList();
            Assert.Single(match);
            // round-trip：project_json 原樣入庫再取回，欄位一致。
            Assert.Equal("ENG-777", match[0].Document.ProjectCode);
            Assert.Equal(ProjectDocument.SqlServerDatabaseProvider, match[0].Document.DatabaseProvider);
            // created_utc 取自 registry 欄位（= doc 建立時間），供 serverOnly 排序。
            Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), match[0].CreatedUtc);
            Assert.Null(match[0].LastOpenedUtc); // 未 touch → null
        }
        finally
        {
            await registry.UnregisterAsync(projectId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task ListVisible_UnauthorizedPrincipal_SeesNothing()
    {
        var registry = await TryArrangeAsync();
        if (registry is null) { return; }

        var owner = Unique("owner");
        var stranger = Unique("stranger");
        var projectId = Unique("私案");
        try
        {
            await registry.RegisterAsync(Doc(projectId), owner, CancellationToken.None);

            var strangerView = await registry.ListVisibleAsync(stranger, CancellationToken.None);

            // 未被授權的 principal 看不到該案（ACL 過濾生效）。
            Assert.DoesNotContain(strangerView, r => r.Document.ProjectId == projectId);
        }
        finally
        {
            await registry.UnregisterAsync(projectId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task ExistsAsync_TrueAfterRegister_FalseForUnknown()
    {
        var registry = await TryArrangeAsync();
        if (registry is null) { return; }

        var principal = Unique("user");
        var projectId = Unique("案件");
        try
        {
            Assert.False(await registry.ExistsAsync(projectId, CancellationToken.None)); // 註冊前
            await registry.RegisterAsync(Doc(projectId), principal, CancellationToken.None);
            Assert.True(await registry.ExistsAsync(projectId, CancellationToken.None));   // 註冊後
            Assert.False(await registry.ExistsAsync(Unique("其他"), CancellationToken.None)); // 未知 id
        }
        finally
        {
            await registry.UnregisterAsync(projectId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task Unregister_ClearsRegistryAndAccess()
    {
        var registry = await TryArrangeAsync();
        if (registry is null) { return; }

        var principal = Unique("user");
        var projectId = Unique("待刪案");
        await registry.RegisterAsync(Doc(projectId), principal, CancellationToken.None);
        Assert.True(await registry.ExistsAsync(projectId, CancellationToken.None));
        Assert.Contains(
            await registry.ListVisibleAsync(principal, CancellationToken.None),
            r => r.Document.ProjectId == projectId);

        await registry.UnregisterAsync(projectId, CancellationToken.None);

        // registry 列已清。
        Assert.False(await registry.ExistsAsync(projectId, CancellationToken.None));
        // access 列一併清（否則會留下懸空授權）。
        Assert.DoesNotContain(
            await registry.ListVisibleAsync(principal, CancellationToken.None),
            r => r.Document.ProjectId == projectId);
    }

    [SqlServerFact]
    public async Task TouchLastOpened_SetsLastOpenedUtc()
    {
        var registry = await TryArrangeAsync();
        if (registry is null) { return; }

        var principal = Unique("user");
        var projectId = Unique("開啟案");
        try
        {
            await registry.RegisterAsync(Doc(projectId), principal, CancellationToken.None);
            Assert.Null(SingleFor(await registry.ListVisibleAsync(principal, CancellationToken.None), projectId).LastOpenedUtc);

            await registry.TouchLastOpenedAsync(projectId, CancellationToken.None);

            var touched = SingleFor(await registry.ListVisibleAsync(principal, CancellationToken.None), projectId);
            Assert.NotNull(touched.LastOpenedUtc);
        }
        finally
        {
            await registry.UnregisterAsync(projectId, CancellationToken.None);
        }
    }

    /// <summary>
    /// 交易性：重複 project_id 的第二次 Register 撞 PK 而整筆回滾——不得留下第二個 principal 的
    /// access 半套。以第一位授權者仍可見、第二位不可見來證明第二筆交易未寫入任何一行。
    /// </summary>
    [SqlServerFact]
    public async Task Register_DuplicateProjectId_RollsBackWithoutHalfWrite()
    {
        var registry = await TryArrangeAsync();
        if (registry is null) { return; }

        var first = Unique("first");
        var second = Unique("second");
        var projectId = Unique("撞名案");
        try
        {
            await registry.RegisterAsync(Doc(projectId), first, CancellationToken.None);

            // 第二次同 project_id → registry PK 衝突 → 整筆交易回滾。
            await Assert.ThrowsAnyAsync<Exception>(
                () => registry.RegisterAsync(Doc(projectId), second, CancellationToken.None));

            // 第一位授權仍完整。
            Assert.Contains(
                await registry.ListVisibleAsync(first, CancellationToken.None),
                r => r.Document.ProjectId == projectId);
            // 第二位的 access 未被寫入（交易性：不留半套）。
            Assert.DoesNotContain(
                await registry.ListVisibleAsync(second, CancellationToken.None),
                r => r.Document.ProjectId == projectId);
        }
        finally
        {
            await registry.UnregisterAsync(projectId, CancellationToken.None);
        }
    }

    /// <summary>
    /// SampleSeed 保全（控制面鐵律 / spec §6 ③）：registry 以 project_json 原樣 round-trip 整份 ProjectDocument,
    /// 故 INF 抽樣的 per-project 種子必須無損往返（本輪整併不改欄位拆解儲存,種子不遺失）。
    /// </summary>
    [SqlServerFact]
    public async Task Register_ThenListVisible_PreservesSampleSeedAndVersion()
    {
        var registry = await TryArrangeAsync();
        if (registry is null) { return; }

        var principal = Unique("user");
        var projectId = Unique("種子案");
        const long seed = 1_987_654_321L; // 落在 [1, 2147483646] 內的固定值
        try
        {
            await registry.RegisterAsync(
                Doc(projectId) with { SampleSeed = seed, SampleSeedVersion = 2 },
                principal,
                CancellationToken.None);

            var restored = SingleFor(await registry.ListVisibleAsync(principal, CancellationToken.None), projectId);
            Assert.Equal(seed, restored.Document.SampleSeed);
            Assert.Equal(2, restored.Document.SampleSeedVersion);
        }
        finally
        {
            await registry.UnregisterAsync(projectId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task UpdateDocument_PersistsCalendarStateForServerOnlyMaterialization()
    {
        var registry = await TryArrangeAsync();
        if (registry is null) { return; }

        var principal = Unique("user");
        var projectId = Unique("日期案");
        try
        {
            var original = Doc(projectId) with { CalendarImported = false };
            await registry.RegisterAsync(original, principal, CancellationToken.None);

            await registry.UpdateDocumentAsync(
                original with
                {
                    CalendarImported = true,
                    NonWorkingDays = Array.Empty<int>()
                },
                CancellationToken.None);

            var restored = SingleFor(
                await registry.ListVisibleAsync(principal, CancellationToken.None),
                projectId);
            Assert.True(restored.Document.CalendarImported);
            Assert.NotNull(restored.Document.NonWorkingDays);
            Assert.Empty(restored.Document.NonWorkingDays);
        }
        finally
        {
            await registry.UnregisterAsync(projectId, CancellationToken.None);
        }
    }

    private static RegisteredProject SingleFor(IReadOnlyList<RegisteredProject> list, string projectId)
    {
        var match = list.Where(r => r.Document.ProjectId == projectId).ToList();
        Assert.Single(match);
        return match[0];
    }
}
