using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 篩選結果分頁的游標外面多包一層，做法和傳票預覽相同：外層記這次查詢的識別碼 QueryRevision，內層 Key 是接續下一頁用的游標，
/// SQL 只用內層游標。QueryRevision 由案件、action、情境位置、資料版本、情境版本、排序與搜尋文字算出；
/// 換頁時只要其中一項不同就回 stale_result，新舊頁面不會混在一起。
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
            catch (JsonException exception)
            {
                // 游標不是這裡包的格式時（例如其他查詢的游標或被改動過的字串），一律回 stale_result，請畫面從第一頁重新載入，不猜它的來源。
                throw Stale(exception);
            }
        }
        return new(dataRevision, scenarioRevision, queryRevision, innerRequest);
    }

    internal string? WrapCursor(string? innerCursor) => innerCursor is null ? null
        : PageCursor.Encode(JsonSerializer.Serialize(new CursorEnvelope(QueryRevision, innerCursor)));

    internal static JetActionException Stale(Exception? innerException = null) => new(JetErrorCodes.StaleResult,
        "資料或情境已變更，請從第一頁重新載入，不能接續先前的結果。", innerException: innerException);

    private sealed record CursorEnvelope(string QueryRevision, string Key);
}
