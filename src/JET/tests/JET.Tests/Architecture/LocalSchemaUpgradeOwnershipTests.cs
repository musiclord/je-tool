using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// SQLite 與 DuckDB 的結構升版順序只寫一份（LocalSchemaUpgrades）；兩個資料庫類別只提供各自的升版步驟，
/// 不各自寫一遍「第 6 版升第 7 版、第 7 版升第 8 版」的梯子（使用者 2026-10-22 裁定保留升版，共用一份順序）。
/// </summary>
public sealed class LocalSchemaUpgradeOwnershipTests
{
    [Fact]
    public void LocalDatabases_ShareOneUpgradeOrder()
    {
        Assert.NotNull(ProductSources.ProductType("JET.Infrastructure.LocalSchemaUpgrades"));

        foreach (var source in new[]
                 {
                     ProductSources.Product("Infrastructure", "Persistence", "Sqlite", "SqliteProjectDatabase.cs"),
                     ProductSources.Product("Infrastructure", "Persistence", "DuckDb", "DuckDbProjectDatabase.cs")
                 })
        {
            Assert.Contains("LocalSchemaUpgrades.", source, StringComparison.Ordinal);
            Assert.DoesNotContain("if (version == \"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("if (parsedVersion == ", source, StringComparison.Ordinal);
        }
    }
}
