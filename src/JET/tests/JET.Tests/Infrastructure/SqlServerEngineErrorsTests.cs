using System.Reflection;
using System.Text.Json;
using JET.Application;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 引擎錯誤映射與死鎖重試的單元測試（design §2.3；錯誤碼契約見 manifest Error Codes 章）。
/// oracle：manifest 登錄的引擎錯誤號 → 錯誤碼對照（18456→login、2601/2627→duplicate、1205→deadlock、
/// -2/258→timeout）。SqlException 無公開建構式，以反射工廠製造帶指定錯誤號的真實例外——
/// 測的是真實映射路徑（含 Number 抽取），不是替身。
/// </summary>
public sealed class SqlServerEngineErrorsTests
{
    // 等價分割：每個已映射錯誤號一個代表值＋未映射代表值（208=無效物件名）→ null。
    [Theory]
    [InlineData(18456, JetErrorCodes.SqlServerLoginFailed)]
    [InlineData(2601, JetErrorCodes.DuplicateKey)]
    [InlineData(2627, JetErrorCodes.DuplicateKey)]
    [InlineData(1205, JetErrorCodes.SqlServerDeadlock)]
    [InlineData(-2, JetErrorCodes.SqlServerTimeout)]
    [InlineData(258, JetErrorCodes.SqlServerTimeout)]
    public void TryTranslate_MappedEngineErrorNumber_ReturnsRegisteredCode(int engineNumber, string expectedCode)
    {
        var translated = SqlServerEngineErrors.TryTranslate(CreateSqlException(engineNumber));

        Assert.NotNull(translated);
        Assert.Equal(expectedCode, translated.Code);
    }

    [Fact]
    public void TryTranslate_UnmappedSqlErrorNumber_ReturnsNull()
    {
        // 208 = invalid object name：屬程式缺陷、不該被美化成可重試的引擎狀況 → 維持 bridge_error fallback。
        Assert.Null(SqlServerEngineErrors.TryTranslate(CreateSqlException(208)));
    }

    [Fact]
    public void TryTranslate_NonSqlException_ReturnsNull()
    {
        Assert.Null(SqlServerEngineErrors.TryTranslate(new InvalidOperationException("非引擎例外")));
    }

    [Fact]
    public void TryTranslate_SqlExceptionNestedInInnerChain_TranslatesThroughChain()
    {
        // SqlClient 部分路徑會把 SqlException 包一層再拋；映射點須沿 InnerException 鏈找。
        var wrapped = new InvalidOperationException("外層包裝", CreateSqlException(1205));

        var translated = SqlServerEngineErrors.TryTranslate(wrapped);

        Assert.NotNull(translated);
        Assert.Equal(JetErrorCodes.SqlServerDeadlock, translated.Code);
    }

    [Fact]
    public void TryTranslate_DuplicateKey_PreservesEngineMessage()
    {
        // 引擎原文含索引/資料表與重複鍵值方向（design §2.3），映射不得吞掉。
        var translated = SqlServerEngineErrors.TryTranslate(CreateSqlException(2627));

        Assert.NotNull(translated);
        Assert.Contains("測試用引擎錯誤", translated.Message);
    }

