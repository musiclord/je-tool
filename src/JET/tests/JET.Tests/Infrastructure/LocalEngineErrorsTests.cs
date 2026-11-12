using System.Reflection;
using System.Text.RegularExpressions;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 本地引擎錯誤映射的單元測試。oracle 是已登錄的 wire code，
/// SQLite 以公開 primary error code 建構真實例外。
/// LocalEngineErrors 以反射尋找，找不到時測試會明確指出缺少這個型別。
/// </summary>
public sealed class LocalEngineErrorsTests
{
    private static readonly (string FieldName, string WireValue)[] RegisteredErrorCodes =
    [
        ("DatabaseBusy", "database_busy"),
        ("DatabaseLocked", "database_locked"),
        ("DatabaseStorageFull", "database_storage_full"),
        ("DatabaseCorrupt", "database_corrupt"),
        // 下面三個錯誤碼一起核對常數與正式程式不寫裸字串。
        ("DatabaseReadOnly", "database_read_only"),
        ("DatabaseUnavailable", "database_unavailable"),
        ("DatabaseOutOfMemory", "database_out_of_memory"),
        ("BridgeError", "bridge_error"),
        ("InvalidProjectSchema", "invalid_project_schema"),
        ("UnsupportedProvider", "unsupported_provider")
    ];

    // 等價分割：SQLite 四個可處置的 primary result code 各取一個代表值。
    [Theory]
    [InlineData(5, "database_busy")]
    [InlineData(6, "database_locked")]
    [InlineData(13, "database_storage_full")]
    [InlineData(11, "database_corrupt")]
    public void TryTranslate_MappedSqlitePrimaryCode_ReturnsRegisteredCode(int sqliteCode, string expectedCode)
    {
        var translated = TryTranslate(new SqliteException("測試用 SQLite 引擎錯誤", sqliteCode));

        Assert.NotNull(translated);
        Assert.Equal(expectedCode, translated.Code);
    }

    // 唯讀、沒有權限、開不了檔、讀寫失敗與記憶體不足各有自己的錯誤碼，不落到通用的非預期錯誤。
    [Theory]
    [InlineData(8, "database_read_only")]
    [InlineData(8 | (1 << 8), "database_read_only")] // SQLITE_READONLY_RECOVERY
    [InlineData(8 | (5 << 8), "database_read_only")] // SQLITE_READONLY_CANTINIT：共用記憶體檔無法寫入
    [InlineData(3, "database_unavailable")]
    [InlineData(14, "database_unavailable")]
    [InlineData(10, "database_unavailable")]
    [InlineData(10 | (10 << 8), "database_unavailable")] // SQLITE_IOERR_DELETE
    [InlineData(7, "database_out_of_memory")]
    [InlineData(10 | (12 << 8), "database_out_of_memory")] // SQLITE_IOERR_NOMEM 算記憶體不足，不算讀寫錯誤
    public void TryTranslate_SqliteAccessAndMemoryCodes_ReturnTheReason(int extendedCode, string expectedCode)
    {
        var translated = TryTranslate(new SqliteException("測試用 SQLite 引擎錯誤", extendedCode & 0xFF, extendedCode));

        Assert.NotNull(translated);
        Assert.Equal(expectedCode, translated.Code);
        Assert.DoesNotContain("非預期", translated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryTranslate_SqliteExtendedCode_UsesLowEightBits()
    {
        const int extendedBusyCode = 5 | (3 << 8);

        var translated = TryTranslate(new SqliteException("測試用 SQLite extended error", 5, extendedBusyCode));

        Assert.NotNull(translated);
        Assert.Equal("database_busy", translated.Code);
    }

    [Fact]
    public void TryTranslate_EngineExceptionNestedInInnerChain_TranslatesThroughChain()
    {
        var wrapped = new InvalidOperationException(
            "外層包裝",
            new SqliteException("database is busy", 5));

        var translated = TryTranslate(wrapped);

        Assert.NotNull(translated);
        Assert.Equal("database_busy", translated.Code);
    }

    [Fact]
    public void TryTranslate_UnmappedSqliteCode_ReturnsNull()
    {
        Assert.Null(TryTranslate(new SqliteException("SQL logic error", 1)));
    }

    [Fact]
    public void TryTranslate_NonEngineException_ReturnsNull()
    {
        Assert.Null(TryTranslate(new InvalidOperationException("非引擎例外")));
    }

    [Theory]
    [MemberData(nameof(RegisteredErrorCodeCases))]
    public void JetErrorCodes_RegisteredWireLiteral_HasNamedConstant(string fieldName, string expectedValue)
    {
        var field = typeof(JetErrorCodes).GetField(fieldName, BindingFlags.Public | BindingFlags.Static);

        Assert.True(field is not null, $"JetErrorCodes 缺少公開常數 {fieldName}。");
        Assert.True(field.IsLiteral && !field.IsInitOnly, $"JetErrorCodes.{fieldName} 必須是 const string。");
        Assert.Equal(expectedValue, field.GetRawConstantValue());
    }

    [Fact]
    public void ProductionErrorConstruction_RegisteredWireCodes_UsesJetErrorCodesConstants()
    {
        var sourceRoot = SourceRoot();
        var productionRoot = Path.Combine(sourceRoot, "JET");
        var literals = string.Join("|", RegisteredErrorCodes.Select(item => Regex.Escape(item.WireValue)));
        var bareConstruction = new Regex(
            $"new\\s+(?:JetActionException|JetErrorDto)\\s*\\(\\s*\"(?<code>{literals})\"",
            RegexOptions.CultureInvariant);

        var violations = Directory.EnumerateFiles(productionRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("JetActionException.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => FindBareErrorCodeConstructions(sourceRoot, path, bareConstruction))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "已登錄的 wire error code 必須經 JetErrorCodes 常數引用："
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    public static IEnumerable<object[]> RegisteredErrorCodeCases() =>
        RegisteredErrorCodes.Select(item => new object[] { item.FieldName, item.WireValue });

    private static JetActionException? TryTranslate(Exception exception)
    {
        var translatorType = typeof(SqliteProjectDatabase).Assembly.GetType("JET.Infrastructure.LocalEngineErrors");
        Assert.True(translatorType is not null, "JET.Infrastructure.LocalEngineErrors 尚未落地。");

        var method = translatorType.GetMethod(
            "TryTranslate",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            [typeof(Exception)],
            modifiers: null);
        Assert.True(method is not null, "LocalEngineErrors 缺少 public static TryTranslate(Exception)。");

        return (JetActionException?)method.Invoke(null, [exception]);
    }

    private static IEnumerable<string> FindBareErrorCodeConstructions(
        string sourceRoot,
        string sourcePath,
        Regex bareConstruction)
    {
        var source = File.ReadAllText(sourcePath);
        return bareConstruction.Matches(source)
            .Select(match =>
            {
                var lineNumber = 1 + source.AsSpan(0, match.Index).Count('\n');
                return $"{Path.GetRelativePath(sourceRoot, sourcePath)}:{lineNumber} ({match.Groups["code"].Value})";
            });
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !(File.Exists(Path.Combine(directory.FullName, "JET.slnx"))
                && File.Exists(Path.Combine(directory.FullName, "JET", "JET.csproj"))))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到含 JET.slnx 與 JET/JET.csproj 的 source root。");
    }
}
