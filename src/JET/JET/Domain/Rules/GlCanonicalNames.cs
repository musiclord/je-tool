namespace JET.Domain;

/// <summary>
/// logical key → 正準中文欄名(GL 側 *_JE / *_JE_S、TB 側 *_TB)。
///
/// 為什麼存在:匯出底稿的「自動化工具-檔案欄位資訊」表(Field Mapping Info)要把每個已配對欄位
/// 以事務所慣用的正準中文名列出，供底稿顯示與來源追溯。這份清單刻意只包含方法學樣本
/// 已定義的可見名稱，不能反向重建完整 mapping；完整 round-trip 另由版本化 logical-key metadata 承載。
///
/// 與 <see cref="GlFieldWhitelist"/> 的關係:白名單是 logical key → 實體欄(SQL 識別字防線);
/// 本表是 logical key → 對外顯示/匯入比對用的正準中文名。兩個 public view 用途不同，
/// 但都由 internal <see cref="JetFieldCatalog"/> 投影，不能用其中一份反向重建另一份。
/// 涵蓋範圍:只列樣本 Field Mapping Info 有對應正準名的鍵;樣本未給正準名者
/// (docDate/voucherDate/jeSource 等)刻意不列——不臆造名稱(no silent assumption)。
/// </summary>
public static class GlCanonicalNames
{
    /// <summary>GL 欄位的 logical key → 正準中文名(對照樣本 Field Mapping Info 配對後欄名)。</summary>
    public static IReadOnlyDictionary<string, string> Gl { get; } = JetFieldCatalog.GlCanonicalNames;

    /// <summary>TB 欄位的 logical key → 正準中文名(對照樣本 Field Mapping Info 配對後欄名)。</summary>
    public static IReadOnlyDictionary<string, string> Tb { get; } = JetFieldCatalog.TbCanonicalNames;
}
