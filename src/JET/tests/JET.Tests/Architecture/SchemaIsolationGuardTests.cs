using System.Text.RegularExpressions;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 單庫資料隔離的紅線（原始碼靜態守衛，不需資料庫、毫秒級）。
///
/// SQL Server 為「單一資料庫、每專案一個 schema」：隔離**只**靠 schema 牆。任一 `SqlServer*` repo 若在
/// 自己的 SQL 字面值裡裸引用專案表（`FROM gl` 而非 `FROM {s}.gl`），該句就會跨越
/// schema、讓 A 專案讀到 B 專案的資料。本守衛掃描 `Infrastructure/Persistence/SqlServer/` 全部原始碼，
/// 斷言每個專案表引用都經 schema 限定；違規即轉紅並指出 檔:行:表名。
///
/// 表名的唯一事實來源是 <see cref="JetSchemaCatalog"/>（新增表只改登錄表一處，本守衛自動涵蓋——DRY）。
///
/// 範圍取捨（顯式）：只掃 `SqlServer/`。共用述詞層（`GlRulePredicates` 等）以單一 `{schemaPrefix}` 樣板供
/// 兩 provider 共用（SQLite 傳 ""），由構造保證安全；`ValidationProcedures`/`InfSamplePageSql` 等另持「SQLite 專用的
/// 裸樣板」，素樸掃描會誤判，故不納入本靜態守衛。共用層被 SQL Server 路徑「誤用裸樣板」這種語意錯誤，
/// 由雙專案行為守衛（`SchemaIsolationJourneyTests`，真實引擎）兜底——兩道守衛分工見設計 spec §2。
/// </summary>
public sealed class SchemaIsolationGuardTests
{
    /// <summary>專案表名的唯一事實來源（中央三層登錄表）。</summary>
    private static readonly IReadOnlyList<string> ProjectTables =
        JetSchemaCatalog.All.Select(entry => entry.PhysicalName).ToArray();

    /// <summary>
    /// 允許的 schema 限定前綴（去空白、還原 <c>{{</c> 跳脫後的結尾）：
    /// <c>{s}.</c> 命令工廠哨兵、<c>{schemaPrefix}</c>/<c>{prefix}</c> 共用述詞層 C# 內插、
    /// <c>].</c> 已成方括號識別字（如 <c>[{schema}].[table]</c>），以及明確指向控制平面的 <c>dbo.</c>。
    /// dbo 裡只有控制平面的表（例如操作紀錄 <c>dbo.operation_log</c>），不放專案資料；寫明 dbo 不會落到別的專案。
    /// </summary>
    private static readonly string[] AllowedQualifiers = ["{s}.", "{schemaPrefix}", "{prefix}", "].", "dbo."];

    /// <summary>限定詞：<c>{s}.</c>、<c>{{s}}.</c>、<c>{schemaPrefix}</c>、<c>[schema].</c> 或一般的 <c>schema.</c>，可以連用。</summary>
    private const string QualifierPattern =
        @"(?:\{\{?[A-Za-z_]+\}\}?\.?|\[[^\]\r\n]*\]\.|[A-Za-z_][A-Za-z0-9_]*\.)*";

    /// <summary>
    /// FROM 清單裡逗號前面的一項：可帶限定詞的表名，加上可有可無的別名。別名不能是接在表後面的子句關鍵字，
    /// 否則 <c>FROM {s}.gl g WHERE x IN (a, tb)</c> 這類寫法會把 WHERE 當成別名，再把括號裡的 tb 誤判成表。
    /// </summary>
    private const string FromListItemPattern =
        QualifierPattern + @"\[?[A-Za-z_][A-Za-z0-9_]*\]?" +
        @"(?:\s+(?:AS\s+)?(?!(?:WHERE|JOIN|INNER|LEFT|RIGHT|FULL|CROSS|OUTER|ON|GROUP|ORDER|HAVING|UNION|EXCEPT|INTERSECT|WITH|SET|OUTPUT|VALUES|SELECT)\b)\[?[A-Za-z_][A-Za-z0-9_]*\]?)?";

