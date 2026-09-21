using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// project-scoped 科目分類 replace-all action。categoryId 缺省代表新增 custom；既有 ID、
/// built-in 身分與 semantic role 由目前 revision 校驗，持久化層再以交易重驗 revision／刪除使用中分類。
/// </summary>
public sealed class AccountTaxonomySaveHandler(
    IAccountTaxonomyStore store,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "accountTaxonomy.save";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        var expectedRevision = RequiredInt(payload, "revision");
        if (!payload.TryGetProperty("categories", out var categoriesElement)
            || categoriesElement.ValueKind != JsonValueKind.Array)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "payload 缺少必填陣列 'categories'。");
        }

        var current = await store.ReadAsync(projectId, cancellationToken);
        if (current.Revision != expectedRevision)
        {
            throw new JetActionException(
                JetErrorCodes.TaxonomyRevisionConflict,
                $"科目分類已由其他作業更新（要求 revision {expectedRevision}，目前為 {current.Revision}），請重新載入後再試。");
        }

        var existing = current.Categories.ToDictionary(item => item.CategoryId, StringComparer.Ordinal);
        var usedIds = new HashSet<string>(existing.Keys, StringComparer.Ordinal);
        var replacement = new List<AccountTaxonomyCategory>();
        foreach (var element in categoriesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Invalid("categories 內每一項都必須是物件。");
            }

            var requestedId = OptionalString(element, "categoryId");
            string categoryId;
            bool isBuiltIn;
            if (requestedId is null)
            {
                do
                {
                    categoryId = $"custom.{Guid.NewGuid():N}";
                }
                while (!usedIds.Add(categoryId));
                isBuiltIn = false;
            }
            else if (existing.TryGetValue(requestedId, out var persisted))
            {
                categoryId = persisted.CategoryId;
                isBuiltIn = persisted.IsBuiltIn;
            }
            else
            {
                Invalid($"categoryId '{requestedId}' 不存在；新增分類請省略 categoryId，由後端產生穩定 ID。");
                throw new InvalidOperationException("unreachable");
            }

            replacement.Add(new AccountTaxonomyCategory(
                categoryId,
                RequiredString(element, "label"),
                RequiredInt(element, "ordinal"),
                RequiredString(element, "semanticRole"),
                isBuiltIn,
                element.TryGetProperty("parentCategoryId", out _)
                    ? OptionalString(element, "parentCategoryId")
                    : existing.GetValueOrDefault(categoryId)?.ParentCategoryId));
        }

        AccountTaxonomyInvariant.ValidateReplacement(replacement);
        var saved = await store.SaveAsync(
            projectId,
            expectedRevision,
            replacement,
            cancellationToken);
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
            }).ToArray()
        };
    }

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
