using System.Runtime.CompilerServices;
using JET.Tests.TestInfrastructure;
using Xunit;
using Xunit.v3;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SQL Server 可用性的單一檢查點。每輪只檢查一次，結果會保留到測試程序結束。
/// 連線只取自 <c>JET_SQLSERVER_CONNECTION</c>，並沿用
/// <see cref="TempSqlServerProject.ProbeConnectionStringAsync"/> 的判定方式。
/// </summary>
internal static class SqlServerAvailability
{
    // 探索階段第一次讀取時探一次(開一條 master 連線),之後讀快取值——整個測試回合只一次。
    private static readonly Lazy<string?> ConnectionString =
        new(() => TempSqlServerProject.ProbeConnectionStringAsync().GetAwaiter().GetResult());

    public static bool IsAvailable => ConnectionString.Value is not null;

    public const string SkipReason =
        "找不到可用的 SQL Server 2022 以上版本。連線只取自 JET_SQLSERVER_CONNECTION；未設定、連不上、格式錯誤、Express 或舊版都會略過 SQL Server 測試。";
}

/// <summary>
/// 條件式 Fact:偵測不到 SQL Server 2022+（非 Express）時,讓 xUnit 把測試標成「略過(skipped)」,
/// 而不是 early-return 當作通過(誠實顯示「沒測到」)。
///
/// v3 dynamic skip 把 capability probe 延到 execution；Inner／filtered discovery 不連外部資源。
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class SqlServerFactAttribute : FactAttribute, ITraitAttribute
{
    public SqlServerFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = SqlServerAvailability.SkipReason;
        SkipType = typeof(SqlServerAvailability);
        SkipUnless = nameof(SqlServerAvailability.IsAvailable);
    }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        TestProfileTraits.Provider;
}

/// <summary>SQL Server 閘控的 Theory 版(同 <see cref="SqlServerFactAttribute"/> 機制)。</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class SqlServerTheoryAttribute : TheoryAttribute, ITraitAttribute
{
    public SqlServerTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = SqlServerAvailability.SkipReason;
        SkipType = typeof(SqlServerAvailability);
        SkipUnless = nameof(SqlServerAvailability.IsAvailable);
    }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        TestProfileTraits.Provider;
}
