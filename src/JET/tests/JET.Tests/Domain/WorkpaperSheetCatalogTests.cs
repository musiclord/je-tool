using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class WorkpaperSheetCatalogTests
{
    [Fact]
    public void All_PreservesEstablishedWorkbookOrder()
    {
        string[] expected =
        [
            "資料預先整理之說明",
            "JE WorkingPaper說明",
            "step1 完整性測試",
            "step1-1 借貸不平測試",
            "step1-2 分錄編製人員說明",
            "step1-3 完整性測試之差異說明",
            "step2 可靠性測試",
            "step3 高風險條件彙總",
            "step4 符合高風險條件傳票",
            "step4-1 符合高風險條件傳票明細",
            "step5 財務報表關帳後調整之分錄",
            "自動化工具-檔案欄位資訊",
            "自動化工具-假期假日資訊",
            "自動化工具-科目配對資訊"
        ];

        Assert.Equal(expected, WorkpaperSheetCatalog.All);
        Assert.Null(typeof(WorkpaperSheetCatalog).GetField(
            "Step131",
            System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static));
    }

    [Fact]
    public void All_ContainsNoDuplicateNames()
    {
        Assert.Equal(
            WorkpaperSheetCatalog.All.Count,
            WorkpaperSheetCatalog.All.Distinct(StringComparer.Ordinal).Count());
    }
}
