using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class JourneyFixedAnswerTests
{
    /// <summary>
    /// fixed seed INF sample member sequence：同一 demo 案件建兩份，各自 validate.run 落地 INF 樣本後，
    /// 以小 pageSize 走訪 query.infSamplePage 到底，斷言完整 wire row 序列完全相同。>pageSize 擋住只走首頁就看似通過的情況。
    /// 比對兩個各自建立的 SQLite 案件，確認固定種子在不同案件抽出同一份樣本。
    /// </summary>
    [Fact]
    public async Task InfSampleMemberSequence_FixedSeed_IsTheSameInTwoSeparateCases()
    {
        const int pageSize = 25;
        const long pinnedSeed = 48271;

        using var firstHost = new HandlerTestHost();
        var firstSequence = await RunFixedSeedInfSampleSequenceAsync(
            firstHost, "sqlite", pinnedSeed, pageSize);

        using var secondHost = new HandlerTestHost();
        var secondSequence = await RunFixedSeedInfSampleSequenceAsync(
            secondHost, "sqlite", pinnedSeed, pageSize);

        Assert.True(firstSequence.Count > pageSize,
            $"INF 樣本須跨頁(>{pageSize} 筆)才能走到後續頁面分支,實得 {firstSequence.Count} 筆。");
        Assert.Equal(firstSequence, secondSequence);
    }

    private static async Task<List<string>> RunFixedSeedInfSampleSequenceAsync(
        HandlerTestHost host,
        string provider,
        long seed,
        int pageSize)
    {
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        PinSampleSeed(host.ProjectsRoot, context.ProjectId, seed);
        await host.DispatchAsync("validate.run");
        return await WalkInfSamplePagesAsync(host, pageSize);
    }

    /// <summary>
    /// 把案件 project.json 的 sampleSeed 與抽樣演算法版本一起固定，
    /// 讓兩個案件用同一版演算法、抽出同一組樣本。
    /// </summary>
    private static void PinSampleSeed(string projectsRoot, string projectId, long seed)
    {
        var path = Path.Combine(projectsRoot, projectId, "project.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["sampleSeed"] = seed;
        node["sampleSeedVersion"] = JetAuditProgram.CurrentInfSamplingAlgorithmVersion;
        File.WriteAllText(path, node.ToJsonString());
    }

    /// <summary>
    /// 逐頁帶 nextCursor 走訪 query.infSamplePage 到底，回傳完整 wire row 序列；
    /// 不另外計算預期樣本，只比對兩個案件在同一種子下抽出的成員與順序。
    /// </summary>
    private static async Task<List<string>> WalkInfSamplePagesAsync(HandlerTestHost host, int pageSize)
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.infSamplePage",
                JsonSerializer.Serialize(new { cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                all.Add($"{r.GetProperty("voucherNumber").GetString()}|" +
                        $"{r.GetProperty("accountNumber").GetString()}|" +
                        $"{r.GetProperty("accountName").GetString()}|" +
                        $"{r.GetProperty("debit").GetRawText()}|" +
                        $"{r.GetProperty("credit").GetRawText()}|" +
                        $"{r.GetProperty("glDate").GetString()}|" +
                        $"{r.GetProperty("approvalDate").GetString()}|" +
                        $"{r.GetProperty("createdBy").GetString()}|" +
                        $"{r.GetProperty("approvedBy").GetString()}|" +
                        $"{r.GetProperty("description").GetString()}");
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    /// <summary>
    /// 自動編號：lineItem 未對應的 GL 批次（多傳票、每傳票多列、交錯送入）投影後，
    /// (voucher_number, source_row_number, line_item) 序列符合逐傳票依 source_row_number 編號的手算答案。
    /// </summary>
    [Fact]
    public async Task GlAutoNumbering_LineItemUnmapped_RestartsAtOneForEachVoucher()
    {
        using var root = new TempProjectRoot();
        var sqliteDb = new SqliteProjectDatabase(new JetProjectFolder(root.Path));
        var sqliteSequence = await ProjectUnmappedLineItemAsync(sqliteDb);

        Assert.Equal(ExpectedAutoNumberingSequence(), sqliteSequence);
    }

    private static async Task<List<(string?, long, string?)>> ProjectUnmappedLineItemAsync(
        ILocalProjectDatabase database)
    {
        const string batchId = "b1";
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await using (var seed = database.CreateConnection(projectId))
        {
            await seed.OpenAsync();
            await SeedGlStagingAsync(seed, batchId, AutoNumberingSeedRows());
        }

        await new LocalGlRepository(database).ProjectStagingToTargetAsync(
            projectId, batchId, DualSpec(), 10_000, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        await using var read = database.CreateConnection(projectId);
        await read.OpenAsync();
        return await ReadLineItemSequenceAsync(read);
    }

    /// <summary>交錯送入：D1 在第 1、3、4 位，D2 在第 2、5 位。</summary>
    private static (long RowNumber, string Doc)[] AutoNumberingSeedRows() =>
        [(1, "D1"), (2, "D2"), (3, "D1"), (4, "D1"), (5, "D2")];

    /// <summary>手算答案：每張傳票依 source_row_number 從 1 重新編號。</summary>
    private static (string?, long, string?)[] ExpectedAutoNumberingSequence() =>
    [
        ("D1", 1, "1"), ("D1", 3, "2"), ("D1", 4, "3"),
        ("D2", 2, "1"), ("D2", 5, "2")
    ];

    /// <summary>不配對 lineItem 的欄位配對，金額借貸分欄；種子資料只有借方有金額。</summary>
    private static GlMappingSpec DualSpec() => new(
        new Dictionary<string, string>
        {
            [GlMappingKeys.VoucherNumber] = "doc",
            [GlMappingKeys.GlDate] = "date",
            [GlMappingKeys.AccountNumber] = "acc",
            [GlMappingKeys.AccountName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.DebitAmount] = "debit",
            [GlMappingKeys.CreditAmount] = "credit"
        },
        GlAmountMode.DualAmount);

    /// <summary>把合成的 GL 原始列寫進 pbc_gl。</summary>
    private static async Task SeedGlStagingAsync(
        DbConnection connection, string batchId, IReadOnlyList<(long RowNumber, string Doc)> rows)
    {
        foreach (var (rowNumber, doc) in rows)
        {
            var values = new Dictionary<string, string>
            {
                ["doc"] = doc, ["date"] = "2024-01-01", ["acc"] = "1101", ["name"] = "現金", ["desc"] = "t",
                ["debit"] = "100"
            };

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO pbc_gl (batch_id, row_number, source_no, source_row_number, row_json)
                VALUES (@batchId, @rowNumber, 1, @sourceRowNumber, @rowJson);
                """;
            AddParam(command, "@batchId", batchId);
            AddParam(command, "@rowNumber", rowNumber);
            AddParam(command, "@sourceRowNumber", (int)rowNumber);
            AddParam(command, "@rowJson", JsonSerializer.Serialize(values, JetJsonStorage.Options));
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>依 voucher_number 與 source_row_number 排序，讀出 (voucher_number, source_row_number, line_item)。</summary>
    private static async Task<List<(string?, long, string?)>> ReadLineItemSequenceAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT voucher_number, source_row_number, line_item
            FROM gl
            ORDER BY voucher_number, source_row_number;
            """;

        var sequence = new List<(string?, long, string?)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sequence.Add((
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return sequence;
    }

    private static void AddParam(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}
