using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class ResultPageColumnRegistryTests
{
    private const string MoneyField = "rde.00000000000000000000000000000001";
    private const string TextField = "rde.00000000000000000000000000000002";
    private const string DateField = "rde.00000000000000000000000000000003";

    [Fact]
    public void NestedVoucherFieldsRemainAvailableToPagesAndReports()
    {
        var scenario = Scenario(new { type = "voucher", side = "credit", quantifier = "all", rules = new[] {
            new { type = "group", rules = new[] { new { type = "fieldValue", fieldId = TextField, @operator = "equals", value = "x" } } }
        } });
        var plan = ResultPageColumnRegistry.ForFilter(scenario, Mapping(), 100);
        Assert.Equal(new[] { TextField }, plan.CustomFields.Select(field => field.FieldId));
        var metadata = new ReportWorkbookMetadata("2025-01-01", "2025-12-31", 1, Mapping(), null);
        Assert.Equal(new[] { TextField }, FormalReportRdeFieldSelection.ForScenarios([scenario], metadata, 100).Select(field => field.FieldId));
    }

    [Fact]
    public void ForFilter_UsesTypedFieldUnionInCommittedOrdinal()
    {
        var plan = ResultPageColumnRegistry.ForFilter(
            Scenario(
                new { type = "typed", fieldId = DateField, @operator = "isNotBlank" },
                new { type = "typed", fieldId = TextField, @operator = "contains", value = "x" },
                new { type = "typed", fieldId = TextField, @operator = "isBlank" }),
            Mapping(),
            100);

        Assert.Equal(
            [TextField, DateField],
            plan.CustomFields.Select(static field => field.FieldId).ToArray());
        Assert.Equal(
            [TextField, DateField],
            plan.Columns.Where(static column => column.IsCustom).Select(static column => column.Key).ToArray());
    }

    [Fact]
    public void ForFilter_RemovedOrTypeMismatchedTypedField_FailsClosed()
    {
        var removed = Assert.Throws<JetActionException>(() => ResultPageColumnRegistry.ForFilter(
            Scenario(new { type = "typed", fieldId = "rde.ffffffffffffffffffffffffffffffff", @operator = "isBlank" }),
            Mapping(),
            100));
        var mismatched = Assert.Throws<JetActionException>(() => ResultPageColumnRegistry.ForFilter(
            Scenario(new { type = "typed", fieldId = MoneyField, @operator = "contains", value = "1" }),
            Mapping(),
            100));

        Assert.Equal(JetErrorCodes.StaleResult, removed.Code);
        Assert.Equal(JetErrorCodes.StaleResult, mismatched.Code);
    }

    [Fact]
    public void Renderer_EmitsNativeValuesExactKeysAndExplicitNulls()
    {
        var plan = ResultPageColumnRegistry.ForInf(Mapping());

        var rendered = ResultPageCustomValueRenderer.Render(
            [10, 11],
            [
                new ResultPageRdeValue(10, MoneyField, "money", null, null, 1234),
                new ResultPageRdeValue(10, TextField, "text", "alpha", null, null),
                new ResultPageRdeValue(10, DateField, "date", null, "2025-04-02", null)
            ],
            plan,
            100);

        Assert.Equal([MoneyField, TextField, DateField], rendered[10].Keys.ToArray());
        Assert.Equal(12.34m, Assert.IsType<decimal>(rendered[10][MoneyField]));
        Assert.Equal("alpha", Assert.IsType<string>(rendered[10][TextField]));
        Assert.Equal("2025-04-02", Assert.IsType<string>(rendered[10][DateField]));
        Assert.All(rendered[11].Values, Assert.Null);
    }

    [Theory]
    [InlineData("rde.ffffffffffffffffffffffffffffffff", "text")]
    [InlineData(TextField, "date")]
    public void Renderer_UnknownOrTypeMismatchedStoredCell_FailsClosed(string fieldId, string valueType)
    {
        var exception = Assert.Throws<JetActionException>(() => ResultPageCustomValueRenderer.Render(
            [10],
            [new ResultPageRdeValue(10, fieldId, valueType, "value", null, null)],
            ResultPageColumnRegistry.ForInf(Mapping()),
            100));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
    }

    private static SavedFilterScenario Scenario(params object[] rules) => new(
        1,
        "scenario",
        "synthetic",
        JsonSerializer.Serialize(new
        {
            name = "scenario",
            rationale = "synthetic",
            groups = new[] { new { join = "AND", rules } }
        }),
        DateTimeOffset.UnixEpoch);

    private static CommittedMapping Mapping() => new(
        DatasetKind.Gl,
        new Dictionary<string, string>(),
        "flag",
        "batch-1",
        DateTimeOffset.UnixEpoch,
        GlOptions: new GlMappingOptions(
            ApprovalDateModeNames.Unmapped,
            null,
            new GlManualAutoPolicy(["1"], ["0"]),
            [
                new GlRdeFieldMetadata(MoneyField, "money-source", "自訂金額", "money"),
                new GlRdeFieldMetadata(TextField, "text-source", "自訂文字", "text"),
                new GlRdeFieldMetadata(DateField, "date-source", "自訂日期", "date")
            ]));
}
