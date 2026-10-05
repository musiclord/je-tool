using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9CreditCodeWorkflowTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ExistingCommittedMappingWithoutCreditCode_LoadsValidatesAndExportsWithoutReprojection(string provider)
    {
        using var fixture = await Batch9MappingTestFixture.CreateAsync(provider);
        await fixture.CommitAsync(Codes(), "side");
        var current = (await fixture.LoadAsync()).GetProperty("mapping").GetProperty("gl");
        var oldMapping = Codes(); oldMapping.Remove("dcCreditCode");
        var folder = new JetProjectFolder(fixture.Host.ProjectsRoot);
        ILocalProjectDatabase database = provider == "duckdb" ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);
        // 合成舊 v2 案件：保留已完成的 D/C 投影，只讓已儲存 snapshot 缺少新版新增的貸方代碼。
        // 不經新版確認入口猜值，也不改任何 target 列。
        await new LocalMappingStateStore(database).SaveAsync(fixture.ProjectId,
            new CommittedMapping(DatasetKind.Gl, oldMapping, "side",
                current.GetProperty("sourceBatchId").GetString()!, current.GetProperty("committedUtc").GetDateTimeOffset()),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        var persistedMapping = loaded.GetProperty("mapping").GetProperty("gl").GetProperty("mapping");
        Assert.False(persistedMapping.TryGetProperty("dcCreditCode", out _));
        Assert.Equal("D", persistedMapping.GetProperty("dcDebitCode").GetString());
        var validated = await fixture.Host.DispatchAsync("validate.run");
        Assert.Equal(2, validated.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());
        var exported = await fixture.Host.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new
        { runId = validated.GetProperty("resultRef").GetProperty("runId").GetString() }));
        Assert.True(exported.GetProperty("ok").GetBoolean());
        Assert.Equal(2, exported.GetProperty("artifacts").GetArrayLength());
        var report = exported.GetProperty("artifacts").EnumerateArray()
            .Single(item => item.GetProperty("kind").GetString() == "validationReport");
        using (var workbook = new XLWorkbook(report.GetProperty("fullPath").GetString()!))
        using (var metadata = JsonDocument.Parse(workbook.Worksheet(MappingMetadataFormat.WorksheetName)
                   .Cell(MappingMetadataFormat.PayloadCell).GetString()))
        {
            var snapshot = metadata.RootElement.GetProperty("gl").GetProperty("mapping");
            Assert.False(snapshot.TryGetProperty("dcCreditCode", out _));
            Assert.Equal("D", snapshot.GetProperty("dcDebitCode").GetString());
        }
        Assert.Equal(10_000, await fixture.ScalarAsync("SELECT amount_scaled FROM target_gl_entry WHERE document_number = 'V000';"));
        Assert.Equal(-10_000, await fixture.ScalarAsync("SELECT amount_scaled FROM target_gl_entry WHERE document_number = 'V001';"));
        var reopened = await fixture.LoadAsync();
        Assert.Equal(persistedMapping.GetRawText(), reopened.GetProperty("mapping").GetProperty("gl").GetProperty("mapping").GetRawText());
    }

    [Theory]
    [InlineData("sqlite", "side")]
    [InlineData("duckdb", "side")]
    [InlineData("sqlite", "flag")]
    [InlineData("duckdb", "flag")]
    public async Task KnownTwoCodes_HaveFixedSignedAnswers_AndPersistLiteralCodes(string provider, string mode)
    {
        using var fixture = await Batch9MappingTestFixture.CreateAsync(provider);
        var mapping = Codes();
        mapping["dcDebitCode"] = "D"; mapping["dcCreditCode"] = "c";
        await fixture.CommitAsync(mapping, mode);
        Assert.Equal(10_000, await fixture.ScalarAsync("SELECT amount_scaled FROM target_gl_entry WHERE document_number = 'V000';"));
        Assert.Equal(-10_000, await fixture.ScalarAsync("SELECT amount_scaled FROM target_gl_entry WHERE document_number = 'V001';"));
        var loaded = await fixture.LoadAsync();
        Assert.Equal("D", loaded.GetProperty("mapping").GetProperty("gl").GetProperty("mapping").GetProperty("dcDebitCode").GetString());
        Assert.Equal("c", loaded.GetProperty("mapping").GetProperty("gl").GetProperty("mapping").GetProperty("dcCreditCode").GetString());
    }

    [Theory]
    [InlineData("sqlite", "side")]
    [InlineData("duckdb", "side")]
    [InlineData("sqlite", "flag")]
    [InlineData("duckdb", "flag")]
    public async Task UnknownAndBlankCodesAcrossSources_ReportFullCountAndRetainPriorState(string provider, string mode)
    {
        using var fixture = await Batch9MappingTestFixture.CreateAsync(provider, manyRows: true);
        var before = await fixture.LoadAsync();
        var mapping = Codes(); mapping["dcField"] = "BadSide";
        var error = await Assert.ThrowsAsync<JetActionException>(() => fixture.CommitAsync(mapping, mode));
        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.StartsWith("65 列無法轉換", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, error.Details!.Count);
        var unknown = Assert.Single(error.Details.Where(item => item.ReasonCode == "dc_unlisted"));
        var blank = Assert.Single(error.Details.Where(item => item.ReasonCode == "dc_blank"));
        Assert.All(error.Details, item => Assert.Equal("BadSide", item.SourceColumn));
        Assert.Contains("63 列", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("共 60 列", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("second.xlsx 第 2、3、4 列", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("2 列", blank.Message, StringComparison.Ordinal);
        Assert.Contains("second.xlsx 第 5、6 列", blank.Message, StringComparison.Ordinal);
        var after = await fixture.LoadAsync();
        Assert.Equal(before.GetProperty("mapping").GetRawText(), after.GetProperty("mapping").GetRawText());
        Assert.Equal(before.GetProperty("latestRuns").GetRawText(), after.GetProperty("latestRuns").GetRawText());
        Assert.Equal(650_000, await fixture.ScalarAsync("SELECT CAST(SUM(amount_scaled) AS BIGINT) FROM target_gl_entry;"));
    }

    [Theory]
    [InlineData("sqlite", false)]
    [InlineData("duckdb", false)]
    [InlineData("sqlite", true)]
    [InlineData("duckdb", true)]
    public async Task MissingOrOverlappingCreditCode_ExplainsNextStepWithoutChangingCommittedMapping(string provider, bool overlap)
    {
        using var fixture = await Batch9MappingTestFixture.CreateAsync(provider);
        var before = await fixture.LoadAsync();
        var mapping = Codes();
        if (overlap) mapping["dcCreditCode"] = "\u3000d "; else mapping.Remove("dcCreditCode");
        var error = await Assert.ThrowsAsync<JetActionException>(() => fixture.CommitAsync(mapping, "side"));
        Assert.Contains("貸方代碼", error.Message, StringComparison.Ordinal);
        Assert.Contains("借方代碼", error.Message, StringComparison.Ordinal);
        var after = await fixture.LoadAsync();
        Assert.Equal(before.GetProperty("mapping").GetRawText(), after.GetProperty("mapping").GetRawText());
    }

    [Theory]
    [InlineData("sqlite", false)]
    [InlineData("duckdb", false)]
    [InlineData("sqlite", true)]
    [InlineData("duckdb", true)]
    public async Task Metadata_OldMissingCreditCodeFailsWithoutMutation_NewCodesRoundTripVerbatim(string provider, bool hasCredit)
    {
        using var fixture = await Batch9MappingTestFixture.CreateAsync(provider);
        var before = await fixture.LoadAsync();
        var mapping = Codes(); mapping["dcDebitCode"] = "D";
        if (hasCredit) mapping["dcCreditCode"] = "c"; else mapping.Remove("dcCreditCode");
        var gl = new CommittedMapping(DatasetKind.Gl, mapping, "side", "synthetic-gl", DateTimeOffset.UnixEpoch);
        var tb = new CommittedMapping(DatasetKind.Tb, new Dictionary<string, string>
            { ["accNum"] = "Account", ["accName"] = "Name", ["amount"] = "Good" }, "direct", "synthetic-tb", DateTimeOffset.UnixEpoch);
        var encoded = MappingMetadataCodec.Encode(gl, tb);
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Name = MappingMetadataFormat.WorksheetName;
            sheet.Cell(MappingMetadataFormat.MarkerCell).Value = MappingMetadataFormat.Marker;
            sheet.Cell(MappingMetadataFormat.VersionCell).Value = MappingMetadataFormat.CurrentVersion;
            sheet.Cell(MappingMetadataFormat.PayloadCell).Value = encoded;
        });
        try
        {
            if (hasCredit)
            {
                var restored = await fixture.Host.DispatchAsync("mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = path }));
                Assert.Equal("D", restored.GetProperty("gl").GetProperty("mapping").GetProperty("dcDebitCode").GetString());
                Assert.Equal("c", restored.GetProperty("gl").GetProperty("mapping").GetProperty("dcCreditCode").GetString());
            }
            else
            {
                var error = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync("mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = path })));
                Assert.Equal(JetErrorCodes.MappingMetadataInvalid, error.Code);
                Assert.Contains("貸方代碼", error.Message, StringComparison.Ordinal);
                Assert.Contains("第三步", error.Message, StringComparison.Ordinal);
            }
            var after = await fixture.LoadAsync();
            Assert.Equal(before.GetProperty("mapping").GetRawText(), after.GetProperty("mapping").GetRawText());
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    private static Dictionary<string, string> Codes()
    {
        var mapping = Batch9MappingTestFixture.Mapping();
        mapping["dcField"] = "Side"; mapping["dcDebitCode"] = "D"; mapping["dcCreditCode"] = "C";
        return mapping;
    }
}
