using System.Text;
using System.Text.RegularExpressions;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// GL 的範圍只由 <c>GlExcludedPopulation.SqlPredicate</c> 決定（系統規格第 4 節）。產品原始碼裡每一段讀 gl 的 SQL，
/// 都要在同一個 C# 陳述句裡帶上 GL 的範圍條件：直接寫 is_excluded、呼叫 GlExcludedPopulation.SqlPredicate 或包裝它的
/// GlPopulationScopeSql.Predicate，或是經名稱含 population 的參數傳入。刻意讀全部列的查詢列在允許清單並附理由；
/// 清單上的項目找不到對應查詢時也失敗，避免留下過期的例外。
/// 2026-10-22 第一次執行時，授權名單比對把 GL(未納入測試) 的建立人員也算進去，是唯一沒有理由的查詢（第一次執行的
/// 收據 20261022-062415992）。
/// 表名不得當成值傳進讀取查詢：表名從參數傳入時，讀取寫在另一個陳述句，上面的掃描看不到。2026-10-28 複審查到資料預覽 GL 頁
/// 就是這樣繞過，改成表名與母體條件寫在同一句；另加一項檢查，GL 分錄表名單獨成為字串值的地方只能是不讀資料的用途，列在第二份允許清單。
/// </summary>
public sealed partial class GlExcludedPopulationReadGuardTests
{
    /// <summary>刻意不限定母體的查詢：檔案、陳述句裡一定出現的片段（空白已壓成單一空格）、理由。</summary>
    private sealed record AllowedRead(string File, string Marker, string Reason);

