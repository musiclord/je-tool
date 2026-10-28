using System.Text;
using System.Text.RegularExpressions;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// GL 的範圍只由 <c>GlEffectivePopulation.SqlPredicate</c> 決定（系統規格第 4 節）。產品原始碼裡每一段讀 target_gl_entry 的 SQL，
/// 都要在同一個 C# 陳述句裡帶上有效母體：直接寫 is_effective、呼叫 GlEffectivePopulation.SqlPredicate 或包裝它的
/// GlPopulationScopeSql.Predicate，或是經名稱含 population 的參數傳入。刻意讀全部列的查詢列在允許清單並附理由；
/// 清單上的項目找不到對應查詢時也失敗，避免留下過期的例外。
/// 2026-10-22 第一次執行時，授權名單比對把 GL(未納入測試) 的建立人員也算進去，是唯一沒有理由的查詢（預篩選收斂計畫主題一，
/// 收據 20261022-062415992）。
/// 表名不得當成值傳進讀取查詢：表名從參數傳入時，讀取寫在另一個陳述句，上面的掃描看不到。2026-10-28 複審查到資料預覽 GL 頁
/// 就是這樣繞過，改成表名與母體條件寫在同一句；另加一項檢查，GL 分錄表名單獨成為字串值的地方只能是不讀資料的用途，列在第二份允許清單。
/// </summary>
public sealed partial class GlEffectivePopulationReadGuardTests
{
    /// <summary>刻意不限定母體的查詢：檔案、陳述句裡一定出現的片段（空白已壓成單一空格）、理由。</summary>
    private sealed record AllowedRead(string File, string Marker, string Reason);

    private static readonly AllowedRead[] AllowedReads =
    [
        new("src/JET/JET/AuditCore/ValidationProcedures.cs",
            ".Replace(\"FROM target_gl_entry\", $\"FROM {schemaPrefix}target_gl_entry\")",
            "只把共用 CTE 裡的資料表名稱加上 schema 前綴；有效母體寫在 CompletenessDiffCte 本身。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalGlRepository.cs",
            "ROW_NUMBER() OVER (PARTITION BY document_number ORDER BY source_row_number) AS rn FROM target_gl_entry",
            "匯入時為整批剛寫入的列補傳票項次，GL 與 GL(未納入測試) 都要編號。"),
        // 預篩選收斂計畫階段四把 GL 整欄空白偵測從兩個 GL 倉儲搬到 GlMappedColumnAudit，由欄位目錄產生查詢（2026-10-23），
        // 兩筆倉儲的例外合成這一筆。第一次失敗：20261023-024707063-4d0429b2cd094187af7242292420a252，搬家後兩筆倉儲例外過期、新位置沒有例外。
        new("src/JET/JET/Infrastructure/Sql/GlMappedColumnAudit.cs",
            "FROM {prefix}target_gl_entry;",
            "匯入當下對整批剛寫入的列偵測整欄空白，要看全部的列。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalGlRepository.cs",
            "WHERE g.batch_id = @batchId AND g.entry_id > @cursor",
            "匯入當下依批次回取原始列，為整批剛寫入的列寫入攸關資料元素欄位的值。"),
        new("src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerGlRepository.cs",
            "ROW_NUMBER() OVER (PARTITION BY document_number ORDER BY source_row_number) AS rn FROM {s}.target_gl_entry",
            "匯入時為整批剛寫入的列補傳票項次，GL 與 GL(未納入測試) 都要編號。"),
        new("src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerGlRepository.cs",
            "WHERE g.batch_id = @batchId AND g.entry_id > @cursor",
            "匯入當下依批次回取原始列，為整批剛寫入的列寫入攸關資料元素欄位的值。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalNullRecordsPageRepository.cs",
            "\"WHERE \" + predicate + paging.Predicate",
            "WHERE 由 NullRecordsCategoryPredicate.Scoped 產生，裡面已含有效母體。"),
        new("src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerNullRecordsPageRepository.cs",
            "\"WHERE \" + predicate + paging.Predicate",
            "WHERE 由 NullRecordsCategoryPredicate.Scoped 產生，裡面已含有效母體。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalRawGlExportRepository.cs",
            "WHERE g.entry_id IN (",
            "依已選出的 entry_id 回取原始列，母體在選出時已限定。"),
        new("src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerRawGlExportRepository.cs",
            "WHERE g.entry_id IN (__ENTRY_IDS__)",
            "依已選出的 entry_id 回取原始列，母體在選出時已限定。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalValidationRunRepository.cs",
            "SELECT {columns} FROM target_gl_entry;",
            "四類空值計數的條件由 NullRecordsCategoryPredicate.Scoped 產生，各自已含有效母體。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalValidationRunRepository.cs",
            "WHERE {ValidationProcedures.NullPostDateSourceQualityPredicate};",
            "缺總帳日期的來源品質是 ValidationProcedures 寫明的唯一原始列例外。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalValidationRunRepository.cs",
            "{flags} FROM target_gl_entry WHERE ({anyMatch})",
            "空值明細的旗標與條件由 NullRecordsCategoryPredicate.Scoped 產生，已含有效母體。"),
        new("src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerValidationRunRepository.cs",
            "SELECT {{columns}} FROM {s}.target_gl_entry;",
            "四類空值計數的條件由 NullRecordsCategoryPredicate.Scoped 產生，各自已含有效母體。"),
        new("src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerValidationRunRepository.cs",
            "WHERE {ValidationProcedures.NullPostDateSourceQualityPredicate};",
            "缺總帳日期的來源品質是 ValidationProcedures 寫明的唯一原始列例外。"),
        new("src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerValidationRunRepository.cs",
            "{{flags}} FROM {s}.target_gl_entry WHERE ({{anyMatch}})",
            "空值明細的旗標與條件由 NullRecordsCategoryPredicate.Scoped 產生，已含有效母體。"),
        new("src/JET/JET/Infrastructure/Sql/SourceQualityPageReader.cs",
            "ValidationProcedures.NullPostDateSourceQualityPredicateWith(\"g\")",
            "缺總帳日期的來源品質分頁，和計數是同一個例外。")
    ];

