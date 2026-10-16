using JET.Tests.Application;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

[assembly: TestCollectionOrderer(typeof(JET.Tests.LongRunningFirstCollectionOrderer))]

namespace JET.Tests;

/// <summary>
/// 讓耗時最長的測試類別最先開始，其餘類別維持 xUnit 預設順序（依 collection 的 UniqueID 排序）。
/// 平行執行時一個類別占一個名額直到跑完，再照順序放行下一個；排在後面的長測試會在其他類別跑完後獨自拖長整段測試。
/// 使用者 2026-10-15 選「只把它排第一」，所以名單只有 <see cref="ActionShapeContractTests"/>。
/// </summary>
public sealed class LongRunningFirstCollectionOrderer : ITestCollectionOrderer
{
    private static readonly HashSet<string> StartFirst = new(StringComparer.Ordinal)
    {
        CollectionAttribute.GetCollectionNameForType(typeof(ActionShapeContractTests)),
    };

    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : ITestCollection =>
        DefaultTestCollectionOrderer.Instance.OrderTestCollections(testCollections)
            .OrderBy(collection => StartFirst.Contains(collection.TestCollectionDisplayName) ? 0 : 1)
            .ToList();
}