    // 兩份允許清單原本也列 SQL Server 倉儲的查詢。只保留 SQLite 之後那些檔案已刪除，項目跟著拿掉（裁定 2026-11-06-08）；
    // 第一次失敗：Focused 收據 20261106-084107421-33f747f01c134f3ab499d047fcc1c3ed。
    private static readonly AllowedRead[] AllowedReads =
    [
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalGlRepository.cs",
            "ROW_NUMBER() OVER (PARTITION BY voucher_number ORDER BY source_row_number) AS rn FROM gl",
            "匯入時為整批剛寫入的列補傳票項次，GL 與 GL(未納入測試) 都要編號。"),
        // GL 整欄空白偵測從兩個 GL 倉儲搬到 GlMappedColumnAudit，由欄位目錄產生查詢，
        // 兩筆倉儲的例外合成這一筆。第一次失敗：20261023-024707063-4d0429b2cd094187af7242292420a252，搬家後兩筆倉儲例外過期、新位置沒有例外。
        new("src/JET/JET/Infrastructure/Sql/GlMappedColumnAudit.cs",
            "FROM {prefix}gl;",
            "匯入當下對整批剛寫入的列偵測整欄空白，要看全部的列。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalGlRepository.cs",
            "WHERE g.batch_id = @batchId AND g.entry_id > @cursor",
            "匯入當下依批次回取原始列，為整批剛寫入的列寫入攸關資料元素欄位的值。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalBlankRecordsPageRepository.cs",
            "\"WHERE \" + predicate + paging.Predicate",
            "WHERE 由 BlankRecordsCategoryPredicate.Scoped 產生，裡面已經只看納入測試的分錄。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalRawGlExportRepository.cs",
            "WHERE g.entry_id IN (",
            "依已選出的 entry_id 回取原始列，母體在選出時已限定。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalValidationRunRepository.cs",
            "SELECT {columns} FROM gl;",
            "四類空值計數的條件由 BlankRecordsCategoryPredicate.Scoped 產生，各自都只看納入測試的分錄。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalValidationRunRepository.cs",
            "WHERE {ValidationProcedures.BlankGlDateSourceQualityPredicate};",
            "總帳日期空白是 ValidationProcedures 寫明的唯一原始列例外。"),
        // 空值明細多讀傳票核准日（裁定 2026-11-06-13），approval_date 接在旗標後面，認這句查詢的片段跟著改；
        // 查詢的範圍條件與理由不變。第一次失敗：Focused 收據 20261106-084107421-33f747f01c134f3ab499d047fcc1c3ed。
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalValidationRunRepository.cs",
            "approval_date FROM gl WHERE ({anyMatch})",
            "空值明細的旗標與條件由 BlankRecordsCategoryPredicate.Scoped 產生，已經只看納入測試的分錄。"),
        new("src/JET/JET/Infrastructure/Sql/SourceQualityPageReader.cs",
            "ValidationProcedures.BlankGlDateSourceQualityPredicateWith(\"g\")",
            "總帳日期空白的分頁，和計數是同一個例外。")
    ];

    /// <summary>GL 分錄表名單獨成為字串值、但不拿來讀資料的地方：檔案、陳述句裡一定出現的片段、理由。</summary>
    private static readonly AllowedRead[] AllowedTableNameValues =
    [
        new("src/JET/JET/AuditCore/JetSchemaCatalog.cs",
            "public const string GlTableName = \"gl\";",
            "表名常數的唯一定義。"),
        new("src/JET/JET/AuditCore/JetSchemaCatalog.cs",
            "new(GlTableName, \"GL、GL(未納入測試)\"",
            "資料表目錄只列名稱與說明，不讀資料。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalGlRepository.cs",
            "CreateBulkRowWriter(connection, transaction, JetSchemaCatalog.GlTableName, TargetColumns)",
            "匯入時批次寫入，不讀資料。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalImportRepository.cs",
            "DatasetKind.Gl => JetSchemaCatalog.GlTableName",
            "只用在重新匯入前的 DELETE。"),
        new("src/JET/JET/Infrastructure/Persistence/Local/LocalDataPreviewRepository.cs",
            "StagingPreviewAsync(connection, \"gl\", \"pbc_gl\"",
            "這裡的 \"gl\" 是資料集種類，用來找 PBC-GL 的匯入批次；讀的是 pbc_gl，不是 GL 分錄表。"),
    ];

    // 2026-10-28 複審新增。資料預覽 GL 頁先改好才補這項檢查，沒有第一次失敗的收據；下面第一則 Theory 案例就是資料預覽原本的寫法。
    // 2026-10-30 GL 分錄表改名為 gl，和資料集種類的值 "gl" 同字，原本逐字找 "gl" 的寫法把資料集種類都當成表名
    // （第一次失敗的收據 20261030-094446703-a10ae02321f1477fa8df977229a4fd17）。改成三種情形才算表名：用表名常數
    // JetSchemaCatalog.GlTableName；字串帶 schema 限定（"{s}.gl"）；或字串 "gl" 和其他表名字串、SQL 文字寫在同一句。
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
    [InlineData("select.CommandText = DataPreviewGlQuery.Select(\n    columns, \"gl\", \"gl_rde_value\", filter, \"LIMIT @limit OFFSET @offset\");")]
    [InlineData("var sql = Select(columns, \"{s}.gl\", \"{s}.gl_rde_value\", paging);")]
    [InlineData("var rows = Read(connection, \"gl\", \"WHERE entry_id > @cursor\");")]
    [InlineData("var table = JetSchemaCatalog.GlTableName;")]
    [InlineData("var sql = Select(columns, \"{prefix}gl\", paging);")]
    public void Detector_FlagsTheGlEntryTableNamePassedAsAValue(string source)
    {
        Assert.Single(TableNameValues(source));
    }

    [Theory]
    [InlineData("var sql = $\"FROM {tablePrefix}gl WHERE {population}\";")]
    [InlineData("// Select(columns, \"gl\") 只是註解\nvar other = 1;")]
    [InlineData("var name = \"gl_archive\";")]
    [InlineData("case \"gl\":")]
    [InlineData("var payload = new { kind = \"gl\", rows = 3 };")]
    [InlineData("var gl = ReadCurrentGl(root.GetProperty(\"gl\"));")]
    [InlineData("await log.TryRequiresMappingRecommitAuditAsync(projectId, \"gl\", cancellationToken);")]
    [InlineData("string[] names = [\"pbcGl\", \"pbcTb\", \"gl\", \"glExcluded\", \"tb\", \"accountMapping\"];")]
    public void Detector_LeavesTableNamesInsideSqlAndCommentsAlone(string source)
    {
        Assert.Empty(TableNameValues(source));
    }

    [Fact]
    public void ProductionSql_ReadsGlWithTheExcludedPopulationPredicate_OrIsListedWithAReason()
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
    [InlineData("command.CommandText = $\"\"\"\n    SELECT COUNT(*)\n    FROM {prefix}gl g\n    WHERE g.created_by IS NOT NULL;\n    \"\"\";")]
    [InlineData("var sql = \"SELECT g.entry_id \" + \"FROM gl g \" + \"WHERE g.batch_id = @batchId;\";")]
    [InlineData("return $\"JOIN {{s}}.gl g ON g.entry_id = r.entry_id\";")]
    [InlineData("var sql = $$\"\"\"\n    SELECT TOP ({{limit}}) * FROM {s}.gl\n    \"\"\";")]
    public void Detector_FlagsReadsWithoutAPopulationPredicate(string source)
    {
        Assert.Single(UnscopedReads(source));
    }

    [Theory]
    [InlineData("command.CommandText = $\"SELECT COUNT(*) FROM gl g WHERE {GlExcludedPopulation.SqlPredicate(\"g\")};\";")]
    [InlineData("var sql = \"SELECT 1 FROM gl WHERE is_excluded = 1;\";")]
    [InlineData("var sql = $\"FROM {prefix}gl g WHERE {GlPopulationScopeSql.Predicate(context, \"g\")}\";")]
    [InlineData("string Set(string p) => $\"SELECT g.voucher_number FROM {schemaPrefix}gl g WHERE {population} AND ({p})\";")]
    [InlineData("var sql = $\"FROM {schemaPrefix}gl g WHERE {populationPredicate} AND ({hitPopulationPredicate})\";")]
    [InlineData("// SELECT * FROM gl 只是註解\nvar other = 1;")]
    [InlineData("/// <summary>JOIN gl 取顯示欄</summary>\npublic sealed class Reader {")]
    [InlineData("command.CommandText = \"DELETE FROM gl;\";")]
    public void Detector_AcceptsScopedReads_AndIgnoresCommentsAndDeletes(string source)
    {
        Assert.Empty(UnscopedReads(source));
    }

    [Fact]
    public void Detector_JudgesEachStatementOnItsOwn()
    {
        const string source =
            "a.CommandText = \"SELECT 1 FROM gl WHERE is_excluded = 0;\";\n" +
            "b.CommandText = \"SELECT 1 FROM gl;\";";

        var read = Assert.Single(UnscopedReads(source));
        Assert.Equal(2, read.Line);
    }

    private sealed record UnscopedRead(int Line, string Statement);

    /// <summary>每個 C# 陳述句（以分號或大括號切開，註解已去除）裡讀 gl 卻沒有母體述詞的查詢。</summary>
    private static IReadOnlyList<UnscopedRead> UnscopedReads(string text)
    {
        var reads = new List<UnscopedRead>();
        foreach (var (offset, statement) in Statements(text))
        {
            var matches = GlRead().Matches(statement)
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
            var index = TableNameValueIndex(statement);
            if (index < 0)
            {
                continue;
            }

            var line = 1 + CountNewLines(text, offset) + CountNewLines(statement, index);
            values.Add(new UnscopedRead(line, Whitespace().Replace(statement.Trim(), " ")));
        }

        return values;
    }

    /// <summary>
    /// 陳述句裡 GL 分錄表名被當成值的位置，沒有時回傳 -1。字串 "gl" 也是資料集種類的值，
    /// 只在同一句還有別的表名字串或 SQL 文字時才算表名；表名常數與帶 schema 限定的字串一律算。
    /// </summary>
    private static int TableNameValueIndex(string statement)
    {
        var constant = GlTableNameConstant().Match(statement);
        if (constant.Success)
        {
            return constant.Index;
        }

        var qualified = QualifiedGlTableLiteral().Match(statement);
        if (qualified.Success)
        {
            return qualified.Index;
        }

        var bare = BareGlLiteral().Match(statement);
        if (!bare.Success)
        {
            return -1;
        }

        var sqlContext = statement.Contains("CommandText", StringComparison.Ordinal)
            || StringLiterals().Matches(statement).Any(literal =>
                SqlText().IsMatch(literal.Groups["text"].Value)
                || OtherTableNames.Contains(literal.Groups["text"].Value));
        return sqlContext ? bare.Index : -1;
    }

    /// <summary>不和資料集種類值同字的專案表名；和 "gl" 寫在同一句時，表示這一句在傳表名。</summary>
    private static readonly HashSet<string> OtherTableNames = JET.AuditCore.JetSchemaCatalog.All
        .Select(entry => entry.PhysicalName)
        .Except(Enum.GetValues<JET.Domain.DatasetKind>().Select(JET.Domain.DatasetKindNames.ToStorageName))
        .ToHashSet(StringComparer.Ordinal);

    private static string Truncate(string statement) =>
        statement.Length <= 200 ? statement : statement[..200] + "…";

    [GeneratedRegex(@"\b(?:FROM|JOIN)\s+(?:\{\{?[A-Za-z_]*\}\}?\.?|\[[^\]]+\]\.)?gl\b", RegexOptions.IgnoreCase)]
    private static partial Regex GlRead();

    [GeneratedRegex(@"\bGlTableName\b")]
    private static partial Regex GlTableNameConstant();

    [GeneratedRegex(@"""(?:\{\{?s\}\}?\.|\{\{?(?:prefix|schemaPrefix)\}\}?)gl""")]
    private static partial Regex QualifiedGlTableLiteral();

    [GeneratedRegex(@"""gl""")]
    private static partial Regex BareGlLiteral();

    [GeneratedRegex(@"""(?<text>(?:[^""\\]|\\.)*)""")]
    private static partial Regex StringLiterals();

    [GeneratedRegex(@"\b(?:SELECT|FROM|JOIN|WHERE|INSERT|UPDATE|DELETE|LIMIT)\b")]
    private static partial Regex SqlText();

    [GeneratedRegex(@"DELETE\s+$", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteBefore();

    [GeneratedRegex(@"is_excluded|GlExcludedPopulation\.SqlPredicate\(|GlPopulationScopeSql\.Predicate\(|populationScopePredicate\(|\{(?:hit)?population(?:Predicate(?:Sql)?)?\}")]
    private static partial Regex PopulationPredicate();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
