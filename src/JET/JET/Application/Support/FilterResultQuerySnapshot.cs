using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 沿用傳票預覽的 QueryRevision/Key 游標外包方式。內層 keyset 游標與 SQL 不變，
/// 外層只把頁面綁在同一案件、資料、情境及查詢條件，避免新舊頁面混用。
/// </summary>
internal sealed record FilterResultQuerySnapshot(
    string DataRevision,
    string ScenarioRevision,
    string QueryRevision,
    PageRequest Request)
{
    internal static FilterResultQuerySnapshot Bind(string projectId, string action, int? scenarioPosition,
        string dataRevision, string scenarioRevision, PageRequest request)
    {
        var queryRevision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new
            {
                projectId, action, scenarioPosition, dataRevision, scenarioRevision,
                request.Sort, request.Search
            }))));
        var innerRequest = request;
        if (request.Cursor is not null)
        {
            if (request.Cursor.Length > 16_384 || !PageCursor.TryDecode(request.Cursor, out var decoded))
            {
                throw Stale();
            }
            try
            {
                var cursor = JsonSerializer.Deserialize<CursorEnvelope>(decoded);
                if (cursor is null || cursor.QueryRevision != queryRevision
                    || string.IsNullOrWhiteSpace(cursor.Key) || PageCursor.IsMalformed(cursor.Key))
                {
                    throw Stale();
                }
                innerRequest = request with { Cursor = cursor.Key };
            }
            catch (JsonException)
            {
                // 不是這個外包格式的 token（例如其他查詢的游標或被改動的字串）一律回第一頁，不猜測其來源。
                throw Stale();
            }
        }
        return new(dataRevision, scenarioRevision, queryRevision, innerRequest);
    }

    internal string? WrapCursor(string? innerCursor) => innerCursor is null ? null
        : PageCursor.Encode(JsonSerializer.Serialize(new CursorEnvelope(QueryRevision, innerCursor)));

    internal static JetActionException Stale() => new(JetErrorCodes.StaleResult,
        "資料或情境已變更，請從第一頁重新載入，不能接續先前的結果。");

    private sealed record CursorEnvelope(string QueryRevision, string Key);
}
