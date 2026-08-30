using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// dev.log.export：診斷日誌（第三層、跨專案、dev-only）ring buffer 完整匯出為 NDJSON（每行一筆完整 JSON 物件）。
/// 供開發測試把完整系統真相（action 生命週期 / SQL+參數 / transaction / exception / milestone）交給 AI 驗證。
/// 不需 active project（診斷日誌跨專案）。僅 Debug 組建註冊（同 dev.db.*）;Release 不註冊 → unknown action。
/// </summary>
public sealed class DevLogExportHandler(IDiagnosticLogStore diagnosticLog) : IApplicationActionHandler
{
    public string Action => "dev.log.export";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        // 序列化與診斷日誌檔案 sink 共用 DiagnosticNdjson,確保匯出與檔案兩條路徑格式一致。
        var ndjson = string.Join('\n', diagnosticLog.Snapshot().Select(DiagnosticNdjson.SerializeLine));
        return Task.FromResult<object?>(new { ndjson });
    }
}
