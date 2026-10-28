using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// INF 報告主表與底稿第二步共用的「借貸代號」與「分錄來源」（使用者 2026-10-28 裁定往下填補後改讀整理後的值）。
/// 分錄來源讀 GL 整理後的值，和同一列的傳票號碼及第 4-1 頁一致。借貸代號照 PBC-GL 原樣寫出；
/// 原始列空白而借貸別有往下填補時，依整理後的借貸方向寫已確認配對的借方或貸方代碼。
/// 借貸別模式的金額正負號由代碼決定，所以整理後的方向就是填進去的代碼那一側；金額為 0 的列一律算借方，
/// 看不出填的是哪一側，以及那一側沒有指定代碼時，都留白，不寫可能不對的代碼。
/// 「可靠性樣本_所有欄位」不經過這裡，仍是 PBC-GL 原樣。
/// </summary>
internal sealed class InfSampleDisplayValues
{
    private readonly string? directionColumn;
    private readonly string? debitCode;
    private readonly string? creditCode;
    private readonly bool directionFilledDown;

    public InfSampleDisplayValues(CommittedMapping? glMapping)
    {
        if (glMapping is null)
        {
            return;
        }

        glMapping.Mapping.TryGetValue(GlMappingKeys.DcField, out directionColumn);
        glMapping.Mapping.TryGetValue(GlMappingKeys.DcDebitCode, out debitCode);
        glMapping.Mapping.TryGetValue(GlMappingKeys.DcCreditCode, out creditCode);
        directionFilledDown = glMapping.GlOptions?.FillDownFields.Contains(GlMappingKeys.DcField) == true;
    }

    /// <summary>回傳這一列的借貸代號與分錄來源；raw 是同一列的 PBC-GL 原樣。</summary>
    public (string? Direction, string? SourceModule) For(JsonElement raw, InfSampleRow sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return (Direction(raw, sample), sample.SourceModule);
    }

    private string? Direction(JsonElement raw, InfSampleRow sample)
    {
        var original = ReadRawValue(raw, directionColumn);
        if (!string.IsNullOrWhiteSpace(original) || !directionFilledDown
            || (sample.DebitScaled == 0 && sample.CreditScaled == 0))
        {
            return original;
        }

        var code = sample.DrCr switch
        {
            "DEBIT" => debitCode,
            "CREDIT" => creditCode,
            _ => null
        };
        return string.IsNullOrWhiteSpace(code) ? original : code;
    }

    private static string? ReadRawValue(JsonElement row, string? sourceColumn)
    {
        if (string.IsNullOrWhiteSpace(sourceColumn)
            || !row.TryGetProperty(sourceColumn, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }
}