    /// <summary>
    /// SQL 裡接在這些字後面的識別字才是資料表：FROM、JOIN、INTO、INSERT（SQL Server 允許省略 INTO）、UPDATE、
    /// TABLE（CREATE、ALTER、TRUNCATE、DROP）、MERGE、USING、REFERENCES、DELETE、ON（CREATE INDEX ... ON）、
    /// 逗號分隔的 FROM 清單裡逗號後面的那一項，以及 OBJECT_ID 與 COL_LENGTH 的第一個引數。
    /// 表名改短以後（gl、tb），同一個字也是資料集種類的值（<c>'gl'</c>）和日誌名稱（<c>"gl-projection"</c>）；
    /// 只看表引用的位置，這些值不會被當成裸引用，真正接在 FROM 這類字後面的表名一樣會被抓到。
    /// 逗號只在緊接 FROM 清單的前一項時才算，SELECT 的欄位清單、函式引數與 GROUP BY 裡的逗號不算。
    /// </summary>
    private static readonly Regex TableReference = new(
        @"(?<=(?:\b(?:FROM|JOIN|INTO|INSERT|UPDATE|TABLE|MERGE|USING|REFERENCES|DELETE|ON)\s+" +
        @"|\bFROM\s+(?:" + FromListItemPattern + @"\s*,\s*)+" +
        @"|\b(?:OBJECT_ID|COL_LENGTH)\s*\(\s*N?'))" +
        @"(?<qualifier>" + QualifierPattern + @")" +
        @"\[?(?<name>[A-Za-z_][A-Za-z0-9_]*)(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 正向對照（證明掃描真的有牙）：裸引用必被抓、各種合法限定必被放行、註解內提及不算。
    /// 這條若失敗，代表掃描邏輯壞掉，上面的「無違規」便可能是空掃真空通過。
    /// </summary>
    [Fact]
    public void Scanner_FlagsBareReference_AndAcceptsQualifiedAndComments()
    {
        // 裸引用 → 抓到
        Assert.NotEmpty(FindBareTableReferences("x = \"SELECT * FROM gl g\";", ProjectTables));
        // 裸引用在字串結尾（後面接右引號）→ 仍抓到
        Assert.NotEmpty(FindBareTableReferences("x = \"SELECT * FROM gl\";", ProjectTables));
        // {s}. 命令工廠限定 → 放行
        Assert.Empty(FindBareTableReferences("x = \"SELECT * FROM {s}.gl g\";", ProjectTables));
        // $"..." 內插字串把 {s} 跳脫為 {{s}}（執行時 = {s}.）→ 放行
        Assert.Empty(FindBareTableReferences("x = $\"FROM {{s}}.gl g WHERE {where}\";", ProjectTables));
        // {schemaPrefix} 述詞層限定 → 放行
        Assert.Empty(FindBareTableReferences("x = $\"FROM {schemaPrefix}gl g\";", ProjectTables));
        // 方括號 schema 限定 → 放行
        Assert.Empty(FindBareTableReferences("x = \"SELECT * FROM [{schema}].[gl]\";", ProjectTables));
        // 表名以完整雙引號字串單獨出現（當識別字傳給消費端 {s}. 串接）→ 放行
        Assert.Empty(FindBareTableReferences("Preview(conn, id, \"gl\", \"pbc_gl\", n);", ProjectTables));
        // 控制平面的 dbo 表 → 放行
        Assert.Empty(FindBareTableReferences("x = \"INSERT INTO dbo.operation_log (action) VALUES (@a)\";", ProjectTables));
        Assert.Empty(FindBareTableReferences("x = \"IF OBJECT_ID(N'dbo.operation_log','U') IS NULL\";", ProjectTables));
        // 和表名同字的資料集種類值與日誌名稱，不在表引用的位置 → 放行
        Assert.Empty(FindBareTableReferences("x = \"DELETE FROM {s}.gl_rde_value WHERE @kind = 'gl';\";", ProjectTables));
        Assert.Empty(FindBareTableReferences("x = \"CHECK (dataset_kind IN ('gl','tb','account_mapping'))\";", ProjectTables));
        Assert.Empty(FindBareTableReferences("Milestone(_log, \"gl-projection\", n);", ProjectTables));
        // 別名後面的欄位限定不是表引用 → 放行
        Assert.Empty(FindBareTableReferences("x = \"FROM {s}.gl gl JOIN {s}.tb tb ON gl.account_number = tb.account_number\";", ProjectTables));
        // 其他位置的裸表名 → 抓到
        Assert.NotEmpty(FindBareTableReferences("x = \"DELETE FROM tb;\";", ProjectTables));
        Assert.NotEmpty(FindBareTableReferences("x = \"UPDATE gl SET x = 1\";", ProjectTables));
        Assert.NotEmpty(FindBareTableReferences("x = \"INSERT INTO operation_log (action) VALUES (@a)\";", ProjectTables));
        Assert.NotEmpty(FindBareTableReferences("x = \"CREATE TABLE account_mapping (id INT)\";", ProjectTables));
        Assert.NotEmpty(FindBareTableReferences("x = \"CREATE INDEX ix ON gl (entry_id)\";", ProjectTables));
        Assert.NotEmpty(FindBareTableReferences("x = \"IF OBJECT_ID(N'gl','U') IS NULL\";", ProjectTables));
        Assert.NotEmpty(FindBareTableReferences("x = \"SELECT 1 FROM {s}.gl g\nJOIN\n    tb t ON t.x = g.x\";", ProjectTables));
        // 只看表引用位置的寫法原本抓不到下面兩種裸引用；補這些案例、還沒改掃描時的失敗收據
        // 20261030-134845391-faf127181bed43a394fc8a92d8b955ab。
        // 逗號分隔的 FROM 清單：第一個表有限定，後面的表沒有 → 抓到
        Assert.NotEmpty(FindBareTableReferences("x = \"SELECT 1 FROM {s}.gl g, tb t WHERE t.x = g.x\";", ProjectTables));
        Assert.NotEmpty(FindBareTableReferences("x = \"SELECT 1 FROM {s}.gl AS g, {s}.tb AS t,\n    account_mapping m\";", ProjectTables));
        Assert.NotEmpty(FindBareTableReferences("x = \"SELECT 1 FROM {s}.gl, tb\";", ProjectTables));
        // 省略 INTO 的 INSERT（SQL Server 允許）→ 抓到
        Assert.NotEmpty(FindBareTableReferences("x = \"INSERT tb (account_number) VALUES (@a)\";", ProjectTables));
        Assert.NotEmpty(FindBareTableReferences("x = \"INSERT\n    gl (entry_id) SELECT 1\";", ProjectTables));
        // 清單裡每個表都有限定 → 放行
        Assert.Empty(FindBareTableReferences("x = \"SELECT 1 FROM {s}.gl g, {s}.tb t WHERE t.x = g.x\";", ProjectTables));
        Assert.Empty(FindBareTableReferences("x = \"INSERT {s}.tb (account_number) VALUES (@a)\";", ProjectTables));
        // SELECT 的欄位清單和函式引數也用逗號，裡面和表名同字的欄位或別名不是表引用 → 放行
        Assert.Empty(FindBareTableReferences("x = \"SELECT a, b FROM {s}.gl g\";", ProjectTables));
        Assert.Empty(FindBareTableReferences("x = \"SELECT g.entry_id, tb, gl FROM {s}.gl g\";", ProjectTables));
        Assert.Empty(FindBareTableReferences("x = \"SELECT COALESCE(a, tb) AS gl FROM {s}.gl g WHERE g.x IN (1, 2) ORDER BY a, tb\";", ProjectTables));
        Assert.Empty(FindBareTableReferences("x = \"SELECT 1 FROM {s}.gl g GROUP BY g.account_number, tb\";", ProjectTables));
        // 不是允許的限定詞 → 抓到
        Assert.NotEmpty(FindBareTableReferences("x = $\"FROM {other}gl\";", ProjectTables));
        // 註解內提及（// 與 ///）→ 不算
        Assert.Empty(FindBareTableReferences("// 這段查 gl", ProjectTables));
        Assert.Empty(FindBareTableReferences("/// <summary>對 gl 做…</summary>", ProjectTables));
        // 前綴為更長表名的一部分 → 不誤判（import_batch 不匹配 import_batch_source）
        Assert.Empty(FindBareTableReferences("x = \"FROM {s}.import_batch_source\";", ProjectTables));
    }

    /// <summary>
    /// 掃描一份原始碼文字，回傳其中「未經允許限定」的專案表引用（行號、表名）。
    /// 先把整行 `//` / `///` 註解抹白（保留換行以維持行號），再找出 SQL 表引用位置上的識別字（見 <see cref="TableReference"/>）；
    /// 識別字是專案表、前面沒有限定詞或限定詞不在允許清單時算違規（可跨換行，穩健於多行 SQL）。
    /// 2026-10-30 表名改短後，原本逐字比對表名的寫法把資料集種類值 'gl'、日誌名稱 "gl-projection"、
    /// 控制平面的 dbo.operation_log 都當成裸引用，一共 16 處誤判，沒有一處真的少了 schema 限定
    /// （第一次失敗的收據 20261030-094437561-77be117df798489184dba51d53adcb67），所以改成只看表引用的位置。
    /// </summary>
    private static IReadOnlyList<(int Line, string Table)> FindBareTableReferences(
        string text, IReadOnlyCollection<string> tables)
    {
        var code = BlankCommentLines(text);
        var results = new List<(int, string)>();
        var projectTables = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);

        foreach (Match match in TableReference.Matches(code))
        {
            var name = match.Groups["name"].Value;
            if (!projectTables.Contains(name))
            {
                continue;
            }

            // 還原 $"..." 內插字串的跳脫雙括號（{{s}} → {s}）後比對限定前綴。
            var qualifier = match.Groups["qualifier"].Value
                .Replace("{{", "{")
                .Replace("}}", "}");
            var qualified = qualifier.Length > 0
                && AllowedQualifiers.Any(q => qualifier.EndsWith(q, StringComparison.OrdinalIgnoreCase));
            if (!qualified)
            {
                var lineNumber = 1 + code[..match.Index].Count(c => c == '\n');
                results.Add((lineNumber, name));
            }
        }

        return results;
    }

    /// <summary>整行 `//` / `///` 註解抹白（保留換行維持行號）；表名常出現在說明文字，不算違規。</summary>
    private static string BlankCommentLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                lines[i] = string.Empty;
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>由測試組件位置向上尋 `JET.slnx`，定位 repo 根（不綁死機器路徑，可重複）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JET.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }
}