    /// <summary>GL 分錄表名單獨成為字串值、但不拿來讀資料的地方：檔案、陳述句裡一定出現的片段、理由。</summary>
    private static readonly AllowedRead[] AllowedTableNameValues =
    [
        new("src/JET/JET/AuditCore/JetSchemaCatalog.cs",
            "new(\"target_gl_entry\", \"GL、GL(未納入測試)\"",
            "資料表目錄只列名稱與說明，不讀資料。"),
        new("src/JET/JET/Infrastructure/Persistence/DuckDb/DuckDbProjectDatabase.cs",
            "connection, transaction, \"target_gl_entry\", \"",
            "舊案件升版時補欄位，不讀資料。"),
        new("src/JET/JET/Infrastructure/Persistence/Sqlite/SqliteProjectDatabase.cs",
            "connection, transaction, \"target_gl_entry\", \"",
            "舊案件升版時補欄位，不讀資料。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalGlRepository.cs",
            "CreateBulkRowWriter(connection, transaction, \"target_gl_entry\", TargetColumns)",
            "匯入時批次寫入，不讀資料。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalImportRepository.cs",
            "DatasetKind.Gl => \"target_gl_entry\"",
            "只用在重新匯入前的 DELETE，本機與 SQL Server 匯入倉儲共用。")
    ];

