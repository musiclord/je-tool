using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 查核期間、金額存放倍率、民國年選項、隨機種子、建案者、期末財報準備日與每週休息日以案件資料庫為準。
/// 使用者可以用記事本改案件設定檔，改不了案件資料庫：作業一律照資料庫的值；重新開案時把設定檔改回資料庫的值，
/// 並列出哪些項目被改回。這裡用合成案件模擬在 JET 以外改設定檔。
/// </summary>
public sealed class ProtectedProjectSettingsTests
{
    private static readonly string[] AllProtectedKeys =
    [
        "periodStart", "periodEnd", "prepStartDate", "nonWorkingDays",
        "operatorId", "sampleSeed", "moneyScale", "rocDateEnabled"
    ];

    [Fact]
    public async Task SettingsFileEditedOutsideJet_DoesNotChangeWhatTheOpenCaseUses()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host);
        var original = await ReadSettingsFileAsync(host, projectId);
        var seedBefore = (await host.DispatchAsync("validate.run"))
            .GetProperty("infSamplingTest").GetProperty("seed").GetInt64();

        await EditEverySettingAsync(host, projectId, original);

        var validation = await host.DispatchAsync("validate.run");
        Assert.Equal(seedBefore, validation.GetProperty("infSamplingTest").GetProperty("seed").GetInt64());

        var updated = await host.DispatchAsync("project.update", JsonSerializer.Serialize(new
        {
            projectId,
            clientName = "合成客戶改名"
        }));
        AssertProjectMatchesOriginal(updated.GetProperty("project"), original);

        // 下一次 JET 存設定檔時，連同改過的項目一起改回資料庫的值。
        var saved = await ReadSettingsFileAsync(host, projectId);
        AssertProtectedFieldsEqual(original, saved);
        Assert.Equal("合成客戶改名", saved.ClientName);
    }

    [Fact]
    public async Task ReopeningAfterSettingsFileEdit_RestoresTheFileAndListsTheChangedItems()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host);
        var original = await ReadSettingsFileAsync(host, projectId);

        var untouched = await LoadAsync(host, projectId);
        Assert.Empty(untouched.GetProperty("settingsRestored").EnumerateArray());

        await EditEverySettingAsync(host, projectId, original);

        var reopened = await LoadAsync(host, projectId);
        Assert.Equal(AllProtectedKeys, Keys(reopened.GetProperty("settingsRestored")));
        AssertProjectMatchesOriginal(reopened.GetProperty("project"), original);
        Assert.Equal(
            NonWorkingDays.Resolve(original.NonWorkingDays),
            reopened.GetProperty("importState").GetProperty("calendar").GetProperty("nonWorkingDays").EnumerateArray().Select(day => day.GetInt32()));

        var restored = await ReadSettingsFileAsync(host, projectId);
        AssertProtectedFieldsEqual(original, restored);

        var again = await LoadAsync(host, projectId);
        Assert.Empty(again.GetProperty("settingsRestored").EnumerateArray());
    }

    [Fact]
    public async Task PreparationDateAndNonWorkingDays_ChangedInJet_AreRecordedInTheCaseDatabase()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host);
        await host.DispatchAsync("project.update", JsonSerializer.Serialize(new
        {
            projectId,
            prepStartDate = "2025-10-01"
        }));
        await host.DispatchAsync("calendar.setNonWorkingDays", JsonSerializer.Serialize(new { days = new[] { 0, 5 } }));

        await EditSettingsFileAsync(host, projectId, node =>
        {
            node["lastAccountingPeriodDate"] = "2025-09-01";
            node["nonWorkingDays"] = new JsonArray(0, 6);
        });

        var reopened = await LoadAsync(host, projectId);
        Assert.Equal(["prepStartDate", "nonWorkingDays"], Keys(reopened.GetProperty("settingsRestored")));
        Assert.Equal("2025-10-01", reopened.GetProperty("project").GetProperty("prepStartDate").GetString());
        Assert.Equal(
            [0, 5],
            reopened.GetProperty("importState").GetProperty("calendar").GetProperty("nonWorkingDays").EnumerateArray().Select(day => day.GetInt32()));
    }

    // 每週休息日的存檔：設定檔存不進去時，案件資料庫記的休息日一起回復，兩邊都還是舊值。
    [Fact]
    public async Task NonWorkingDays_WhenSettingsCannotBeSaved_KeepTheOldDaysInBothPlaces()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host);
        var settings = Path.Combine(host.ProjectsRoot, projectId, "project.json");
        using (new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() =>
                host.DispatchAsync("calendar.setNonWorkingDays", JsonSerializer.Serialize(new { days = new[] { 0, 5 } })));
            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        }

        var reopened = await LoadAsync(host, projectId);
        Assert.Empty(reopened.GetProperty("settingsRestored").EnumerateArray());
        Assert.False(reopened.GetProperty("importState").GetProperty("calendar").GetProperty("nonWorkingDaysConfigured").GetBoolean());
    }

    // 開啟中改了設定檔的資料庫種類，作業照樣用案件資料庫記的設定，不因為種類不同就改用設定檔的值。
    [Fact]
    public async Task SettingsFileWithAnotherDatabaseKind_StillUsesTheCaseDatabaseValues()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host);
        var seedBefore = (await host.DispatchAsync("validate.run"))
            .GetProperty("infSamplingTest").GetProperty("seed").GetInt64();

        await EditSettingsFileAsync(host, projectId, node =>
        {
            node["databaseProvider"] = "SQLite";
            node["sampleSeed"] = seedBefore == 1 ? 2 : seedBefore - 1;
        });

        var validation = await host.DispatchAsync("validate.run");
        Assert.Equal(seedBefore, validation.GetProperty("infSamplingTest").GetProperty("seed").GetInt64());
    }

    // 建案在設定檔發布之後、資料庫建好之前中斷時，使用者沒有拿到這個案件。開案照建案的做法把資料庫建完：
    // 種子重新產生，建案者取開案的 Windows 身分，金額存放倍率與民國年選項用建案的預設值，不採用設定檔裡的這些值；
    // 查核期間與期末財報準備日是建案時填的，照設定檔。之後照常以資料庫為準。
    [Fact]
    public async Task InterruptedCreate_RecordsNewCaseValuesInsteadOfTheSettingsFile()
    {
        using var root = new TempProjectRoot();
        const string name = "interrupted-protected-settings";
        var folder = new JetProjectFolder(root.Path);
        const string principal = "EXAMPLE\\protected.creator";
        using (var creator = new HandlerTestHost(principalName: principal, projectsRootPath: root.Path))
        {
            await creator.DispatchAsync("project.create", JsonSerializer.Serialize(new
            {
                projectName = name,
                periodStart = "2025-01-01", periodEnd = "2025-12-31", prepStartDate = "2025-09-01",
                databaseProvider = ProjectDocument.DefaultDatabaseProvider
            }));
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var databasePath = folder.GetDatabasePath(name);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(databasePath + suffix);
        }
        await File.WriteAllBytesAsync(folder.GetCreationMarkerPath(name), []);

        using var host = new HandlerTestHost(principalName: principal, projectsRootPath: root.Path);
        var created = await ReadSettingsFileAsync(host, name);
        const long editedSeed = 12_345;
        await EditSettingsFileAsync(host, name, node =>
        {
            node["sampleSeed"] = editedSeed;
            node["operatorId"] = "合成的另一位建案者";
            node["moneyScale"] = 100;
            node["rocDateEnabled"] = false;
            node["nonWorkingDays"] = new JsonArray(1, 2);
        });

        var first = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = name }));
        Assert.Empty(first.GetProperty("settingsRestored").EnumerateArray());
        var project = first.GetProperty("project");
        Assert.Equal("2025-09-01", project.GetProperty("prepStartDate").GetString());
        Assert.Equal(created.OperatorId, project.GetProperty("operatorId").GetString());
        Assert.Equal(ProjectDocument.DefaultMoneyScale, project.GetProperty("moneyScale").GetInt32());
        Assert.True(project.GetProperty("rocDateEnabled").GetBoolean());
        Assert.False(first.GetProperty("importState").GetProperty("calendar").GetProperty("nonWorkingDaysConfigured").GetBoolean());
        var saved = await ReadSettingsFileAsync(host, name);
        Assert.NotEqual(editedSeed, saved.SampleSeed);
        Assert.Equal(created.OperatorId, saved.OperatorId);

        await EditSettingsFileAsync(host, name, node => node["periodEnd"] = "2025-06-30");
        var second = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = name }));
        Assert.Equal(["periodEnd"], Keys(second.GetProperty("settingsRestored")));
        Assert.Equal("2025-12-31", second.GetProperty("project").GetProperty("periodEnd").GetString());
    }

    private static Task<string> SetupAsync(HandlerTestHost host) =>
        InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "借方", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "貸方", "100.00", 0),
            prepStartDate: "2025-09-01");

    private static Task<JsonElement> LoadAsync(HandlerTestHost host, string projectId) =>
        host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));

    private static async Task<ProjectDocument> ReadSettingsFileAsync(HandlerTestHost host, string projectId) =>
        (await new JsonFileProjectStore(new JetProjectFolder(host.ProjectsRoot)).FindAsync(projectId, CancellationToken.None))!;

    // 照使用者的做法：取消唯讀，用文字編輯器改設定檔裡的值，每個值都仍然合法。
    private static async Task EditSettingsFileAsync(HandlerTestHost host, string projectId, Action<JsonObject> edit)
    {
        var path = new JetProjectFolder(host.ProjectsRoot).GetProjectJsonPath(projectId);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        edit(node);
        await ProjectJsonEdits.WriteAllTextAsync(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Task EditEverySettingAsync(HandlerTestHost host, string projectId, ProjectDocument original) =>
        EditSettingsFileAsync(host, projectId, node =>
        {
            node["periodStart"] = "2024-01-01";
            node["periodEnd"] = "2024-12-31";
            node["lastAccountingPeriodDate"] = "2024-09-01";
            node["nonWorkingDays"] = new JsonArray(1, 2);
            node["operatorId"] = "合成的另一位建案者";
            node["sampleSeed"] = original.SampleSeed == 1 ? 2 : original.SampleSeed - 1;
            node["moneyScale"] = 100;
            node["rocDateEnabled"] = false;
        });

    private static void AssertProjectMatchesOriginal(JsonElement project, ProjectDocument original)
    {
        Assert.Equal(original.PeriodStart, project.GetProperty("periodStart").GetString());
        Assert.Equal(original.PeriodEnd, project.GetProperty("periodEnd").GetString());
        Assert.Equal(original.LastAccountingPeriodDate, project.GetProperty("prepStartDate").GetString());
        Assert.Equal(original.OperatorId, project.GetProperty("operatorId").GetString());
        Assert.Equal(original.MoneyScale, project.GetProperty("moneyScale").GetInt32());
        Assert.Equal(original.RocDateEnabled, project.GetProperty("rocDateEnabled").GetBoolean());
    }

    private static void AssertProtectedFieldsEqual(ProjectDocument expected, ProjectDocument actual)
    {
        Assert.Equal(expected.PeriodStart, actual.PeriodStart);
        Assert.Equal(expected.PeriodEnd, actual.PeriodEnd);
        Assert.Equal(expected.LastAccountingPeriodDate, actual.LastAccountingPeriodDate);
        Assert.Equal(expected.NonWorkingDays, actual.NonWorkingDays);
        Assert.Equal(expected.OperatorId, actual.OperatorId);
        Assert.Equal(expected.SampleSeed, actual.SampleSeed);
        Assert.Equal(expected.SampleSeedVersion, actual.SampleSeedVersion);
        Assert.Equal(expected.MoneyScale, actual.MoneyScale);
        Assert.Equal(expected.RocDateEnabled, actual.RocDateEnabled);
    }

    private static string[] Keys(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString()!).ToArray();
}
