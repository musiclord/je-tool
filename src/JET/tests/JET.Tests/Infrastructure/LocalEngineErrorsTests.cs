using System.Reflection;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 本地引擎錯誤映射的單元測試。oracle 是 manifest Error Codes 章登錄的 wire code，
/// SQLite 以公開 primary error code 建構真實例外；DuckDB 以 1.5.3 實際錯誤訊息型樣建構真實例外。
/// LocalEngineErrors 尚未落地時以反射尋找，讓紅燈能先編譯並明確指出缺少的 production seam。
/// </summary>
public sealed class LocalEngineErrorsTests
{
    private static readonly (string FieldName, string WireValue)[] RegisteredErrorCodes =
    [
        ("DatabaseBusy", "database_busy"),
        ("DatabaseLocked", "database_locked"),
        ("DatabaseStorageFull", "database_storage_full"),
        ("DatabaseCorrupt", "database_corrupt"),
        ("BridgeError", "bridge_error"),
        ("SqlServerNotConfigured", "sql_server_not_configured"),
        ("SqlServerExpressUnsupported", "sql_server_express_unsupported"),
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

    // 等價分割：檔案鎖衝突與交易寫入衝突是 DuckDB 兩種不同可恢復故障面。
    [Theory]
    [InlineData(
        "IO Error: Could not set lock on file \"C:\\projects\\jet.duckdb\": Conflicting lock is held in another process",
        "database_locked")]
    [InlineData("TransactionContext Error: Conflict on update!", "database_busy")]
    [InlineData("TransactionContext Error: Conflict on tuple deletion!", "database_busy")]
    [InlineData("TransactionContext Error: write-write conflict on table gl_target", "database_busy")]
    [InlineData("IO Error: Could not write database file: No space left on device", "database_storage_full")]
    [InlineData("IO Error: Could not write database file: There is not enough space on the disk.", "database_storage_full")]
    [InlineData(
        "Corrupt database file: computed checksum 101 does not match stored checksum 202 in block at location 0",
        "database_corrupt")]
    public void TryTranslate_MappedDuckDbMessagePattern_ReturnsRegisteredCode(string message, string expectedCode)
    {
        var translated = TryTranslate(CreateDuckDbException(message));

        Assert.NotNull(translated);
        Assert.Equal(expectedCode, translated.Code);
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
    public void TryTranslate_UnmappedDuckDbMessage_ReturnsNull()
    {
        Assert.Null(TryTranslate(CreateDuckDbException("Catalog Error: Table with name missing does not exist!")));
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
        var translatorType = typeof(SqlServerEngineErrors).Assembly.GetType("JET.Infrastructure.LocalEngineErrors");
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

    private static Exception CreateDuckDbException(string message)
    {
        var exceptionType = typeof(DuckDBConnection).Assembly.GetType("DuckDB.NET.Data.DuckDBException");
        Assert.True(exceptionType is not null, "DuckDB.NET.Data.DuckDBException 型別不存在。");

        var constructor = exceptionType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(string)],
            modifiers: null);
        Assert.True(constructor is not null, "DuckDBException 缺少 message 建構式。");

        return Assert.IsAssignableFrom<Exception>(constructor.Invoke([message]));
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