    // 2026-10-28 複審新增。資料預覽 GL 頁先改好才補這項檢查，沒有第一次失敗的收據；下面第一則 Theory 案例就是資料預覽原本的寫法。
    [Fact]
    public void ProductionCode_DoesNotPassTheGlEntryTableNameAsAValue_ExceptListedNonReads()
    {
        var root = TestRepositoryPaths.RepositoryRoot;
        var product = Path.Combine(root, "src", "JET", "JET");
        var violations = new List<string>();
        var used = new HashSet<AllowedRead>();
        foreach (var file in Directory
                     .EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
                     .Where(file => !Path.GetRelativePath(product, file)
                         .Split(Path.DirectorySeparatorChar)
                         .Any(part => part is "bin" or "obj"))
                     .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var value in TableNameValues(File.ReadAllText(file)))
            {
                var allowance = AllowedTableNameValues.FirstOrDefault(entry =>
                    entry.File == relative && value.Statement.Contains(entry.Marker, StringComparison.Ordinal));
                if (allowance is null)
                {
                    violations.Add($"{relative}:{value.Line}: {Truncate(value.Statement)}");
                    continue;
                }

                used.Add(allowance);
            }
        }

        violations.AddRange(AllowedTableNameValues
            .Where(entry => !used.Contains(entry))
            .Select(entry => $"允許清單過期，找不到對應的表名：{entry.File} 的「{entry.Marker}」"));

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("select.CommandText = DataPreviewGlQuery.Select(\n    columns, \"target_gl_entry\", \"target_gl_rde_value\", filter, \"LIMIT @limit OFFSET @offset\");")]
    [InlineData("var sql = Select(columns, \"{s}.target_gl_entry\", \"{s}.target_gl_rde_value\", paging);")]
    public void Detector_FlagsTheGlEntryTableNamePassedAsAValue(string source)
    {
        Assert.Single(TableNameValues(source));
    }

    [Theory]
    [InlineData("var sql = $\"FROM {tablePrefix}target_gl_entry WHERE {population}\";")]
    [InlineData("// Select(columns, \"target_gl_entry\") 只是註解\nvar other = 1;")]
    [InlineData("var name = \"target_gl_entry_archive\";")]
    public void Detector_LeavesTableNamesInsideSqlAndCommentsAlone(string source)
    {
        Assert.Empty(TableNameValues(source));
    }

    [Fact]
    public void ProductionSql_ReadsTargetGlEntryWithinEffectivePopulation_OrIsListedWithAReason()
    {
        var root = TestRepositoryPaths.RepositoryRoot;
        var product = Path.Combine(root, "src", "JET", "JET");
        var files = Directory
            .EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(product, file)
                .Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);

        var violations = new List<string>();
        var used = new HashSet<AllowedRead>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var read in UnscopedReads(File.ReadAllText(file)))
            {
                var allowance = AllowedReads.FirstOrDefault(entry =>
                    entry.File == relative && read.Statement.Contains(entry.Marker, StringComparison.Ordinal));
                if (allowance is null)
                {
                    violations.Add($"{relative}:{read.Line}: {Truncate(read.Statement)}");
                    continue;
                }

                used.Add(allowance);
            }
        }

        violations.AddRange(AllowedReads
            .Where(entry => !used.Contains(entry))
            .Select(entry => $"允許清單過期，找不到對應的查詢：{entry.File} 的「{entry.Marker}」"));

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("command.CommandText = $\"\"\"\n    SELECT COUNT(*)\n    FROM {prefix}target_gl_entry g\n    WHERE g.created_by IS NOT NULL;\n    \"\"\";")]
    [InlineData("var sql = \"SELECT g.entry_id \" + \"FROM target_gl_entry g \" + \"WHERE g.batch_id = @batchId;\";")]
    [InlineData("return $\"JOIN {{s}}.target_gl_entry g ON g.entry_id = r.entry_id\";")]
    [InlineData("var sql = $$\"\"\"\n    SELECT TOP ({{limit}}) * FROM {s}.target_gl_entry\n    \"\"\";")]
    public void Detector_FlagsReadsWithoutAPopulationPredicate(string source)
    {
        Assert.Single(UnscopedReads(source));
    }

    [Theory]
    [InlineData("command.CommandText = $\"SELECT COUNT(*) FROM target_gl_entry g WHERE {GlEffectivePopulation.SqlPredicate(\"g\")};\";")]
    [InlineData("var sql = \"SELECT 1 FROM target_gl_entry WHERE is_effective = 0;\";")]
    [InlineData("var sql = $\"FROM {prefix}target_gl_entry g WHERE {GlPopulationScopeSql.Predicate(context, \"g\")}\";")]
    [InlineData("string Set(string p) => $\"SELECT g.document_number FROM {schemaPrefix}target_gl_entry g WHERE {population} AND ({p})\";")]
    [InlineData("var sql = $\"FROM {schemaPrefix}target_gl_entry g WHERE {populationPredicate} AND ({hitPopulationPredicate})\";")]
    [InlineData("// SELECT * FROM target_gl_entry 只是註解\nvar other = 1;")]
    [InlineData("/// <summary>JOIN target_gl_entry 取顯示欄</summary>\npublic sealed class Reader {")]
    [InlineData("command.CommandText = \"DELETE FROM target_gl_entry;\";")]
    public void Detector_AcceptsScopedReads_AndIgnoresCommentsAndDeletes(string source)
    {
        Assert.Empty(UnscopedReads(source));
    }

    [Fact]
    public void Detector_JudgesEachStatementOnItsOwn()
    {
        const string source =
            "a.CommandText = \"SELECT 1 FROM target_gl_entry WHERE is_effective = 1;\";\n" +
            "b.CommandText = \"SELECT 1 FROM target_gl_entry;\";";

        var read = Assert.Single(UnscopedReads(source));
        Assert.Equal(2, read.Line);
    }

    private sealed record UnscopedRead(int Line, string Statement);

    /// <summary>每個 C# 陳述句（以分號或大括號切開，註解已去除）裡讀 target_gl_entry 卻沒有母體述詞的查詢。</summary>
    private static IReadOnlyList<UnscopedRead> UnscopedReads(string text)
    {
        var reads = new List<UnscopedRead>();
        foreach (var (offset, statement) in Statements(text))
        {
            var matches = TargetGlEntryRead().Matches(statement)
                .Where(match => !DeleteBefore().IsMatch(statement[..match.Index]))
                .ToArray();
            if (matches.Length == 0 || PopulationPredicate().IsMatch(statement))
            {
                continue;
            }

            var line = 1 + CountNewLines(text, offset) + CountNewLines(statement, matches[0].Index);
            reads.Add(new UnscopedRead(line, Whitespace().Replace(statement.Trim(), " ")));
        }

        return reads;
    }

    /// <summary>以分號與大括號切開原始碼，跳過字串、字元與註解；回傳每句的起點與去掉註解後的文字。</summary>
    private static IEnumerable<(int Offset, string Text)> Statements(string text)
    {
        var buffer = new StringBuilder();
        var start = 0;
        var segment = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '/' && Peek(text, i + 1) == '/')
            {
                buffer.Append(text, segment, i - segment);
                var newline = text.IndexOf('\n', i);
                i = newline < 0 ? text.Length : newline;
                segment = i;
                continue;
            }

            if (c == '/' && Peek(text, i + 1) == '*')
            {
                buffer.Append(text, segment, i - segment);
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                segment = i;
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(text, i);
                continue;
            }

            if (c is '$' or '@' or '"')
            {
                var end = SkipStringLiteral(text, i);
                if (end >= 0)
                {
                    i = end;
                    continue;
                }
            }

            if (c is ';' or '{' or '}')
            {
                buffer.Append(text, segment, i + 1 - segment);
                yield return (start, buffer.ToString());
                buffer.Clear();
                start = i + 1;
                segment = i + 1;
            }

            i++;
        }

        buffer.Append(text, segment, text.Length - segment);
        if (start < text.Length)
        {
            yield return (start, buffer.ToString());
        }
    }

    /// <summary>text[i] 是字串前綴（$、@）或引號時回傳字串結束後的位置，否則回傳 -1。處理一般、逐字、原始與插補字串。</summary>
    private static int SkipStringLiteral(string text, int i)
    {
        var j = i;
        while (j < text.Length && text[j] is '$' or '@')
        {
            j++;
        }

        if (j >= text.Length || text[j] != '"')
        {
            return -1;
        }

        var interpolated = text.AsSpan(i, j - i).Contains('$');
        var verbatim = text.AsSpan(i, j - i).Contains('@');
        if (text.AsSpan(j).StartsWith("\"\"\"", StringComparison.Ordinal))
        {
            var quotes = 0;
            while (j + quotes < text.Length && text[j + quotes] == '"')
            {
                quotes++;
            }

            var close = text.IndexOf(new string('"', quotes), j + quotes, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + quotes;
        }

        j++;
        while (j < text.Length)
        {
            var c = text[j];
            if (verbatim)
            {
                if (c == '"')
                {
                    if (Peek(text, j + 1) == '"')
                    {
                        j += 2;
                        continue;
                    }

                    return j + 1;
                }
            }
            else
            {
                if (c == '\\')
                {
                    j += 2;
                    continue;
                }

                if (c == '"')
                {
                    return j + 1;
                }
            }

            if (interpolated && c == '{')
            {
                if (Peek(text, j + 1) == '{')
                {
                    j += 2;
                    continue;
                }

                j = SkipInterpolationHole(text, j + 1);
                continue;
            }

            j++;
        }

        return j;
    }

    private static int SkipInterpolationHole(string text, int i)
    {
        var depth = 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '$' or '@' or '"')
            {
                var end = SkipStringLiteral(text, i);
                if (end >= 0)
                {
                    i = end;
                    continue;
                }
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(text, i);
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i + 1;
                }
            }

            i++;
        }

        return i;
    }

    private static int SkipCharLiteral(string text, int i)
    {
        var j = i + 1;
        while (j < text.Length)
        {
            if (text[j] == '\\')
            {
                j += 2;
                continue;
            }

            if (text[j] == '\'')
            {
                return j + 1;
            }

            if (text[j] == '\n')
            {
                return j;
            }

            j++;
        }

        return j;
    }

    private static char Peek(string text, int index) => index < text.Length ? text[index] : '\0';

    private static int CountNewLines(string text, int endExclusive)
    {
        var count = 0;
        for (var i = 0; i < endExclusive && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>每個陳述句（註解已去除）裡，GL 分錄表名單獨成為字串值的地方，例如當成參數傳給另一段組 SQL 的程式。</summary>
    private static IReadOnlyList<UnscopedRead> TableNameValues(string text)
    {
        var values = new List<UnscopedRead>();
        foreach (var (offset, statement) in Statements(text))
        {
            var match = TableNameValue().Match(statement);
            if (!match.Success)
            {
                continue;
            }

            var line = 1 + CountNewLines(text, offset) + CountNewLines(statement, match.Index);
            values.Add(new UnscopedRead(line, Whitespace().Replace(statement.Trim(), " ")));
        }

        return values;
    }

    private static string Truncate(string statement) =>
        statement.Length <= 200 ? statement : statement[..200] + "…";

    [GeneratedRegex(@"\b(?:FROM|JOIN)\s+(?:\{\{?[A-Za-z_]*\}\}?\.?|\[[^\]]+\]\.)?target_gl_entry\b", RegexOptions.IgnoreCase)]
    private static partial Regex TargetGlEntryRead();

    [GeneratedRegex(@"""(?:\{\{?s\}\}?\.)?target_gl_entry""")]
    private static partial Regex TableNameValue();

    [GeneratedRegex(@"DELETE\s+$", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteBefore();

    [GeneratedRegex(@"is_effective|GlEffectivePopulation\.SqlPredicate\(|GlPopulationScopeSql\.Predicate\(|populationScopePredicate\(|\{(?:hit)?population(?:Predicate(?:Sql)?)?\}")]
    private static partial Regex PopulationPredicate();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
