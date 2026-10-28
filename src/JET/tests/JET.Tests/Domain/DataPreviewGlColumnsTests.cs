using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 資料預覽 GL 兩頁的欄位計畫涵蓋欄位目錄的每個 GL 欄位（預篩選收斂計畫階段五，使用者 2026-10-22 裁定預覽要看得到
/// 已配對的欄位）。目錄以後多了欄位，這裡會失敗，提醒決定它在預覽列在哪裡，不會默默漏掉。
/// 第一次失敗：Focused 收據 20261023-112355120-fddd838677804bc6918049b35c7ab70f，建置失敗。
/// </summary>
public sealed class DataPreviewGlColumnsTests
{
    [Fact]
    public void EveryCatalogField_IsListedExactlyOnceWhenAllAreMapped()
    {
        var mapping = JetFieldCatalog.GlMappingSlots
            .Where(slot => !slot.IsLiteral)
            .ToDictionary(slot => slot.Key, slot => "來源-" + slot.Key, StringComparer.Ordinal);

        var columns = DataPreviewGlColumns.For(DataPreviewDataset.GlExcluded, mapping, GlMappingOptions.NormalizeLegacy(mapping));

        var entryStorage = columns
            .Where(column => column.Source != DataPreviewGlValueSource.Rde)
            .Select(column => column.Storage)
            .ToArray();
        Assert.Equal(entryStorage.Length, entryStorage.Distinct(StringComparer.Ordinal).Count());
        Assert.All(JetFieldCatalog.GlFields, field => Assert.Contains(field.SemanticSqlTarget, entryStorage));
        // 目錄之外只多兩欄：由金額配對方式決定的借貸別，以及 GL(未納入測試) 的未納入原因。
        Assert.Equal(
            ["dr_cr", "exclusion_reason"],
            entryStorage.Except(JetFieldCatalog.GlFields.Select(field => field.SemanticSqlTarget), StringComparer.Ordinal));
    }
}
