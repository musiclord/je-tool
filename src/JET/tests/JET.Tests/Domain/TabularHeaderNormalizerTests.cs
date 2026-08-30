using FsCheck.Xunit;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// TabularHeaderNormalizer 測試。
/// oracle：自 xlsx reader 抽出前的既有行為（golden，由 OpenXmlSaxTableReaderTests 鎖定整合面）。
/// 設計技術：等價分割——一般標頭 / 待 trim / 空白 / 重複 / 混合。
/// </summary>
public sealed class TabularHeaderNormalizerTests
{
    [Property(Replay = "20260815,101", MaxTest = 128)]
    public bool Normalize_BoundedHeadersAlwaysProduceGloballyUniqueNames(
        int seed,
        int[] headerSeeds)
    {
        var baseName = $"H{(uint)seed % 1_000u}";
        var boundedSeeds = (headerSeeds ?? []).Take(64).ToArray();
        if (boundedSeeds.Length == 0)
        {
            boundedSeeds = [seed];
        }

        var generated = boundedSeeds
            .Select((value, index) =>
                (ColumnNumber: index + 1, RawName: ((uint)value % 7u) switch
                {
                    0 => null,
                    1 => " ",
                    2 => baseName,
                    3 => $"{baseName}_2",
                    4 => $"H{(uint)value % 8u}",
                    5 => $"H{(uint)value % 8u}_2",
                    _ => $"COL_{index + 1}"
                }))
            .ToArray();
        IReadOnlyList<(int ColumnNumber, string? RawName)>[] cases =
        [
            generated,
            [(1, baseName), (2, baseName), (3, $"{baseName}_2")],
            [(1, $"{baseName}_2"), (2, $"{baseName}_2_2"), (3, $"{baseName}_2")],
            [(1, null), (2, "COL_1")]
        ];

        return cases.All(headers =>
        {
            var result = TabularHeaderNormalizer.Normalize(headers);
            return result.Count == headers.Count
                && result.All(static name => !string.IsNullOrWhiteSpace(name))
                && result.Distinct(StringComparer.Ordinal).Count() == result.Count;
        });
    }

    [Fact]
    public void Normalize_TrimsBlanksAndDeduplicates()
    {
        // 與 OpenXmlSaxTableReaderTests.ReadsSparseAndTypedCellsWithNormalizedHeaders 同形狀（golden）
        var result = TabularHeaderNormalizer.Normalize(
        [
            (1, "日期"),
            (2, "金額"),
            (3, ""),
            (4, "金額"),
            (5, " 摘要 ")
        ]);

        Assert.Equal(["日期", "金額", "COL_3", "金額_2", "摘要"], result);
    }

    [Fact]
    public void Normalize_BlankHeaderUsesColumnNumberNotPosition()
    {
        // xlsx 的欄位編號可能不從 1 開始（資料從 C 欄起）：COL_{n} 用實際欄號
        var result = TabularHeaderNormalizer.Normalize([(3, ""), (4, "金額")]);

        Assert.Equal(["COL_3", "金額"], result);
    }

    [Fact]
    public void Normalize_TripleDuplicate_GetsSequentialSuffixes()
    {
        var result = TabularHeaderNormalizer.Normalize([(1, "A"), (2, "A"), (3, "A")]);

        Assert.Equal(["A", "A_2", "A_3"], result);
    }

    [Fact]
    public void Normalize_NullAndWhitespaceTreatedAsBlank()
    {
        var result = TabularHeaderNormalizer.Normalize([(1, null), (2, "   ")]);

        Assert.Equal(["COL_1", "COL_2"], result);
    }

    [Fact]
    public void Normalize_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(TabularHeaderNormalizer.Normalize([]));
    }

    // IsPlaceholder（guide §3.1.5）：佔位欄名 = Normalize 對空白標頭產生的 COL_{欄號}。
    // 等價類：正準形（COL_+純數字）true；前綴缺數字 / 小寫 / 去重字尾 / 一般欄名 / 空字串 false。
    // COL_3_2 是「重複標頭去重」的產物（視為具名），不是佔位欄。
    [Theory]
    [InlineData("COL_1", true)]
    [InlineData("COL_20", true)]
    [InlineData("COL_999", true)]
    [InlineData("COL_", false)]
    [InlineData("COL_x", false)]
    [InlineData("col_3", false)]
    [InlineData("COL_3_2", false)]
    [InlineData("金額", false)]
    [InlineData("", false)]
    public void IsPlaceholder_MatchesOnlyCanonicalForm(string name, bool expected)
    {
        Assert.Equal(expected, TabularHeaderNormalizer.IsPlaceholder(name));
    }

    // FinalizeBatchColumns（guide §3.1.5 欄位集合收斂）。oracle：規格手算。
    // 等價類：具名空欄保留 / 無資料佔位欄剔除 / 有資料佔位欄保留 /
    //         範圍外 lazy 佔位欄附加 / 防衛性未知 key 仍納入。

    [Fact]
    public void FinalizeBatchColumns_NamedEmptyColumnIsKept()
    {
        // 具名標頭是來源 schema 的聲明，整欄無資料也保留
        var result = TabularHeaderNormalizer.FinalizeBatchColumns(
            ["A", "B", "C"],
            new HashSet<string>(StringComparer.Ordinal) { "A" });

        Assert.Equal(["A", "B", "C"], result);
    }

    [Fact]
    public void FinalizeBatchColumns_PlaceholderWithoutDataIsDropped()
    {
        // 雙工作表稀疏標頭合成案例：標頭縫隙佔位欄 COL_2 整欄無資料 → 不屬於批次欄位
        var result = TabularHeaderNormalizer.FinalizeBatchColumns(
            ["A", "COL_2", "C"],
            new HashSet<string>(StringComparer.Ordinal) { "A", "C" });

        Assert.Equal(["A", "C"], result);
    }

    [Fact]
    public void FinalizeBatchColumns_PlaceholderWithDataIsKeptInHeaderOrder()
    {
        var result = TabularHeaderNormalizer.FinalizeBatchColumns(
            ["A", "COL_2", "C"],
            new HashSet<string>(StringComparer.Ordinal) { "A", "COL_2" });

        Assert.Equal(["A", "COL_2", "C"], result);
    }

    [Fact]
    public void FinalizeBatchColumns_LazyPlaceholdersAppendedByColumnNumber()
    {
        // 標頭範圍外的資料欄（reader lazy 合成）：依欄號升冪附加在具名欄之後
        var result = TabularHeaderNormalizer.FinalizeBatchColumns(
            ["A", "B"],
            new HashSet<string>(StringComparer.Ordinal) { "A", "COL_7", "COL_5" });

        Assert.Equal(["A", "B", "COL_5", "COL_7"], result);
    }

    [Fact]
    public void FinalizeBatchColumns_UnknownObservedKeyIsKept()
    {
        // 防衛性：觀察到既不在標頭、也非佔位形的 key（理論上不該發生）寧可納入，
        // 不靜默丟棄——誠實優先於整潔
        var result = TabularHeaderNormalizer.FinalizeBatchColumns(
            ["A"],
            new HashSet<string>(StringComparer.Ordinal) { "A", "幽靈欄" });

        Assert.Equal(["A", "幽靈欄"], result);
    }
}
