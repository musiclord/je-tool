using System.Text.RegularExpressions;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 清除下游結果的寫入都經過 <c>ResultReset.ClearWithinAsync</c>。這裡列出每一個呼叫它的檔案與傳入的上游修改種類，
/// 新增、移除或改了種類時就會失敗，提醒開發者一併確認：呼叫它的 action 列在
/// <c>tests/JET.Tests/Fixtures/upstream-mutation-invalidation.json</c>，回應帶 <c>invalidatedResults</c>，
/// 畫面收到回應後交給 <c>Store.applyMutationEffects</c>。
/// </summary>
public sealed class ResultResetCallSiteTests
{
    private static readonly Regex Call = new(@"ResultReset\.ClearWithinAsync\((?<args>[^;]*?)\);", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex Mutation = new(@"AuditMutation\.(?<name>\w+)", RegexOptions.CultureInvariant);
    private static readonly Regex AnyReference = new(@"\bResultReset\.ClearWithinAsync\b|\busing\s+static\s+[\w.]*\bResultReset\s*;", RegexOptions.CultureInvariant);

    // 每一列是一個呼叫：檔案相對於產品專案資料夾，後面是這個呼叫可能傳入的上游修改種類。
    private static readonly string[] KnownCalls =
    [
        "Infrastructure/Persistence/AccountMappingEditorRepository.cs: AccountMapping",
        "Infrastructure/Persistence/Local/LocalAccountCategoryStore.cs: AccountCategory",
        "Infrastructure/Persistence/Local/LocalAccountMappingRepository.cs: AccountMapping",
        "Infrastructure/Persistence/Local/LocalAuthorizedPreparerRepository.cs: AuthorizedPreparer",
        "Infrastructure/Persistence/Local/LocalAuthorizedPreparerRepository.cs: AuthorizedPreparer",
        "Infrastructure/Persistence/Local/LocalCalendarStore.cs: Calendar",
        "Infrastructure/Persistence/Local/LocalCalendarStore.cs: Calendar",
        "Infrastructure/Persistence/Local/LocalGlRepository.cs: GlProjection",
        "Infrastructure/Persistence/Local/LocalImportRepository.cs: GlImport、TbImport",
        "Infrastructure/Persistence/Local/LocalImportRepository.cs: GlImport、TbImport",
        "Infrastructure/Persistence/Local/LocalResultStaleStateStore.cs: PreparationDate",
        "Infrastructure/Persistence/Local/LocalTbRepository.cs: TbProjection",
    ];

    [Fact]
    public void EveryResultResetCall_IsAKnownUpstreamWrite()
    {
        var product = ProductSources.ProductRoot;
        var found = new List<string>();
        var unreadable = new List<string>();
        foreach (var file in Directory
                     .EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
                     .Where(file => !Path.GetRelativePath(product, file)
                         .Split(Path.DirectorySeparatorChar)
                         .Any(part => part is "bin" or "obj"))
                     .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(product, file).Replace('\\', '/');
            if (relative == "Infrastructure/Sql/ResultReset.cs")
            {
                continue;
            }

            var source = File.ReadAllText(file);
            var calls = Call.Matches(source);
            foreach (Match call in calls)
            {
                var mutations = Mutation.Matches(call.Groups["args"].Value).Select(match => match.Groups["name"].Value);
                found.Add(relative + ": " + string.Join("、", mutations));
            }

            // 用 using static 省略類別名稱，或把方法當成委派傳出去時，上面的樣式認不出傳入的種類，這裡另外列出。
            if (AnyReference.Matches(source).Count != calls.Count)
            {
                unreadable.Add(relative);
            }
        }

        Assert.True(unreadable.Count == 0,
            "這些檔案用到 ResultReset.ClearWithinAsync，卻沒有寫成直接呼叫的 ResultReset.ClearWithinAsync(…)，認不出傳入的上游修改種類："
            + string.Join("、", unreadable) + "。請照其他呼叫的寫法。");
        var added = Without(found, KnownCalls);
        var missing = Without(KnownCalls, found);
        Assert.True(added.Count == 0 && missing.Count == 0,
            "清除下游結果的寫入和這裡列的不一樣。這裡沒有列的呼叫：" + Lines(added) + "；這裡列了、現在找不到的呼叫：" + Lines(missing) + "。"
            + "新增或改動會清除下游結果的寫入時，呼叫它的 action 要列進 tests/JET.Tests/Fixtures/upstream-mutation-invalidation.json，"
            + "回應要帶 invalidatedResults，畫面收到回應後要交給 Store.applyMutationEffects；"
            + "確認這三處之後，再更新這裡的清單。");
    }

    // 同一個檔案可能有兩個相同的呼叫，所以逐筆扣掉，不當成集合比較。
    private static List<string> Without(IEnumerable<string> source, IEnumerable<string> remove)
    {
        var left = source.Order(StringComparer.Ordinal).ToList();
        foreach (var item in remove)
        {
            left.Remove(item);
        }

        return left;
    }

    private static string Lines(IReadOnlyCollection<string> items) => items.Count == 0 ? "沒有" : string.Join("、", items);
}
