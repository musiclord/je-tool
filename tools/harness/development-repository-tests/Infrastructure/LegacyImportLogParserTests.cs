using System.Text;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyImportLogParserTests
{
    [Fact]
    public void StructuralFingerprint_ContainsOnlyAggregateShapeAndKnownMarkerCount()
    {
        var content = Big5(
            "Set task = Client.GetImportTask(\"ImportExcel\")\r\n" +
            "task.FileToImport = \"synthetic-sensitive-source.xlsx\"\r\n" +
            "task.SheetToImport = \"synthetic-sensitive-sheet\"\r\n" +
            "task.FirstRowIsFieldName = \"TRUE\"\r\n" +
            "task.PerformTask\r\n");

        var fingerprint = LegacyImportLogParser.StructuralFingerprint(content);

        Assert.Equal(
            "V2-L5-E4-O0-T0-C0-S0-N0-Q4-A3-K5-I1-F1-H1-R1-P1-U0-Z0-B5-D3",
            fingerprint);
        Assert.DoesNotContain("synthetic", fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain("xlsx", fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ClosedBig5ImportTask_ReturnsRedactedAggregateResult()
    {
        var result = LegacyImportLogParser.Parse(Big5(ValidLog()));

        Assert.Equal("journal-source.xlsx", result.SourceFileName);
        Assert.Equal("總帳", result.WorksheetName);
        Assert.True(result.SourceFilePresent);
        Assert.True(result.WorksheetPresent);
        Assert.True(result.FirstRowIsFieldNames);
        Assert.Equal(2, result.FieldBlockCount);
        Assert.True(result.FieldBlocksComplete);
        Assert.True(result.TaskClosed);
    }

    [Fact]
    public void Parse_ClosedIdeaImportExcelTask_ReturnsOnlyRedactedAggregateResult()
    {
        var result = LegacyImportLogParser.Parse(Big5(IdeaImportLog()));

        Assert.Equal("journal-source.xlsx", result.SourceFileName);
        Assert.Equal("GL-SHEET", result.WorksheetName);
        Assert.True(result.FirstRowIsFieldNames);
        Assert.Equal(5, result.FieldBlockCount);
        Assert.True(result.FieldBlocksComplete);
        Assert.True(result.TaskClosed);

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("synthetic-sensitive", json, StringComparison.Ordinal);
        Assert.DoesNotContain("journal-source.xlsx", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GL-SHEET", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_IdeaExternalSourceIdentifier_RequiresOneWorkbookLiteralFallback()
    {
        var external = IdeaImportLog().Replace(
            "dbName = \"C:\\synthetic-sensitive-root\\journal-source.xlsx\"",
            "' workbook = \"C:\\synthetic-sensitive-root\\journal-source.xlsx\"",
            StringComparison.Ordinal);

        var result = LegacyImportLogParser.Parse(Big5(external));

        Assert.Equal("journal-source.xlsx", result.SourceFileName);
        Assert.Equal("GL-SHEET", result.WorksheetName);

        var ambiguous = external.Replace(
            "' workbook = \"C:\\synthetic-sensitive-root\\journal-source.xlsx\"",
            "' workbook = \"C:\\synthetic-sensitive-root\\journal-source.xlsx\"\r\n" +
            "' second = \"C:\\synthetic-sensitive-root\\balance-source.xlsx\"",
            StringComparison.Ordinal);
        var exception = Assert.Throws<LegacyImportLogParseException>(() =>
            LegacyImportLogParser.Parse(Big5(ambiguous)));
        Assert.Equal("IdeaWorkbookLiteralInventory", exception.DiagnosticCode);
        Assert.DoesNotContain("synthetic-sensitive", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown-property", "IdeaPropertyInventory")]
    [InlineData("duplicate-task", "IdeaStartInventory")]
    [InlineData("missing-perform", "IdeaAssignmentTarget")]
    [InlineData("reordered-property", "IdeaClosedTask")]
    [InlineData("duplicate-local", "IdeaAssignmentTarget")]
    public void Parse_IdeaImportExcelTaskWithOpenOrUnknownGrammar_FailsClosed(
        string mutation,
        string expectedDiagnosticCode)
    {
        var content = mutation switch
        {
            "unknown-property" => IdeaImportLog().Replace(
                "task.OutputFilePrefix = \"synthetic-output\"",
                "task.UnknownProperty = \"synthetic-sensitive\"",
                StringComparison.Ordinal),
            "duplicate-task" => IdeaImportLog()
                + "Set otherTask = Client.GetImportTask(\"ImportExcel\")\r\n",
            "missing-perform" => IdeaImportLog().Replace(
                "task.PerformTask\r\n",
                string.Empty,
                StringComparison.Ordinal),
            "reordered-property" => IdeaImportLog().Replace(
                "task.OutputFilePrefix = \"synthetic-output\"\r\n" +
                "task.FirstRowIsFieldName = \"TRUE\"\r\n",
                "task.FirstRowIsFieldName = \"TRUE\"\r\n" +
                "task.OutputFilePrefix = \"synthetic-output\"\r\n",
                StringComparison.Ordinal),
            "duplicate-local" => IdeaImportLog().Replace(
                "localResult = task.OutputFilePath(\"synthetic-output\")\r\n",
                "localResult = task.OutputFilePath(\"synthetic-output\")\r\n" +
                "secondResult = task.OutputFilePath(\"synthetic-output\")\r\n",
                StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };

        var exception = Assert.Throws<LegacyImportLogParseException>(() =>
            LegacyImportLogParser.Parse(Big5(content)));

        Assert.Equal(LegacyImportLogFailure.InvalidGrammar, exception.Failure);
        Assert.Equal(expectedDiagnosticCode, exception.DiagnosticCode);
        Assert.DoesNotContain("synthetic-sensitive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_InvalidBig5ByteSequence_FailsAsEncodingWithoutEchoingBytes()
    {
        byte[] invalidBig5 = [0x81, 0x30];

        var exception = Assert.Throws<LegacyImportLogParseException>(() =>
            LegacyImportLogParser.Parse(invalidBig5));

        Assert.Equal(LegacyImportLogFailure.InvalidEncoding, exception.Failure);
        Assert.DoesNotContain("81", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(InvalidGrammarCases))]
    public void Parse_OpenDuplicateUnknownOrOutOfOrderTask_FailsClosed(
        string content,
        LegacyImportLogFailure expectedFailure)
    {
        var exception = Assert.Throws<LegacyImportLogParseException>(() =>
            LegacyImportLogParser.Parse(Big5(content)));

        Assert.Equal(expectedFailure, exception.Failure);
        Assert.DoesNotContain("synthetic-sensitive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FieldValuesAreNotRetainedAndOnlyCompleteAggregateCountIsSerialized()
    {
        var result = LegacyImportLogParser.Parse(Big5(ValidLog(
            sourceFile: @"C:\synthetic-sensitive-root\private-source.xlsx",
            worksheet: "synthetic-sensitive-sheet",
            firstFieldName: "synthetic-sensitive-field-one",
            secondFieldName: "synthetic-sensitive-field-two")));

        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("synthetic-sensitive", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-source.xlsx", json, StringComparison.Ordinal);
        Assert.DoesNotContain("field-one", json, StringComparison.Ordinal);
        Assert.Contains("FieldBlockCount", json, StringComparison.Ordinal);
        Assert.Contains(":2", json, StringComparison.Ordinal);
        Assert.Contains("FieldBlocksComplete", json, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-sensitive", result.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<string, LegacyImportLogFailure> InvalidGrammarCases => new()
    {
        {
            ValidLog().Replace("task.end=success\r\n", string.Empty, StringComparison.Ordinal),
            LegacyImportLogFailure.InvalidGrammar
        },
        {
            ValidLog().Replace(
                "source.sheet=總帳\r\n",
                "source.sheet=總帳\r\nsource.sheet=synthetic-sensitive-duplicate\r\n",
                StringComparison.Ordinal),
            LegacyImportLogFailure.InvalidGrammar
        },
        {
            ValidLog().Replace(
                "source.sheet=總帳\r\n",
                "source.sheet=總帳\r\nsource.unknown=synthetic-sensitive-value\r\n",
                StringComparison.Ordinal),
            LegacyImportLogFailure.InvalidGrammar
        },
        {
            ValidLog().Replace(
                "field.2.name=金額\r\nfield.2.type=numeric\r\n",
                "field.3.name=synthetic-sensitive-gap\r\nfield.3.type=numeric\r\n",
                StringComparison.Ordinal),
            LegacyImportLogFailure.IncompleteFieldBlocks
        },
        {
            ValidLog().Replace("field.2.type=numeric\r\n", string.Empty, StringComparison.Ordinal),
            LegacyImportLogFailure.IncompleteFieldBlocks
        },
        {
            ValidLog().Replace("field.count=2", "field.count=3", StringComparison.Ordinal),
            LegacyImportLogFailure.IncompleteFieldBlocks
        },
        {
            ValidLog().Replace(
                "task.begin=import_excel\r\nsource.file=journal-source.xlsx\r\n",
                "source.file=journal-source.xlsx\r\ntask.begin=import_excel\r\n",
                StringComparison.Ordinal),
            LegacyImportLogFailure.InvalidGrammar
        },
    };

    private static string ValidLog(
        string sourceFile = "journal-source.xlsx",
        string worksheet = "總帳",
        string firstFieldName = "傳票編號",
        string secondFieldName = "金額") =>
        $"task.begin=import_excel\r\n" +
        $"source.file={sourceFile}\r\n" +
        $"source.sheet={worksheet}\r\n" +
        "source.first_row_is_field_names=true\r\n" +
        "field.count=2\r\n" +
        $"field.1.name={firstFieldName}\r\n" +
        "field.1.type=character\r\n" +
        $"field.2.name={secondFieldName}\r\n" +
        "field.2.type=numeric\r\n" +
        "task.end=success\r\n";

    private static string IdeaImportLog() =>
        "' synthetic IDEA recorder header\r\n" +
        "Sub Main\r\n" +
        "dbName = \"C:\\synthetic-sensitive-root\\journal-source.xlsx\"\r\n" +
        "Set task = Client.GetImportTask(\"ImportExcel\")\r\n" +
        "task.FileToImport = dbName\r\n" +
        "task.SheetToImport = \"GL-SHEET\"\r\n" +
        "task.OutputFilePrefix = \"synthetic-output\"\r\n" +
        "task.FirstRowIsFieldName = \"TRUE\"\r\n" +
        "task.EmptyNumericFieldAsZero = \"FALSE\"\r\n" +
        "localResult = task.OutputFilePath(\"synthetic-output\")\r\n" +
        "' perform the closed task\r\n" +
        "task.PerformTask\r\n" +
        "Set task = Nothing\r\n" +
        "End Sub\r\n";

    private static byte[] Big5(string content)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            950,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback).GetBytes(content);
    }
}
