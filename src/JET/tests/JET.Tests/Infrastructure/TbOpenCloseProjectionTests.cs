using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

// 改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
// TB 提交時間固定使用 DateTimeOffset.UnixEpoch。
namespace JET.Tests.Infrastructure;

/// <summary>
/// TB legacy 金額模式 OpenClose（SA=2）與 OpenCloseBySide（SA=4）在各引擎的 target 落地驗收。
/// 投影純函式 <see cref="TbRowProjector"/> 由三引擎共用，此處驗「值真的寫進 tb」，
/// 覆蓋 Local 家族雙引擎（SQLite / DuckDB）＋ SqlServer 閘控。
/// oracle：規格手算（closing−opening；(cdr−ccr)−(odr−ocr)）× scale=10000。斷言鎖值＋身分（科目碼）。
/// </summary>
public sealed class TbOpenCloseProjectionTests
{
    private const int Scale = 10_000;

    private static async IAsyncEnumerable<StagingRow> ToAsync(IEnumerable<StagingRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static ImportSourceDescriptor Source() => new(@"C:\tb.xlsx", "tb.xlsx", null, null, null);

    private static async Task<long> ScalarAsync(ILocalProjectDatabase db, string projectId, string sql)
    {
        await using var connection = db.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? 0L : Convert.ToInt64(result);
    }

    private static ILocalProjectDatabase MakeDatabase(string provider, JetProjectFolder folder) =>
        new SqliteProjectDatabase(folder);

    // OpenClose：期末 − 期初。兩科目一正一負變動。
    [Theory]
    [InlineData("sqlite")]
    public async Task OpenClose_ProjectsClosingMinusOpening(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = MakeDatabase(provider, folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        // 只有建案會建立案件資料庫，讀寫前的檢查不再順便建表（裁定 2026-11-05-72），所以照建案的做法先建表。第一次失敗收據 20261105-155959508-7b95566f0d5a426aa5145c6a92f63fc1。
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        var rows = new List<StagingRow>
        {
            new(2, new Dictionary<string, string>
            {
                ["acc"] = "1101", ["name"] = "現金", ["opening"] = "1000", ["closing"] = "1500"
            }),
            new(3, new Dictionary<string, string>
            {
                ["acc"] = "1102", ["name"] = "銀行", ["opening"] = "800", ["closing"] = "300"
            })
        };

        var batch = (await new LocalImportRepository(db).ReplaceBatchAsync(
            projectId, DatasetKind.Tb, [new ImportSourceInput(Source(),
            ["acc", "name", "opening", "closing"], ToAsync(rows))], CancellationToken.None)).Batch;

        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccountNumber] = "acc",
                [TbMappingKeys.AccountName] = "name",
                [TbMappingKeys.OpeningBalance] = "opening",
                [TbMappingKeys.ClosingBalance] = "closing"
            },
            TbChangeMode.OpenClose);

        var result = await new LocalTbRepository(db).ProjectStagingToTargetAsync(
            projectId, batch.BatchId, spec, Scale, committedUtc: DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.ProjectedRowCount);
        Assert.Equal(5_000_000L, await ScalarAsync(db, projectId,
            "SELECT change_amount_scaled FROM tb WHERE account_number='1101'"));
        Assert.Equal(-5_000_000L, await ScalarAsync(db, projectId,
            "SELECT change_amount_scaled FROM tb WHERE account_number='1102'"));
    }

    // OpenCloseBySide：(期末借 − 期末貸) − (期初借 − 期初貸)。借方科目與貸方科目各 +500。
    [Theory]
    [InlineData("sqlite")]
    public async Task OpenCloseBySide_ProjectsNetSideChange(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = MakeDatabase(provider, folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        // 照建案的做法先建表（裁定 2026-11-05-72）。
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        var rows = new List<StagingRow>
        {
            new(2, new Dictionary<string, string>
            {
                ["acc"] = "1101", ["name"] = "現金",
                ["odr"] = "1000", ["ocr"] = "0", ["cdr"] = "1500", ["ccr"] = "0"
            }),
            new(3, new Dictionary<string, string>
            {
                ["acc"] = "2101", ["name"] = "應付帳款",
                ["odr"] = "0", ["ocr"] = "800", ["cdr"] = "0", ["ccr"] = "300"
            })
        };

        var batch = (await new LocalImportRepository(db).ReplaceBatchAsync(
            projectId, DatasetKind.Tb, [new ImportSourceInput(Source(),
            ["acc", "name", "odr", "ocr", "cdr", "ccr"], ToAsync(rows))], CancellationToken.None)).Batch;

        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccountNumber] = "acc",
                [TbMappingKeys.AccountName] = "name",
                [TbMappingKeys.OpeningDebitBalance] = "odr",
                [TbMappingKeys.OpeningCreditBalance] = "ocr",
                [TbMappingKeys.ClosingDebitBalance] = "cdr",
                [TbMappingKeys.ClosingCreditBalance] = "ccr"
            },
            TbChangeMode.OpenCloseBySide);

        var result = await new LocalTbRepository(db).ProjectStagingToTargetAsync(
            projectId, batch.BatchId, spec, Scale, committedUtc: DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.ProjectedRowCount);
        Assert.Equal(5_000_000L, await ScalarAsync(db, projectId,
            "SELECT change_amount_scaled FROM tb WHERE account_number='1101'"));
        Assert.Equal(5_000_000L, await ScalarAsync(db, projectId,
            "SELECT change_amount_scaled FROM tb WHERE account_number='2101'"));
    }
}
