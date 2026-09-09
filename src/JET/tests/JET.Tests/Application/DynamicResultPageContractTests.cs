using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// filter／INF dynamic result columns 的 wire contract。全部資料為自含 synthetic workbook；
/// oracle 直接來自 committed RDE ordinal、輸入列與 frozen wire type，不重算篩選 SQL。
/// </summary>
public sealed class DynamicResultPageContractTests
{
    private static readonly (string Key, string Label, string ValueType)[] FilterFixedColumns =
    [
        ("documentNumber", "傳票號碼", "text"),
        ("lineItem", "傳票文件項次", "text"),
        ("postDate", "過帳日期", "date"),
        ("accountCode", "會計科目編號", "text"),
        ("accountName", "會計科目名稱", "text"),
        ("amount", "傳票金額", "money"),
        ("drCr", "借貸別", "text"),
        ("description", "傳票摘要", "text")
    ];

    private static readonly (string Key, string Label, string ValueType)[] InfFixedColumns =
    [
        ("documentNumber", "傳票號碼", "text"),
        ("accountCode", "會計科目編號", "text"),
        ("accountName", "會計科目名稱", "text"),
        ("debit", "借方金額", "money"),
        ("credit", "貸方金額", "money"),
        ("postDate", "過帳日期", "date"),
        ("approvalDate", "核准日期", "date"),
        ("createdBy", "編製人員", "text"),
        ("approvedBy", "核准人員", "text"),
        ("description", "傳票摘要", "text")
    ];

