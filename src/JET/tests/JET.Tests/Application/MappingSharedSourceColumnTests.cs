using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 審計員在第三步把兩個 GL 欄位配到同一個來源欄，例如總帳日期與傳票核准日都選「Date」，科目名稱與傳票摘要都選「Name」。
/// 2026-10-08 研究欄位配對頁時從程式推論：確認配對時，欄位定義會因同一個來源欄被認領兩次而丟出例外，畫面只看得到系統錯誤。
/// 使用者 2026-10-19 要求在當前階段處理。依「預設審計員可信」，JET 照審計員的選擇配對，兩個欄位都帶同一個來源值，不另外擋。
/// 第一次失敗（兩個資料庫都是「來源欄位定義 'Date' 被重複配對。」）：20261019-091333937-1708bb1345dd4c47a210aa9531af4e6e。
/// </summary>
public sealed class MappingSharedSourceColumnTests
{
    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task TwoGlFieldsOnOneSourceColumn_CommitAndBothCarryTheSourceValue(string provider)
    {
        using var fixture = await Batch9MappingTestFixture.CreateAsync(provider);
        var mapping = Batch9MappingTestFixture.Mapping();
        mapping[GlMappingKeys.DocDate] = mapping[GlMappingKeys.PostDate];
        mapping[GlMappingKeys.Description] = mapping[GlMappingKeys.AccName];

        await fixture.CommitAsync(mapping, "signed");

        Assert.Equal(2, await fixture.ScalarAsync("SELECT COUNT(*) FROM target_gl_entry"));
        Assert.Equal(0, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM target_gl_entry WHERE approval_date IS NULL OR approval_date <> post_date"));
        Assert.Equal(0, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM target_gl_entry WHERE document_description IS NULL OR document_description <> account_name"));

        // 報告與底稿列出的 GL 欄位定義：第一個欄位在來源欄原位置改名，第二個欄位照同一個來源欄的定義追加在後面。
        var definitions = await ReadTargetDefinitionsAsync(fixture, provider);
        Assert.Equal(Enumerable.Range(1, definitions.Count), definitions.Select(static definition => definition.Ordinal));
        var postDate = Assert.Single(definitions, static definition => definition.FieldName == "總帳日期_JE");
        var approvalDate = Assert.Single(definitions, static definition => definition.FieldName == "傳票核准日_JE");
        Assert.Equal("Date", postDate.Description);
        Assert.Equal("Date", approvalDate.Description);
        Assert.Equal(postDate.Kind, approvalDate.Kind);
        Assert.True(approvalDate.Ordinal > postDate.Ordinal);
        var accountName = Assert.Single(definitions, static definition => definition.FieldName == "會計科目名稱_JE");
        var description = Assert.Single(definitions, static definition => definition.FieldName == "傳票摘要_JE");
        Assert.Equal("Name", accountName.Description);
        Assert.Equal("Name", description.Description);
        Assert.Equal(accountName.Kind, description.Kind);
        Assert.Equal(accountName.TextLength, description.TextLength);
    }

    // TB 的科目編號與科目名稱選同一欄，例如來源只有一欄「1101 現金」。
    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task TbAccountCodeAndNameOnOneSourceColumn_CommitAndBothCarryTheSourceValue(string provider)
    {
        using var fixture = await Batch9MappingTestFixture.CreateAsync(provider);

        await fixture.Host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
        { changeMode = "direct", mapping = new { accNum = "Account", accName = "Account", amount = "Good" } }));

        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM target_tb_balance"));
        Assert.Equal(0, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM target_tb_balance WHERE account_name IS NULL OR account_name <> account_code"));
        var definitions = await ReadTargetDefinitionsAsync(fixture, provider, DatasetKind.Tb);
        Assert.Equal("Account", Assert.Single(definitions, static definition => definition.FieldName == "會計科目編號_TB").Description);
        Assert.Equal("Account", Assert.Single(definitions, static definition => definition.FieldName == "會計科目名稱_TB").Description);
    }

    private static async Task<IReadOnlyList<LegacyFieldDefinition>> ReadTargetDefinitionsAsync(
        Batch9MappingTestFixture fixture,
        string provider,
        DatasetKind dataset = DatasetKind.Gl)
    {
        var folder = new JetProjectFolder(fixture.Host.ProjectsRoot);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        return await new LocalFieldDefinitionFactsPort(database).ReadAsync(
            fixture.ProjectId,
            dataset,
            LegacyFieldDefinitionScope.Target,
            CancellationToken.None);
    }
}
