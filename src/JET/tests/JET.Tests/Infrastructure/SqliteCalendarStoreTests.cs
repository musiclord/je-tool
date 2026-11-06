using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SqliteCalendarStoreTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly TempProjectRoot _root = new();
        private readonly JetProjectFolder _folder;

        public Fixture()
        {
            _folder = new JetProjectFolder(_root.Path);
            ProjectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(_folder.GetProjectDirectory(ProjectId));
            var database = new SqliteProjectDatabase(_folder);
            // 只有建案會建立案件資料庫，讀寫前的檢查不再順便建表（裁定 2026-11-05-72），所以照建案的做法先建表。第一次失敗收據 20261105-155959508-7b95566f0d5a426aa5145c6a92f63fc1。
            database.EnsureCreatedAsync(ProjectId, CancellationToken.None).GetAwaiter().GetResult();
            Store = new LocalCalendarStore(database);
        }

        public string ProjectId { get; }

        public LocalCalendarStore Store { get; }

        public async Task<string?> QueryNameAsync(string date)
        {
            var database = new SqliteProjectDatabase(_folder);
            await using var connection = database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT day_name FROM calendar_day WHERE date = @d;";
            command.AddWithValue("@d", date);
            var result = await command.ExecuteScalarAsync();
            return result is DBNull or null ? null : (string)result;
        }

        public void Dispose() => _root.Dispose();
    }

    [Fact]
    public async Task ReplaceDays_StoresDatesAndCounts()
    {
        using var fixture = new Fixture();

        await fixture.Store.ReplaceDaysAsync(
            fixture.ProjectId,
            CalendarDayType.Holiday,
            [new CalendarDayEntry("2025-01-01", null), new CalendarDayEntry("2025-02-28", null), new CalendarDayEntry("2025-10-10", null)],
            CancellationToken.None);

        var count = await fixture.Store.CountAsync(
            fixture.ProjectId, CalendarDayType.Holiday, CancellationToken.None);

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task ReplaceDays_ReplacesPriorSet_NotAccumulates()
    {
        using var fixture = new Fixture();

        await fixture.Store.ReplaceDaysAsync(
            fixture.ProjectId, CalendarDayType.Holiday,
            [new CalendarDayEntry("2025-01-01", null), new CalendarDayEntry("2025-02-28", null), new CalendarDayEntry("2025-10-10", null)],
            CancellationToken.None);

        await fixture.Store.ReplaceDaysAsync(
            fixture.ProjectId, CalendarDayType.Holiday,
            [new CalendarDayEntry("2025-05-01", null), new CalendarDayEntry("2025-06-01", null)],
            CancellationToken.None);

        var count = await fixture.Store.CountAsync(
            fixture.ProjectId, CalendarDayType.Holiday, CancellationToken.None);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task HolidayAndMakeupSets_AreIndependent()
    {
        using var fixture = new Fixture();

        await fixture.Store.ReplaceDaysAsync(
            fixture.ProjectId, CalendarDayType.Holiday,
            [new CalendarDayEntry("2025-01-01", null), new CalendarDayEntry("2025-02-28", null)], CancellationToken.None);
        await fixture.Store.ReplaceDaysAsync(
            fixture.ProjectId, CalendarDayType.Makeup,
            [new CalendarDayEntry("2025-02-08", null)], CancellationToken.None);

        await fixture.Store.ReplaceDaysAsync(
            fixture.ProjectId, CalendarDayType.Makeup, [], CancellationToken.None);

        Assert.Equal(2, await fixture.Store.CountAsync(
            fixture.ProjectId, CalendarDayType.Holiday, CancellationToken.None));
        Assert.Equal(0, await fixture.Store.CountAsync(
            fixture.ProjectId, CalendarDayType.Makeup, CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceDays_PersistsDayName()
    {
        using var fixture = new Fixture();

        await fixture.Store.ReplaceDaysAsync(
            fixture.ProjectId, CalendarDayType.Holiday,
            [new CalendarDayEntry("2025-01-01", "元旦"), new CalendarDayEntry("2025-02-28", "228 紀念日")],
            CancellationToken.None);

        Assert.Equal("元旦", await fixture.QueryNameAsync("2025-01-01"));
        Assert.Equal("228 紀念日", await fixture.QueryNameAsync("2025-02-28"));
    }
}
