using System.Text.RegularExpressions;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 單庫資料隔離的紅線（原始碼靜態守衛，不需資料庫、毫秒級）。
///
/// SQL Server 為「單一資料庫、每專案一個 schema」：隔離**只**靠 schema 牆。任一 `SqlServer*` repo 若在
/// 自己的 SQL 字面值裡裸引用專案表（`FROM target_gl_entry` 而非 `FROM {s}.target_gl_entry`），該句就會跨越
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
    /// 允許的 schema 限定前綴（去空白後的結尾）：
    /// <c>{s}.</c> 命令工廠哨兵、<c>{schemaPrefix}</c>/<c>{prefix}</c> 共用述詞層 C# 內插、
    /// <c>].</c> 與 <c>].[</c> 已成方括號識別字（如 <c>[{schema}].[table]</c>）。
    /// </summary>
    private static readonly string[] AllowedQualifiers = ["{s}.", "{schemaPrefix}", "{prefix}", "].", "].["];

    [Fact]
    public void SqlServerRepositories_NeverReferenceBareProjectTables()
    {
        var sqlServerDir = Path.Combine(
            RepoRoot(), "JET", "Infrastructure", "Persistence", "SqlServer");
        Assert.True(Directory.Exists(sqlServerDir), $"找不到 SqlServer repo 目錄：{sqlServerDir}");

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sqlServerDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var (line, table) in FindBareTableReferences(text, ProjectTables))
            {
                violations.Add(
                    $"{Path.GetFileName(file)}:{line} 裸引用專案表 '{table}'" +
                    "（單庫模型下缺 schema 限定＝跨專案資料外洩）");
            }
        }

        Assert.True(
            violations.Count == 0,
            "SQL Server 路徑出現未經 schema 限定的專案表引用：\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// 正向對照（證明掃描真的有牙）：裸引用必被抓、各種合法限定必被放行、註解內提及不算。
    /// 這條若失敗，代表掃描邏輯壞掉，上面的「無違規」便可能是空掃真空通過。
    /// </summary>
    [Fact]
    public void Scanner_FlagsBareReference_AndAcceptsQualifiedAndComments()
    {
        // 裸引用 → 抓到
        Assert.NotEmpty(FindBareTableReferences("x = \"SELECT * FROM target_gl_entry g\";", ProjectTables));
        // 裸引用在字串結尾（後面接右引號）→ 仍抓到
        Assert.NotEmpty(FindBareTableReferences("x = \"SELECT * FROM target_gl_entry\";", ProjectTables));
        // {s}. 命令工廠限定 → 放行
        Assert.Empty(FindBareTableReferences("x = \"SELECT * FROM {s}.target_gl_entry g\";", ProjectTables));
        // $"..." 內插字串把 {s} 跳脫為 {{s}}（執行時 = {s}.）→ 放行
        Assert.Empty(FindBareTableReferences("x = $\"FROM {{s}}.target_gl_entry g WHERE {where}\";", ProjectTables));
        // {schemaPrefix} 述詞層限定 → 放行
        Assert.Empty(FindBareTableReferences("x = $\"FROM {schemaPrefix}target_gl_entry g\";", ProjectTables));
        // 方括號 schema 限定 → 放行
        Assert.Empty(FindBareTableReferences("x = \"[{schema}].[target_gl_entry]\";", ProjectTables));
        // 表名以完整雙引號字串單獨出現（當識別字傳給消費端 {s}. 串接）→ 放行
        Assert.Empty(FindBareTableReferences("Preview(conn, id, \"gl\", \"staging_gl_raw_row\", n);", ProjectTables));
        // 註解內提及（// 與 ///）→ 不算
        Assert.Empty(FindBareTableReferences("// 這段查 target_gl_entry", ProjectTables));
        Assert.Empty(FindBareTableReferences("/// <summary>對 target_gl_entry 做…</summary>", ProjectTables));
        // 前綴為更長表名的一部分 → 不誤判（import_batch 不匹配 import_batch_source）
        Assert.Empty(FindBareTableReferences("x = \"FROM {s}.import_batch_source\";", ProjectTables));
    }

    /// <summary>
    /// 掃描一份原始碼文字，回傳其中「未經允許限定」的專案表引用（行號、表名）。
    /// 先把整行 `//` / `///` 註解抹白（保留換行以維持行號），再對每個表名做字界比對；
    /// 對每次命中往前取 40 字、去空白後檢查是否以任一允許前綴結尾（可跨換行，穩健於多行 SQL）。
    /// </summary>
    private static IReadOnlyList<(int Line, string Table)> FindBareTableReferences(
        string text, IReadOnlyCollection<string> tables)
    {
        var code = BlankCommentLines(text);
        var results = new List<(int, string)>();

        foreach (var table in tables)
        {
            foreach (Match match in Regex.Matches(code, $@"(?<![A-Za-z0-9_]){Regex.Escape(table)}(?![A-Za-z0-9_])"))
            {
                // 表名以完整雙引號字串字面值單獨出現（如 "staging_gl_raw_row"）＝當識別字資料傳遞，
                // 其 schema 限定發生在消費端（{s}. + 串接），非此處的嵌入式 SQL；靜態不追變數流，
                // 該消費端由雙專案行為守衛（SchemaIsolationJourneyTests）兜底。
                var beforeChar = match.Index > 0 ? code[match.Index - 1] : '\0';
                var afterChar = match.Index + table.Length < code.Length ? code[match.Index + table.Length] : '\0';
                if (beforeChar == '"' && afterChar == '"')
                {
                    continue;
                }

                var windowStart = Math.Max(0, match.Index - 40);
                var before = code.Substring(windowStart, match.Index - windowStart);
                // 去空白（穩健於多行 SQL），再還原 $"..." 內插字串的跳脫雙括號（{{s}} → {s}）後比對限定前綴。
                var compact = Regex.Replace(before, @"\s+", string.Empty)
                    .Replace("{{", "{")
                    .Replace("}}", "}");

                var qualified = AllowedQualifiers.Any(q => compact.EndsWith(q, StringComparison.Ordinal));
                if (!qualified)
                {
                    var lineNumber = 1 + code[..match.Index].Count(c => c == '\n');
                    results.Add((lineNumber, table));
                }
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
