using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SQL Server 可用性探測的誠實性守衛（2026-07-03 複審測試債）：連線字串「有值但格式不可解析」時，
/// 探測必須回 null（→ <see cref="SqlServerFactAttribute"/> 以具名理由略過），
/// 不得拋例外——探測跑在測試探索期（<see cref="SqlServerAvailability"/> 的 Lazy），
/// 拋出會讓整批 SQL Server 測試在探索期炸掉而非誠實略過。
/// </summary>
public sealed class SqlServerConnectionProbeTests
{
    // 等價分割：可解析但連不上（既有路徑，SqlException → null）之外的「不可解析」等價類。
    [Theory]
    [InlineData("這不是連線字串")]                       // 無鍵值形狀 → ArgumentException
    [InlineData("Server=localhost;Encrypt=也許")]        // 布林鍵塞垃圾 → 格式錯
    [InlineData(";;;===")]                               // 純分隔符垃圾
    public async Task ProbeConnectionString_UnparseableValue_ReturnsNullInsteadOfThrowing(string malformed)
    {
        Assert.Null(await TempSqlServerProject.ProbeConnectionStringAsync(malformed, CancellationToken.None));
    }
}
