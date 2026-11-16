using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// accountCategory.save：把目前案件的科目分類整份換成送來的清單。categoryId 沒填或是 draft-N 的列是新增的自訂分類，
/// 由這裡配發正式 ID；填了既有 ID 的列沿用原本的 ID 與是否為內建分類，填了不存在的 ID 就拒絕。
/// 送來的 revision 要和目前存的版本相同；內建分類不能刪，也不能改 semanticRole。
/// 存檔時在同一個交易裡再核對一次版本，並拒絕刪除科目配對還在使用的分類。
/// </summary>
public sealed class AccountCategorySaveHandler(
    ProjectSession session, ILogger? logger = null) : IApplicationActionHandler
{
    public string Action => "accountCategory.save";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var expectedRevision = RequiredInt(payload, "revision");
        if (!payload.TryGetProperty("categories", out var categoriesElement)
            || categoriesElement.ValueKind != JsonValueKind.Array)
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "categories_missing");
        }

        var current = await repositories.AccountCategories.ReadAsync(projectId, cancellationToken);
        if (current.Revision != expectedRevision)
        {
            throw new JetActionException(
                JetErrorCodes.CategoryRevisionConflict,
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
                Reject("category_not_object");
            }

            var requestedId = OptionalString(element, "categoryId");
            string categoryId;
            bool isBuiltIn;
            if (requestedId is null || IsDraftId(requestedId))
            {
                do
                {
                    categoryId = AccountCategoryInvariant.NewCustomId();
                }
                while (!usedIds.Add(categoryId));
                isBuiltIn = false;
                if (requestedId is not null && !draftIds.TryAdd(requestedId, categoryId))
                {
                    // 草稿識別由畫面產生，重複只會是程式錯誤；畫面不露出系統用詞。
                    Reject("category_draft_id_duplicate");
                }
            }
            else if (existing.TryGetValue(requestedId, out var persisted))
            {
                categoryId = persisted.CategoryId;
                isBuiltIn = persisted.IsBuiltIn;
            }
            else
            {
                Reject("category_id_unknown");
                throw new InvalidOperationException("unreachable");
            }

            allocated.Add((element, categoryId, isBuiltIn));
        }

        var replacement = new List<AccountCategory>(allocated.Count);
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
            replacement.Add(new AccountCategory(
                categoryId,
                label,
                RequiredInt(element, "ordinal"),
                RequiredString(element, "semanticRole"),
                isBuiltIn,
                parentId));
        }

        AccountCategoryInvariant.ValidateReplacement(replacement);
        var saved = await repositories.AccountCategories.SaveAsync(
            projectId,
            expectedRevision,
            replacement,
            cancellationToken);
        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(
            projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore, AuditMutationEffects.For(AuditMutation.AccountCategory), logger);
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
            reports = mutationState.Reports,
            reportWarning = mutationState.ReportWarning
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
            Reject("category_string_missing:" + property);
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
            Reject("category_string_expected:" + property);
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
            Reject("category_integer_expected:" + property);
        }
        return result;
    }

    [DoesNotReturn]
    private static void Invalid(string message) =>
        throw new JetActionException(JetErrorCodes.InvalidPayload, message);

    // 只有程式送錯資料才會走到：畫面顯示非預期錯誤那一句，檢查代號記進支援日誌。
    // check 只能是程式寫死的代號；property 是程式寫死的 payload 欄位名稱，不含使用者資料。
    [DoesNotReturn]
    private static void Reject(string check) =>
        throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, check);
}
