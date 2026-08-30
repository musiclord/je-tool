using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class MappingHandlersTests
{
    private const string CreatePayload =
        """
        {
          "projectCode": "ENG-2025-001",
          "entityName": "範例股份有限公司",
          "operatorId": "auditor01",
          "periodStart": "2025-01-01",
          "periodEnd": "2025-12-31"
        }
        """;

    [Fact]
    public void MappingReviewPrerequisite_Required_UsesStableErrorCode()
    {
        var ex = Assert.Throws<JetActionException>(() =>
            MappingReviewPrerequisite.EnsureSatisfied(mappingReviewRequired: true));

        Assert.Equal(JetErrorCodes.MappingReviewRequired, ex.Code);
        MappingReviewPrerequisite.EnsureSatisfied(mappingReviewRequired: false);
    }

    [Fact]
    public async Task CommitGl_CurrentPayload_PersistsV2NormalizedDefaultsWithoutChangingProjectionContract()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var stored = await new LocalMappingStateStore(database).FindAsync(
            context.ProjectId,
            DatasetKind.Gl,
            CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(MappingMetadataFormat.CurrentVersion, stored.FormatVersion);
        Assert.NotNull(stored.GlOptions);
        var expectedApprovalMode = stored.Mapping.ContainsKey(GlMappingKeys.DocDate)
            ? ApprovalDateModeNames.Mapped
            : ApprovalDateModeNames.Unmapped;
        Assert.Equal(expectedApprovalMode, stored.GlOptions.ApprovalDateMode);
        Assert.Null(stored.GlOptions.PostingStatusPolicy);
        Assert.Equal(["1"], stored.GlOptions.ManualAutoPolicy.ManualValues);
        Assert.Equal(["0"], stored.GlOptions.ManualAutoPolicy.AutomaticValues);
        Assert.Empty(stored.GlOptions.RdeFields);
    }

    [Fact]
    public async Task CommitGl_V2ProjectionOptions_ProjectsStrictManualAndTypedRdeValues()
    {
        using var host = new HandlerTestHost();
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns(
                "傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要",
                "人工傳票", "金額", "借方旗標", "自訂文字", "自訂日期", "自訂金額")
            .AddRow(
                "JE-001", "2025-01-15", "2025-01-16", "1000", "現金", "測試",
                "M", 100m, "1", "  保留原文  ", "2025-01-17", "12.34567");

        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;
        var filePath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                filePath,
                fileName = "projection-quality.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(filePath);
        }

        var committed = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = builder.BuildFlagModeMapping(),
            amountMode = "flag",
            approvalDateMode = "mapped",
            manualAutoPolicy = new
            {
                manualValues = new[] { " M " },
                automaticValues = new[] { "A" }
            },
            rdeFields = new object[]
            {
                new { sourceColumn = "自訂文字", label = "自訂文字", valueType = "text" },
                new { sourceColumn = "自訂日期", label = "自訂日期", valueType = "date" },
                new { sourceColumn = "自訂金額", label = "自訂金額", valueType = "money" }
            }
        }));

        Assert.Equal("mapped", committed.GetProperty("approvalDateMode").GetString());
        Assert.Equal(3, committed.GetProperty("rdeFields").GetArrayLength());
        Assert.All(
            committed.GetProperty("rdeFields").EnumerateArray(),
            field => Assert.Matches("^rde\\.[0-9a-f]{32}$", field.GetProperty("fieldId").GetString()));
        Assert.Equal(3, await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM target_gl_rde_value;"));

        var ids = committed.GetProperty("rdeFields")
            .EnumerateArray()
            .Select(field => field.GetProperty("fieldId").GetString()!)
            .ToArray();
        var recommitted = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = builder.BuildFlagModeMapping(),
            amountMode = "flag",
            approvalDateMode = "mapped",
            manualAutoPolicy = new
            {
                manualValues = new[] { "M" },
                automaticValues = new[] { "A" }
            },
            rdeFields = new object[]
            {
                new { fieldId = ids[0], sourceColumn = "自訂文字", label = "自訂文字", valueType = "text" },
                new { fieldId = ids[1], sourceColumn = "自訂日期", label = "自訂日期", valueType = "date" },
                new { fieldId = ids[2], sourceColumn = "自訂金額", label = "自訂金額", valueType = "money" }
            }
        }));
        Assert.Equal(
            ids,
            recommitted.GetProperty("rdeFields")
                .EnumerateArray()
                .Select(field => field.GetProperty("fieldId").GetString()!)
                .ToArray());

        var duplicateId = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "mapping.commit.gl",
            JsonSerializer.Serialize(new
            {
                mapping = builder.BuildFlagModeMapping(),
                amountMode = "flag",
                approvalDateMode = "mapped",
                manualAutoPolicy = new
                {
                    manualValues = new[] { "M" },
                    automaticValues = new[] { "A" }
                },
                rdeFields = new object[]
                {
                    new
                    {
                        fieldId = ids[0], sourceColumn = "自訂文字", label = "自訂文字", valueType = "text"
                    },
                    new
                    {
                        fieldId = ids[0], sourceColumn = "自訂日期", label = "自訂日期", valueType = "date"
                    }
                }
            })));
        Assert.Equal(JetErrorCodes.InvalidPayload, duplicateId.Code);
    }

    [Fact]
    public async Task CommitGl_PostingStatusPolicy_RetainsRawRowsAndPartitionsEffectivePopulation()
    {
        using var host = new HandlerTestHost();
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns(
                "傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要",
                "金額", "借方旗標", "過帳狀態")
            .AddRow("JE-001", "2025-01-15", "1000", "現金", "有效", 100m, "1", " posted ")
            .AddRow("JE-002", "2025-01-16", "2000", "應付", "排除", 100m, "0", "VOID");

        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;
        var filePath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                filePath,
                fileName = "posting-policy.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(filePath);
        }

        var mapping = builder.BuildFlagModeMapping();
        mapping[GlMappingKeys.PostingStatus] = "過帳狀態";
        var committed = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping,
            amountMode = "flag",
            postingStatusPolicy = new
            {
                acceptedValues = new[] { "POSTED" },
                includeBlank = false
            }
        }));

        Assert.Equal(2, committed.GetProperty("projectedRowCount").GetInt32());
        Assert.Equal(2, await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM target_gl_entry;"));
        Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM target_gl_entry WHERE is_effective = 1;"));
        Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE exclusion_reason = 'posting_status';"));
    }

    [Fact]
    public async Task QuerySourceQualityPage_PagesBlankPostDatesFromRawSuccessfulGeneration()
    {
        using var host = new HandlerTestHost();
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns(
                "傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JE-001", "", "1000", "現金", "blank-1", 100m, "1")
            .AddRow("JE-002", "   ", "2000", "應付", "blank-2", 100m, "0")
            .AddRow("JE-003", "2025-01-15", "3000", "收入", "effective", 50m, "1");

        await host.DispatchAsync("project.create", CreatePayload);
        var filePath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                filePath,
                fileName = "source-quality.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(filePath);
        }
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = builder.BuildFlagModeMapping(),
            amountMode = "flag"
        }));

        var first = await host.DispatchAsync(
            "query.sourceQualityPage",
            """{ "pageSize": 1 }""");
        var firstRow = Assert.Single(first.GetProperty("rows").EnumerateArray());
        Assert.Equal("nullPostDate", firstRow.GetProperty("category").GetString());
        Assert.Equal(2, firstRow.GetProperty("sourceRowNumber").GetInt32());
        Assert.Contains("source-quality.xlsx", firstRow.GetProperty("sourceLabel").GetString(), StringComparison.Ordinal);
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var second = await host.DispatchAsync(
            "query.sourceQualityPage",
            JsonSerializer.Serialize(new { cursor, pageSize = 1 }));
        var secondRow = Assert.Single(second.GetProperty("rows").EnumerateArray());
        Assert.Equal(3, secondRow.GetProperty("sourceRowNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);

        var invalidCursor = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "query.sourceQualityPage",
            """{ "cursor": 1 }"""));
        Assert.Equal(JetErrorCodes.InvalidPayload, invalidCursor.Code);
    }

    [Fact]
    public async Task CommitGl_InvalidAmountMode_ThrowsUnsupportedModeBeforeRepositoryAccess()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "mapping.commit.gl",
            """{ "mapping": {}, "amountMode": "bogus" }"""));

        Assert.Equal(JetErrorCodes.UnsupportedMode, ex.Code);
        Assert.Contains("amountMode 'bogus' 無效", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitTb_InvalidChangeMode_ThrowsUnsupportedModeBeforeRepositoryAccess()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "mapping.commit.tb",
            """{ "mapping": {}, "changeMode": "bogus" }"""));

        Assert.Equal(JetErrorCodes.UnsupportedMode, ex.Code);
        Assert.Contains("changeMode 'bogus' 無效", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitPlan_UnknownColumns_ThrowsMappingColumnNotFound()
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.DocNum] = "document",
            [GlMappingKeys.PostDate] = "post_date",
            [GlMappingKeys.AccNum] = "account",
            [GlMappingKeys.AccName] = "account_name",
            [GlMappingKeys.Description] = "missing_column",
            [GlMappingKeys.Amount] = "typo_column"
        };

        var ex = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(
            new GlMappingRequest(
                "project",
                "batch",
                mapping,
                GlAmountMode.SignedAmount,
                ["document", "post_date", "account", "account_name"],
                ProjectDocument.DefaultMoneyScale,
                DateParseOptions.Default)));

        Assert.Equal(JetErrorCodes.MappingColumnNotFound, ex.Code);
        Assert.Contains("missing_column、typo_column", ex.Message, StringComparison.Ordinal);
    }
}
