namespace JET.Domain;

/// <summary>
/// GL 投影的退化母體守門（guide §2.1 金額模式的資料品質防線）。
///
/// 背景：GL 金額投影成功（無列級錯誤）、母體也非空，但整個母體的借方總額與貸方總額**皆為 0**——
/// 這幾乎一定是金額欄配對錯誤。最常見於把借/貸金額配到了「傳票總額」欄：同一張傳票每一列都填一樣的
/// 總借＝總貸，<see cref="GlAmountMode.DualAmount"/> 逐列算「借 − 貸」便恆為 0；或把金額配到了空欄。
/// 這種母體借貸淨額全 0，無法做完整性測試（逐科目 GL 加總全 0、與 TB 全不符）與後續所有規則，
/// 而且症狀只會在兩步之後以「完整性全科目不符」這種看不出原因的形式浮現。
/// 守門在 <c>mapping.commit.gl</c> 投影提交前攔下、整批 rollback，並回可行動的錯誤訊息。
/// </summary>
public static class GlProjectionGuard
{
    /// <summary>
    /// 母體非空，但借方總額與貸方總額皆為 0 → 退化（金額欄誤配）。
    /// 空母體（projectedRowCount == 0）不屬此守門範圍，由既有路徑處理。
    /// </summary>
    public static bool IsDegenerateAmountPopulation(
        long projectedRowCount,
        long totalDebitScaled,
        long totalCreditScaled) =>
        projectedRowCount > 0 && totalDebitScaled == 0 && totalCreditScaled == 0;

    public const string DegenerateAmountMessage =
        "GL 金額投影後整個母體借貸總額皆為 0，無法用於完整性測試與後續規則。" +
        "最常見原因：借方／貸方金額欄配對到了「傳票總額」欄（同一張傳票每列借貸相等，逐列淨額恆為 0），或配對到空欄。" +
        "請回「欄位配對」改配對列層的借方／貸方金額欄後重新提交。";
}
