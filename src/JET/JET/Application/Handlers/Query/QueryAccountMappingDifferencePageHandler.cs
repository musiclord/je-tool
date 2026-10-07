using System.Text.Json;
using JET.Domain;

namespace JET.Application;

public sealed class QueryAccountMappingDifferencePageHandler(ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.accountMappingDifferencePage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var kind = PayloadReader.GetRequiredString(payload, "kind");
        if (!AccountMappingDifferenceKinds.IsKnown(kind))
            throw new JetActionException(JetErrorCodes.InvalidPayload, "科目清單種類無效，請選擇本案沒有的科目或配對檔未列的科目。");
        var cursor = PayloadReader.GetOptionalString(payload, "cursor");
        if (PageCursor.IsMalformed(cursor))
            throw new JetActionException(JetErrorCodes.InvalidPayload, "游標格式無效，請重新列出科目。");
        var pageSize = PayloadReader.GetOptionalInt(payload, "pageSize") ?? PageRequest.DefaultPageSize;
        var page = await repositories.AccountMappingDifferences.GetPageAsync(projectId, kind, new PageRequest(cursor, pageSize), cancellationToken);
        return new
        {
            kind,
            rows = page.Rows.Select(row => new { accountCode = row.AccountCode, accountName = row.AccountName }).ToArray(),
            nextCursor = page.NextCursor,
            totalCount = page.TotalCount
        };
    }
}