    [Fact]
    public async Task FilterAndInfPages_RenderExactCustomKeysNativeValuesAndCommittedOrdinal()
    {
        using var host = new HandlerTestHost();
        var setup = await SetupRdeProjectAsync(host);

        // AST 刻意 date 在 text 前；輸出仍須依 committed ordinal（money, text, date）取 text, date。
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[]
            {
                new
                {
                    name = "typed dynamic columns",
                    rationale = "synthetic contract",
                    groups = new[]
                    {
                        new
                        {
                            join = "AND",
                            rules = new object[]
                            {
                                new
                                {
                                    join = "AND", type = "typed", fieldId = setup.DateFieldId,
                                    @operator = "isNotBlank"
                                },
                                new
                                {
                                    join = "OR", type = "typed", fieldId = setup.TextFieldId,
                                    @operator = "isBlank"
                                }
                            }
                        }
                    }
                }
            }
        }));

        var filter = await host.DispatchAsync(
            "query.filterHitsPage",
            JsonSerializer.Serialize(new { scenarioPosition = 1, pageSize = 10 }));
        AssertColumns(
            filter.GetProperty("columns"),
            FilterFixedColumns,
            [
                (setup.TextFieldId, "自訂文字", "text"),
                (setup.DateFieldId, "自訂日期", "date")
            ]);

        var filterRows = filter.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, filterRows.Length);
        AssertCustomKeys(filterRows[0], setup.TextFieldId, setup.DateFieldId);
        Assert.Equal("alpha", filterRows[0].GetProperty("customValues").GetProperty(setup.TextFieldId).GetString());
        Assert.Equal("2025-04-02", filterRows[0].GetProperty("customValues").GetProperty(setup.DateFieldId).GetString());
        AssertCustomKeys(filterRows[1], setup.TextFieldId, setup.DateFieldId);
        Assert.Equal(JsonValueKind.Null, filterRows[1].GetProperty("customValues").GetProperty(setup.TextFieldId).ValueKind);
        Assert.Equal(JsonValueKind.Null, filterRows[1].GetProperty("customValues").GetProperty(setup.DateFieldId).ValueKind);

        var inf = await host.DispatchAsync("query.infSamplePage", JsonSerializer.Serialize(new { pageSize = 10 }));
        AssertColumns(
            inf.GetProperty("columns"),
            InfFixedColumns,
            [
                (setup.MoneyFieldId, "自訂金額", "money"),
                (setup.TextFieldId, "自訂文字", "text"),
                (setup.DateFieldId, "自訂日期", "date")
            ]);

        var infRows = inf.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, infRows.Length);
        AssertCustomKeys(infRows[0], setup.MoneyFieldId, setup.TextFieldId, setup.DateFieldId);
        Assert.Equal(12.34m, infRows[0].GetProperty("customValues").GetProperty(setup.MoneyFieldId).GetDecimal());
        Assert.Equal(JsonValueKind.String, infRows[0].GetProperty("customValues").GetProperty(setup.TextFieldId).ValueKind);
        Assert.Equal(JsonValueKind.String, infRows[0].GetProperty("customValues").GetProperty(setup.DateFieldId).ValueKind);
        AssertCustomKeys(infRows[1], setup.MoneyFieldId, setup.TextFieldId, setup.DateFieldId);
        Assert.All(
            infRows[1].GetProperty("customValues").EnumerateObject(),
            property => Assert.Equal(JsonValueKind.Null, property.Value.ValueKind));
    }

    [Fact]
    public async Task InfPage_LatestValidationRunIsStale_FailsBeforePairingOldRunWithCurrentSql()
    {
        using var host = new HandlerTestHost();
        var setup = await SetupRdeProjectAsync(host);
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await new LocalRuleRunStore(database).SaveAsync(
            setup.ProjectId,
            new RuleRunRecord(
                "stale-validation-run",
                RuleRunKinds.Validate,
                DateTimeOffset.UtcNow.AddMinutes(1),
                """{"resultRef":{"logicVersion":"validation-stale"}}"""),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("query.infSamplePage", JsonSerializer.Serialize(new { pageSize = 10 })));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
    }

    private static async Task<RdeProjectSetup> SetupRdeProjectAsync(HandlerTestHost host)
    {
        InlineGlWorkbookBuilder? builder = null;
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            configured =>
            {
                builder = configured
                    .WithColumns(
                        "傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標",
                        "自訂文字", "自訂日期", "自訂金額")
                    .AddRow(
                        "DYN-001", "2025-04-01", "1100", "Synthetic debit", "row one", 100m, "1",
                        "alpha", "2025-04-02", 12.34m)
                    .AddRow(
                        "DYN-002", "2025-04-03", "2100", "Synthetic credit", "row two", 100m, "0",
                        null, null, null);
            },
            validateForDownstream: true);

        var committed = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = builder!.BuildFlagModeMapping(),
            amountMode = "flag",
            manualAutoPolicy = new
            {
                manualValues = new[] { "true", "1" },
                automaticValues = new[] { "false", "0" }
            },
            // committed ordinal deliberately differs from alphabetical/type order.
            rdeFields = new object[]
            {
                new { sourceColumn = "自訂金額", label = "自訂金額", valueType = "money" },
                new { sourceColumn = "自訂文字", label = "自訂文字", valueType = "text" },
                new { sourceColumn = "自訂日期", label = "自訂日期", valueType = "date" }
            }
        }));
        var fields = committed.GetProperty("rdeFields").EnumerateArray().ToDictionary(
            field => field.GetProperty("sourceColumn").GetString()!,
            field => field.GetProperty("fieldId").GetString()!,
            StringComparer.Ordinal);

        var validation = await host.DispatchAsync("validate.run");
        Assert.True(
            validation.GetProperty("completenessTest").GetProperty("eligibility").GetProperty("isEligible").GetBoolean());

        return new RdeProjectSetup(
            projectId,
            fields["自訂金額"],
            fields["自訂文字"],
            fields["自訂日期"]);
    }

    private static void AssertColumns(
        JsonElement actual,
        IReadOnlyList<(string Key, string Label, string ValueType)> fixedColumns,
        IReadOnlyList<(string Key, string Label, string ValueType)> customColumns)
    {
        var expected = fixedColumns
            .Select(column => (column.Key, column.Label, column.ValueType, IsCustom: false))
            .Concat(customColumns.Select(column => (column.Key, column.Label, column.ValueType, IsCustom: true)))
            .ToArray();
        var columns = actual.EnumerateArray().ToArray();
        Assert.Equal(expected.Length, columns.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            // 2026-09-07 工作包 G：欄位定義多帶 sortable，固定欄可排序、額外欄位不能（第一次失敗：收據 20260907-082210786）。
            JsonShape.HasExactKeys(columns[index], "key", "label", "valueType", "isCustom", "sortable");
            Assert.Equal(expected[index].Key, columns[index].GetProperty("key").GetString());
            Assert.Equal(expected[index].Label, columns[index].GetProperty("label").GetString());
            Assert.Equal(expected[index].ValueType, columns[index].GetProperty("valueType").GetString());
            Assert.Equal(expected[index].IsCustom, columns[index].GetProperty("isCustom").GetBoolean());
            Assert.Equal(!expected[index].IsCustom, columns[index].GetProperty("sortable").GetBoolean());
        }
    }

    private static void AssertCustomKeys(JsonElement row, params string[] expected)
    {
        var actual = row.GetProperty("customValues").EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected.OrderBy(static key => key, StringComparer.Ordinal).ToArray(), actual);
    }

    private sealed record RdeProjectSetup(
        string ProjectId,
        string MoneyFieldId,
        string TextFieldId,
        string DateFieldId);
}
