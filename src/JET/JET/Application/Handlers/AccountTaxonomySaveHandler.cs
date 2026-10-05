using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// project-scoped 科目分類 replace-all action。categoryId 缺省或為 draft-N 代表新增 custom；既有 ID、
/// built-in 身分與 semantic role 由目前 revision 校驗，持久化層再以交易重驗 revision／刪除使用中分類。
/// </summary>
public sealed class AccountTaxonomySaveHandler(
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "accountTaxonomy.save";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var expectedRevision = RequiredInt(payload, "revision");
        if (!payload.TryGetProperty("categories", out var categoriesElement)
            || categoriesElement.ValueKind != JsonValueKind.Array)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "payload 缺少必填陣列 'categories'。");
        }

        var current = await repositories.AccountTaxonomy.ReadAsync(projectId, cancellationToken);
        if (current.Revision != expectedRevision)
        {
            throw new JetActionException(
                JetErrorCodes.TaxonomyRevisionConflict,
                "科目分類剛被其他操作更新，請重新開啟分類設定再儲存一次。");
        }

        var existing = current.Categories.ToDictionary(item => item.CategoryId, StringComparer.Ordinal);
        var usedIds = new HashSet<string>(existing.Keys, StringComparer.Ordinal);
        var draftIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var allocated = new List<(JsonElement Element, string CategoryId, bool IsBuiltIn)>();
        // 先配置本次所有新分類的正式 ID，讓前面的子列也可以引用後面的上層列。
        foreach (var element in categoriesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Invalid("categories 內每一項都必須是物件。");
            }

            var requestedId = OptionalString(element, "categoryId");
            string categoryId;
            bool isBuiltIn;
            if (requestedId is null || IsDraftId(requestedId))
            {
                do
                {
                    categoryId = $"custom.{Guid.NewGuid():N}";
                }
                while (!usedIds.Add(categoryId));
                isBuiltIn = false;
                if (requestedId is not null && !draftIds.TryAdd(requestedId, categoryId))
                {
                    Invalid("本次分類草稿的暫存識別重複，請重新開啟分類設定後再儲存。");
                }
            }
            else if (existing.TryGetValue(requestedId, out var persisted))
            {
                categoryId = persisted.CategoryId;
                isBuiltIn = persisted.IsBuiltIn;
            }
            else
            {
                Invalid("分類識別不存在。新增分類請省略 categoryId，或使用同一份草稿中唯一的 draft-N 暫存識別。");
                throw new InvalidOperationException("unreachable");
            }

            allocated.Add((element, categoryId, isBuiltIn));
        }

        var replacement = new List<AccountTaxonomyCategory>(allocated.Count);
        foreach (var (element, categoryId, isBuiltIn) in allocated)
        {
            var label = RequiredString(element, "label");
            var parentId = element.TryGetProperty("parentCategoryId", out _)
                ? OptionalString(element, "parentCategoryId")
                : existing.GetValueOrDefault(categoryId)?.ParentCategoryId;
            if (parentId is not null && draftIds.TryGetValue(parentId, out var allocatedParent))
            {
                parentId = allocatedParent;
            }
            else if (parentId is not null && !existing.ContainsKey(parentId))
            {
                Invalid($"分類「{label}」的上層不存在於目前分類或本次草稿，請重新選擇上層分類。");
            }
            replacement.Add(new AccountTaxonomyCategory(
                categoryId,
                label,
                RequiredInt(element, "ordinal"),
                RequiredString(element, "semanticRole"),
                isBuiltIn,
                parentId));
        }

        AccountTaxonomyInvariant.ValidateReplacement(replacement);
        var saved = await repositories.AccountTaxonomy.SaveAsync(
            projectId,
            expectedRevision,
            replacement,
            cancellationToken);
        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(
            projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore, AuditMutationEffects.For(AuditMutation.AccountTaxonomy));
        return new
        {
            revision = saved.Revision,
            categories = saved.Categories.Select(item => new
            {
                categoryId = item.CategoryId,
                label = item.Label,
                ordinal = item.Ordinal,
                semanticRole = item.SemanticRole,
                isBuiltIn = item.IsBuiltIn,
                parentCategoryId = item.ParentCategoryId
            }).ToArray(),
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reportArtifacts = mutationState.ReportArtifacts,
            reportArtifactWarning = mutationState.ReportArtifactWarning
        };
    }

    private static bool IsDraftId(string value) =>
        value.Length is >= 7 and <= 64
        && value.StartsWith("draft-", StringComparison.Ordinal)
        && value[6] is >= '1' and <= '9'
        && value.AsSpan(6).IndexOfAnyExcept("0123456789".AsSpan()) < 0;

    private static string RequiredString(JsonElement element, string property)
    {
        var value = OptionalString(element, property);
        if (value is null)
        {
            Invalid($"payload 缺少必填字串 '{property}'。");
        }
        return value!;
    }

    private static string? OptionalString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            Invalid($"payload 欄位 '{property}' 必須是字串。");
        }
        var normalized = value.GetString()?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static int RequiredInt(JsonElement element, string property)
    {
        var result = 0;
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out result))
        {
            Invalid($"payload 欄位 '{property}' 必須是整數。");
        }
        return result;
    }

    [DoesNotReturn]
    private static void Invalid(string message) =>
        throw new JetActionException(JetErrorCodes.InvalidPayload, message);
}
