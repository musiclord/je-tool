using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JET.Domain;

/// <summary>
/// 診斷日誌的 NDJSON 序列化:單一事實來源,供 dev.log.exportFile 的 ring buffer 退路(Application)與診斷日誌檔案 sink
/// (Infrastructure)共用,確保兩條路徑格式一致(Web 規約 camelCase、null 欄位省略)。
/// </summary>
public static class DiagnosticNdjson
{
    private static readonly JsonSerializerOptions Options = DiagnosticNdjsonOptions.Create(encoder: null);

    /// <summary>單筆序列化為一行 JSON(不含換行;NDJSON 由呼叫端以 \n 串接)。</summary>
    public static string SerializeLine(DiagnosticLogEntry entry) => JsonSerializer.Serialize(entry, Options);
}

internal static class DiagnosticNdjsonOptions
{
    internal static JsonSerializerOptions Create(JavaScriptEncoder? encoder) => new(JsonSerializerDefaults.Web)
    {
        Encoder = encoder,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
