using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 2026-10-03 O1、O2：欄位配對無法轉換時，同一欄同一種問題只寫一次處理方式，再依值列出列號；空白值寫成「空白」。
/// 預期文字都是手寫的固定答案，不從被測程式算出。
/// </summary>
public sealed class ProjectionFailureTextTests
{
    private const string Unlisted = "未歸類為人工或自動。請回到欄位配對把這些值歸類";
    private const string Blank = "是空白。請回到欄位配對，在「來源空白時」選擇處理方式";

    [Fact]
    public void SameFieldAndProblem_WritesInstructionOnceAndListsRowsPerValue()
    {
        var error = ProjectionFailureText.Create(new ProjectionResult(0,
        [
            new RowProjectionError(2, "人工註記", "調整", Unlisted),
            new RowProjectionError(3, "人工註記", "調整", Unlisted),
            new RowProjectionError(139, "人工註記", "補登", Unlisted),
            new RowProjectionError(327, "人工註記", "", Blank),
            new RowProjectionError(568, "人工註記", " 調整 ", Unlisted),
            new RowProjectionError(385, "人工註記", "  ", Blank)
        ]));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.Collection(
            error.Details!,
            first => Assert.Equal(
                "欄位「人工註記」有 4 列未歸類為人工或自動：「調整」在第 2、3、568 列；「補登」在第 139 列。請回到欄位配對把這些值歸類。",
                first.Message),
            second => Assert.Equal(
                "欄位「人工註記」有 2 列是空白：第 327、385 列。請回到欄位配對，在「來源空白時」選擇處理方式。",
                second.Message));
        Assert.All(error.Details!, detail => Assert.Equal("人工註記", detail.SourceColumn));
        Assert.Equal(
            "6 列無法轉換，系統沒有儲存這次配對結果。" + error.Details![0].Message + error.Details[1].Message,
            error.Message);
        Assert.DoesNotContain("「」", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreErrorsThanCollected_SaysWhichRowsWereSummarized()
    {
        var error = ProjectionFailureText.Create(new ProjectionResult(0,
            [new RowProjectionError(7, "金額", "abc", "不是有效金額。請確認來源資料的金額格式")])
        {
            TotalErrorCount = 573
        });

        Assert.Equal(
            "573 列無法轉換，系統沒有儲存這次配對結果。以下依前 1 列整理。" +
            "欄位「金額」有 1 列不是有效金額：「abc」在第 7 列。請確認來源資料的金額格式。",
            error.Message);
    }

    [Fact]
    public void ManyRowsAndValues_ShowFirstTenAndTheRest()
    {
        var rows = Enumerable.Range(2, 12)
            .Select(row => new RowProjectionError(row, "代碼", "X", Unlisted))
            .Concat(Enumerable.Range(1, 11)
                .Select(index => new RowProjectionError(100 + index, "代碼", "V" + index, Unlisted)))
            .ToList();

        var detail = Assert.Single(ProjectionFailureText.Create(new ProjectionResult(0, rows)).Details!);

        Assert.StartsWith(
            "欄位「代碼」有 23 列未歸類為人工或自動：「X」在第 2、3、4、5、6、7、8、9、10、11 列，共 12 列；「V1」在第 101 列；",
            detail.Message,
            StringComparison.Ordinal);
        Assert.Contains("「V9」在第 109 列；另有 2 個值。", detail.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("V10", detail.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleValueWithManyRows_DoesNotRepeatTheCountAlreadyInTheSentence()
    {
        var rows = Enumerable.Range(2, 12)
            .Select(row => new RowProjectionError(row, "代碼", "轉入", Unlisted))
            .ToList();

        var detail = Assert.Single(ProjectionFailureText.Create(new ProjectionResult(0, rows)).Details!);

        Assert.Equal(
            "欄位「代碼」有 12 列未歸類為人工或自動：「轉入」在第 2、3、4、5、6、7、8、9、10、11 列等。請回到欄位配對把這些值歸類。",
            detail.Message);
    }

    [Fact]
    public void MultiSourceRows_KeepSourceNamesBeforeRowNumbers()
    {
        var detail = Assert.Single(ProjectionFailureText.Create(new ProjectionResult(0,
        [
            new RowProjectionError(7, "金額", "bad", "不是有效金額", "JE.csv"),
            new RowProjectionError(9, "金額", "bad", "不是有效金額", "JE.csv"),
            new RowProjectionError(5, "金額", "bad", "不是有效金額", "JE-q2.csv [Q2]")
        ])).Details!);

        Assert.Equal("欄位「金額」有 3 列不是有效金額：「bad」在 JE.csv 第 7、9 列，JE-q2.csv [Q2] 第 5 列。", detail.Message);
    }

    [Fact]
    public void ManualAutoReasons_SplitIntoProblemAndInstruction()
    {
        Assert.Equal(1, ProjectionErrorReasons.ManualBlank.Count(character => character == '。'));
        Assert.Equal(1, ProjectionErrorReasons.ManualUnlisted.Count(character => character == '。'));
        Assert.StartsWith("是空白。", ProjectionErrorReasons.ManualBlank, StringComparison.Ordinal);
        Assert.StartsWith("未歸類為人工或自動。", ProjectionErrorReasons.ManualUnlisted, StringComparison.Ordinal);
    }
}
