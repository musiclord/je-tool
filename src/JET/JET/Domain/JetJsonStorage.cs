using System.Text.Encodings.Web;
using System.Text.Json;

namespace JET.Domain;

/// <summary>
/// 儲存層（DB TEXT / project.json）專用的 JSON 設定：保留非 ASCII 原文，
/// 供人工檢視與 SQL JSON 函式（SQLite json_extract / SQL Server OPENJSON）直接讀取。
/// Bridge wire 回應仍用各自的預設設定，不受影響。
/// 置於 Domain（BCL-only、無 I/O）供 Application 與 Infrastructure 共用，維持依賴方向。
/// </summary>
public static class JetJsonStorage
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions IndentedOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };
}
