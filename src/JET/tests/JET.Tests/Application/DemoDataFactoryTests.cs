using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// demo 測試案件資料的建構不變量(baseline 不觸發、seed 確定觸發)。
/// 不進 DB:純斷言資料物件,作為規則 oracle(DemoRuleOracleTests,端到端)之前的快速防線。
/// </summary>
public sealed class DemoDataFactoryTests
{
    // 第五步「依審計目的」的「較少使用的科目」「編製分錄較少的人員」加入時預設 11 筆以下（前端 ui-core.js 的 PRESCREEN_DEFAULTS）。
    // 第四步照舊版沒有同樣門檻的兩條規則，後端沒有這個常數；範例資料仍照這個預設安排較少使用的人員與科目。
    private const int StepFiveDefaultMaxEntries = 11;

    [Fact]
    public void Create_ReturnsExactScale()
    {
        var data = DemoDataFactory.Create();

        Assert.Equal(DemoDataFactory.GlVoucherCount, data.GlRows.Select(r => r.VoucherNumber).Distinct().Count());
        Assert.Equal(DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher, data.GlRows.Count);
        Assert.Equal(DemoDataFactory.TbAccountCount, data.TbRows.Count);
        Assert.Equal(DemoDataFactory.TbAccountCount, data.AccountMappingRows.Count);
    }

    [Fact]
    public void Create_IsDeterministic()
    {
        var first = JsonSerializer.Serialize(DemoDataFactory.Create());
        var second = JsonSerializer.Serialize(DemoDataFactory.Create());

        Assert.Equal(first, second);
    }

    [Fact]
    public void EveryVoucherBalanced_ExceptDesignedOutOfPeriodPair()
    {
        var data = DemoDataFactory.Create();

        var unbalanced = data.GlRows
            .GroupBy(r => r.VoucherNumber)
            .Where(v => v.Sum(r => r.IsDebit ? r.Amount : -r.Amount) != 0m)
            .ToList();

        // 唯一不平者為期外對照組（OutOfPeriodVouchers 張，全 gl_date=2026-01-05）——刻意造以驗證
        // 「期外不平傳票不入借貸不平母體」；兩張互為鏡像使全域借貸淨額仍為 0。
        Assert.Equal(DemoDataFactory.OutOfPeriodVouchers, unbalanced.Count);
        Assert.All(unbalanced, v => Assert.All(v, r =>
        {
            Assert.Equal(2026, r.GlDate.Year);
            Assert.Equal(1, r.GlDate.Month);
            Assert.Equal(5, r.GlDate.Day);
        }));
        Assert.Equal(0m, data.GlRows.Sum(r => r.IsDebit ? r.Amount : -r.Amount));
    }

    [Fact]
    public void AllGlDatesWithin2025_ExceptDesignedOutOfPeriodRows()
    {
        var data = DemoDataFactory.Create();

        var outOfPeriod = data.GlRows.Where(r => r.GlDate.Year != 2025).ToList();
        // 期外對照組：OutOfPeriodVouchers 張 × LinesPerVoucher 行，全 2026-01-05（母體期間界定素材）。
        Assert.Equal(DemoDataFactory.OutOfPeriodVouchers * DemoDataFactory.LinesPerVoucher, outOfPeriod.Count);
        Assert.All(outOfPeriod, r =>
        {
            Assert.Equal(2026, r.GlDate.Year);
            Assert.Equal(1, r.GlDate.Month);
            Assert.Equal(5, r.GlDate.Day);
        });
        Assert.All(data.GlRows.Where(r => r.GlDate.Year == 2025), r => Assert.Equal(2025, r.GlDate.Year));
    }

    [Fact]
    public void TbTotalsAreDerivedFromInPeriodGl()
    {
        var data = DemoDataFactory.Create();

        // TB 本期變動只由本期（2025）GL 彙總而得（期外對照組不計入 TB，與完整性 GL 側同口徑）。
        var inPeriod = data.GlRows.Where(r => r.GlDate.Year == 2025).ToList();
        var glDebit = inPeriod.Where(r => r.IsDebit)
            .GroupBy(r => r.AccountNumber).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
        var glCredit = inPeriod.Where(r => !r.IsDebit)
            .GroupBy(r => r.AccountNumber).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        Assert.All(data.TbRows, tb =>
        {
            Assert.Equal(glDebit.GetValueOrDefault(tb.AccountNumber, 0m), tb.DebitTotal);
            Assert.Equal(glCredit.GetValueOrDefault(tb.AccountNumber, 0m), tb.CreditTotal);
            Assert.Equal(tb.OpeningBalance + tb.DebitTotal - tb.CreditTotal, tb.ClosingBalance);
        });
    }

    // ── 建構不變量:baseline 不觸發任何規則 ──