    [Fact]
    public async Task ExecuteWithDeadlockRetry_TwoDeadlocksThenSuccess_ReturnsResultOnThirdAttempt()
    {
        var attempts = 0;

        var result = await SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync(
            _ =>
            {
                attempts++;
                return attempts < 3
                    ? Task.FromException<string>(CreateSqlException(1205))
                    : Task.FromResult("成功");
            },
            CancellationToken.None,
            retryBaseDelay: TimeSpan.Zero);

        Assert.Equal("成功", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteWithDeadlockRetry_PersistentDeadlock_ThrowsAfterMaxAttempts()
    {
        var attempts = 0;

        var thrown = await Assert.ThrowsAsync<SqlException>(() =>
            SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync<string>(
                _ =>
                {
                    attempts++;
                    return Task.FromException<string>(CreateSqlException(1205));
                },
                CancellationToken.None,
                retryBaseDelay: TimeSpan.Zero));

        // 有限次：含首次共 DeadlockMaxAttempts 次；耗盡後原樣拋出，由映射點轉 sql_server_deadlock。
        Assert.Equal(SqlServerEngineErrors.DeadlockMaxAttempts, attempts);
        Assert.Equal(JetErrorCodes.SqlServerDeadlock, SqlServerEngineErrors.TryTranslate(thrown)!.Code);
    }

    [Fact]
    public async Task ExecuteWithDeadlockRetry_NonDeadlockFailure_DoesNotRetry()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<SqlException>(() =>
            SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync<string>(
                _ =>
                {
                    attempts++;
                    return Task.FromException<string>(CreateSqlException(2627)); // 唯一鍵衝突：重試無意義
                },
                CancellationToken.None,
                retryBaseDelay: TimeSpan.Zero));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteWithDeadlockRetry_SucceedsImmediately_RunsExactlyOnce()
    {
        var attempts = 0;

        var result = await SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(42);
            },
            CancellationToken.None,
            retryBaseDelay: TimeSpan.Zero);

        Assert.Equal(42, result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Dispatcher_HandlerThrowsSqlException_SurfacesMappedCodeInsteadOfBridgeError()
    {
        // 單一映射點的接線驗證：handler 拋原生 SqlException，dispatcher 出口轉為 JetActionException，
        // bridge 的 ToErrorDto 才能把明確錯誤碼放上 wire（而非裸 bridge_error）。
        var dispatcher = new ActionDispatcher(
            [new ThrowingHandler("__test.sqlError", CreateSqlException(18456))],
            NullLogger<ActionDispatcher>.Instance,
            new ProjectSession(),
            SqlServerEngineErrors.TryTranslate);

        using var payload = JsonDocument.Parse("{}");
        var thrown = await Assert.ThrowsAsync<JetActionException>(() =>
            dispatcher.DispatchAsync("__test.sqlError", payload.RootElement, CancellationToken.None));

        Assert.Equal(JetErrorCodes.SqlServerLoginFailed, thrown.Code);
        Assert.Equal(JetErrorCodes.SqlServerLoginFailed, JetWebMessageBridge.ToErrorDto(thrown).Code);
    }

    [Fact]
    public async Task Dispatcher_HandlerThrowsJetActionException_PassesThroughUntranslated()
    {
        // 業務錯誤不進映射點：既有錯誤碼原樣上 wire（負向情境：映射不得攔截業務錯誤）。
        var business = new JetActionException(JetErrorCodes.NotAuthorized, "無存取權");
        var dispatcher = new ActionDispatcher(
            [new ThrowingHandler("__test.business", business)],
            NullLogger<ActionDispatcher>.Instance,
            new ProjectSession(),
            SqlServerEngineErrors.TryTranslate);

        using var payload = JsonDocument.Parse("{}");
        var thrown = await Assert.ThrowsAsync<JetActionException>(() =>
            dispatcher.DispatchAsync("__test.business", payload.RootElement, CancellationToken.None));

        Assert.Same(business, thrown);
    }

    private sealed class ThrowingHandler(string action, Exception exception) : IApplicationActionHandler
    {
        public string Action => action;

        public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken) =>
            Task.FromException<object?>(exception);
    }

    /// <summary>
    /// 反射工廠：製造帶指定引擎錯誤號的真實 <see cref="SqlException"/>（無公開建構式）。
    /// 依 Microsoft.Data.SqlClient 內部形狀（SqlError 內部建構式＋SqlErrorCollection.Add＋
    /// SqlException.CreateException）；套件升級若改內部形狀，本工廠會 fail-loud，屬可接受的維護點。
    /// </summary>
    private static SqlException CreateSqlException(int engineNumber)
    {
        var errorCtor = typeof(SqlError)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = errorCtor.GetParameters();
        var args = parameters.Select(p =>
            p.ParameterType == typeof(int) ? 0
            : p.ParameterType == typeof(uint) ? 0u
            : p.ParameterType == typeof(byte) ? (byte)0
            : p.ParameterType == typeof(string) ? (object)"測試用引擎錯誤"
            : null).ToArray();
        // 第一個 int 參數即 infoNumber（引擎錯誤號）。
        args[Array.FindIndex(parameters, p => p.ParameterType == typeof(int))] = engineNumber;
        var error = errorCtor.Invoke(args);

        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(collection, [error]);

        var create = typeof(SqlException).GetMethod(
            "CreateException",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(SqlErrorCollection), typeof(string)],
            modifiers: null)!;
        return (SqlException)create.Invoke(null, [collection, "16.0.0"])!;
    }
}