    [Fact]
    public void Baseline_NoAmountIsMillionMultiple_ExceptTrailingZerosSeed()
    {
        var data = DemoDataFactory.Create();

        // 金額為 1,000,000 整數倍者(連續零尾數門檻 6)只能出現在 R4 種子(金額 2,000,000)。
        var millionMultiples = data.GlRows.Where(r => r.Amount % 1_000_000m == 0m).ToList();
        Assert.All(millionMultiples, r => Assert.Equal(2_000_000m, r.Amount));
        // 數量 = R4 種子的列數
        Assert.Equal(DemoDataFactory.TrailingZerosVouchers * DemoDataFactory.LinesPerVoucher, millionMultiples.Count);
    }

    [Fact]
    public void RareAccounts_AreExactlyThree_EachAtMostElevenEntries()
    {
        var data = DemoDataFactory.Create();

        var lowFreq = data.GlRows
            .GroupBy(r => r.AccountNumber)
            .Where(g => g.Count() <= StepFiveDefaultMaxEntries)
            .ToList();

        Assert.Equal(DemoDataFactory.RareAccountCount, lowFreq.Count);
        Assert.Equal(
            DemoDataFactory.RareAccountCount * DemoDataFactory.RareAccountVouchersEach,
            lowFreq.Sum(g => g.Count()));
    }

    [Fact]
    public void RarePreparer_IsExactlyOne_Authorized_AtMostEleven()
    {
        var data = DemoDataFactory.Create();

        var lowFreq = data.GlRows
            .GroupBy(r => r.CreatedBy)
            .Where(g => g.Count() <= StepFiveDefaultMaxEntries)
            .ToList();

        Assert.Single(lowFreq);
        Assert.Equal(DemoDataFactory.RarePreparer, lowFreq[0].Key);
        Assert.Contains(DemoDataFactory.RarePreparer, data.AuthorizedPreparers);
        Assert.Equal(DemoDataFactory.RarePreparerVouchers * DemoDataFactory.LinesPerVoucher, lowFreq[0].Count());
    }

    [Fact]
    public void NonAuthorizedPreparer_NotInList_AndAboveRareThreshold()
    {
        var data = DemoDataFactory.Create();

        Assert.DoesNotContain(DemoDataFactory.NonAuthorizedPreparer, data.AuthorizedPreparers);
        var entries = data.GlRows.Count(r => r.CreatedBy == DemoDataFactory.NonAuthorizedPreparer);
        Assert.True(entries > StepFiveDefaultMaxEntries, $"非授權者列數應 > 11,實際 {entries}");
        Assert.Equal(DemoDataFactory.NonAuthorizedVouchers * DemoDataFactory.LinesPerVoucher, entries);
    }

    [Fact]
    public void SafeDescriptions_ContainNoSuspiciousKeyword()
    {
        var data = DemoDataFactory.Create();

        var keywords = SuspiciousKeywordDefaults.Defaults;
        var blankCount = data.GlRows.Count(r => string.IsNullOrWhiteSpace(r.Description));
        var keywordCount = data.GlRows.Count(r =>
            !string.IsNullOrWhiteSpace(r.Description) &&
            keywords.Any(k => r.Description.ToUpperInvariant().Contains(k.ToUpperInvariant())));

        // +1 各源自期外對照組：OOP-2 借方摘要空白、OOP-1 借方含關鍵字（原始資料層計數，未界定期間）。
        Assert.Equal(DemoDataFactory.BlankDescriptionVouchers + 1, blankCount);
        Assert.Equal(DemoDataFactory.SuspiciousKeywordVouchers + 1, keywordCount);
    }

    [Fact]
    public void AuthorizedPreparers_AreSeven_AndExcludeNonAuthorized()
    {
        var data = DemoDataFactory.Create();

        Assert.Equal(7, data.AuthorizedPreparers.Count);
        Assert.DoesNotContain(DemoDataFactory.NonAuthorizedPreparer, data.AuthorizedPreparers);
    }

    [Fact]
    public void AccountMapping_CategoryCounts_MatchDerivation()
    {
        var data = DemoDataFactory.Create();
        var rows = data.AccountMappingRows;

        Assert.Equal(2, rows.Count(r => r.Category == AccountMappingCategories.Cash));
        Assert.Equal(2, rows.Count(r => r.Category == AccountMappingCategories.Receivables));
        Assert.Equal(1, rows.Count(r => r.Category == AccountMappingCategories.ReceiptInAdvance));
        Assert.Equal(3, rows.Count(r => r.Category == AccountMappingCategories.Revenue));
        Assert.Equal(
            DemoDataFactory.TbAccountCount - 2 - 2 - 1 - 3,
            rows.Count(r => r.Category == AccountMappingCategories.Others));
    }

    [Fact]
    public void ClientName_IsFixedFictitiousConstant()
    {
        var data = DemoDataFactory.Create();
        Assert.Equal(DemoDataFactory.ClientNameConst, data.ClientName);
        Assert.Equal("範例製造股份有限公司", data.ClientName);
    }

    [Fact]
    public void ProjectMetadata_CoversAudit2025Period()
    {
        var data = DemoDataFactory.Create();
        Assert.Equal("2025-01-01", data.PeriodStart);
        Assert.Equal("2025-12-31", data.PeriodEnd);
        Assert.Equal("2025-12-31", data.PrepStartDate);
    }
}
