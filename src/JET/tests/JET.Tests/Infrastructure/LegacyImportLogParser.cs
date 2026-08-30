using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JET.Tests.Infrastructure;

public enum LegacyImportLogFailure
{
    InputTooLarge,
    InvalidEncoding,
    InvalidGrammar,
    IncompleteFieldBlocks,
}

internal sealed class LegacyImportLogParseException : InvalidOperationException
{
    internal LegacyImportLogParseException(
        LegacyImportLogFailure failure,
        string message,
        string? diagnosticCode = null)
        : base(message)
    {
        Failure = failure;
        DiagnosticCode = diagnosticCode ?? failure.ToString();
    }

    public LegacyImportLogFailure Failure { get; }

    internal string DiagnosticCode { get; }
}

internal sealed class LegacyImportLogParseResult
{
    internal LegacyImportLogParseResult(
        string sourceFileName,
        string worksheetName,
        bool firstRowIsFieldNames,
        int fieldBlockCount,
        bool fieldBlocksComplete,
        bool taskClosed)
    {
        SourceFileName = sourceFileName;
        WorksheetName = worksheetName;
        FirstRowIsFieldNames = firstRowIsFieldNames;
        FieldBlockCount = fieldBlockCount;
        FieldBlocksComplete = fieldBlocksComplete;
        TaskClosed = taskClosed;
    }

    [JsonIgnore]
    public string SourceFileName { get; }

    [JsonIgnore]
    public string WorksheetName { get; }

    public bool SourceFilePresent => SourceFileName.Length > 0;

    public bool WorksheetPresent => WorksheetName.Length > 0;

    public bool FirstRowIsFieldNames { get; }

    public int FieldBlockCount { get; }

    public bool FieldBlocksComplete { get; }

    public bool TaskClosed { get; }

    public override string ToString() => "legacy import log result (redacted)";
}

internal static partial class LegacyImportLogParser
{
    private const int MaximumByteCount = 4 * 1024 * 1024;
    private const int MaximumFieldCount = 4096;
    private static readonly string[] IdeaImportProperties =
    [
        "FileToImport",
        "SheetToImport",
        "OutputFilePrefix",
        "FirstRowIsFieldName",
        "EmptyNumericFieldAsZero",
    ];

    internal static LegacyImportLogParseResult Parse(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0 || content.Length > MaximumByteCount)
        {
            throw Error(
                LegacyImportLogFailure.InputTooLarge,
                "Legacy import log must be non-empty and within the bounded size limit.");
        }

        string text;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var strictBig5 = Encoding.GetEncoding(
                950,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            text = strictBig5.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw Error(
                LegacyImportLogFailure.InvalidEncoding,
                "Legacy import log is not valid Big5 text.");
        }

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        return lines.Length > 0
            && lines[0].StartsWith("task.begin", StringComparison.OrdinalIgnoreCase)
            ? ParseCanonicalTask(lines)
            : ParseIdeaImportTask(lines);
    }

    private static LegacyImportLogParseResult ParseCanonicalTask(IReadOnlyList<string> lines)
    {
        var position = 0;
        ReadExact(lines, ref position, "task.begin", "import_excel", LegacyImportLogFailure.InvalidGrammar);
        var sourceFile = ReadNonEmpty(lines, ref position, "source.file", LegacyImportLogFailure.InvalidGrammar);
        var worksheet = ReadNonEmpty(lines, ref position, "source.sheet", LegacyImportLogFailure.InvalidGrammar);
        var firstRowValue = ReadNonEmpty(
            lines,
            ref position,
            "source.first_row_is_field_names",
            LegacyImportLogFailure.InvalidGrammar);
        if (!bool.TryParse(firstRowValue, out var firstRowIsFieldNames))
        {
            throw Error(
                LegacyImportLogFailure.InvalidGrammar,
                "Legacy import log first-row flag is invalid.");
        }

        var fieldCountValue = ReadNonEmpty(
            lines,
            ref position,
            "field.count",
            LegacyImportLogFailure.IncompleteFieldBlocks);
        if (!int.TryParse(
                fieldCountValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var fieldCount)
            || fieldCount is < 1 or > MaximumFieldCount)
        {
            throw Error(
                LegacyImportLogFailure.IncompleteFieldBlocks,
                "Legacy import log field count is invalid.");
        }

        for (var fieldIndex = 1; fieldIndex <= fieldCount; fieldIndex++)
        {
            _ = ReadNonEmpty(
                lines,
                ref position,
                $"field.{fieldIndex.ToString(CultureInfo.InvariantCulture)}.name",
                LegacyImportLogFailure.IncompleteFieldBlocks);
            _ = ReadNonEmpty(
                lines,
                ref position,
                $"field.{fieldIndex.ToString(CultureInfo.InvariantCulture)}.type",
                LegacyImportLogFailure.IncompleteFieldBlocks);
        }

        ReadExact(lines, ref position, "task.end", "success", LegacyImportLogFailure.InvalidGrammar);
        if (position != lines.Count)
        {
            throw Error(
                LegacyImportLogFailure.InvalidGrammar,
                "Legacy import log contains data outside its closed task.");
        }

        var sourceFileName = Path.GetFileName(sourceFile);
        if (string.IsNullOrWhiteSpace(sourceFileName))
        {
            throw Error(
                LegacyImportLogFailure.InvalidGrammar,
                "Legacy import log source file is invalid.");
        }

        return new LegacyImportLogParseResult(
            sourceFileName,
            worksheet,
            firstRowIsFieldNames,
            fieldCount,
            fieldBlocksComplete: true,
            taskClosed: true);
    }

    private static LegacyImportLogParseResult ParseIdeaImportTask(IReadOnlyList<string> lines)
    {
        var starts = lines
            .Select((line, index) => (Line: line, Index: index, Match: IdeaImportStartRegex().Match(line)))
            .Where(static item => item.Match.Success)
            .ToArray();
        if (starts.Length != 1)
        {
            throw Error(
                LegacyImportLogFailure.InvalidGrammar,
                "Legacy IDEA import log must contain exactly one ImportExcel task.",
                "IdeaStartInventory");
        }

        var start = starts[0];
        var variable = start.Match.Groups["variable"].Value;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var propertyOrder = new List<string>();
        var localAssignmentCount = 0;
        var endIndex = -1;
        for (var index = start.Index + 1; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.StartsWith("'", StringComparison.Ordinal))
            {
                continue;
            }
            if (string.Equals(line, $"{variable}.PerformTask", StringComparison.OrdinalIgnoreCase))
            {
                endIndex = index;
                break;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw Error(
                    LegacyImportLogFailure.InvalidGrammar,
                    "Legacy IDEA import task contains an invalid statement.",
                    "IdeaBlockStatement");
            }
            var left = line[..separator].Trim();
            var prefix = variable + ".";
            if (!left.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var expression = line[(separator + 1)..].Trim();
                if (localAssignmentCount == 0
                    && IdeaIdentifierRegex().IsMatch(left)
                    && IsSafeIdeaLocalExpression(expression, variable))
                {
                    localAssignmentCount++;
                    continue;
                }
                throw Error(
                    LegacyImportLogFailure.InvalidGrammar,
                    "Legacy IDEA import task contains an invalid assignment target.",
                    "IdeaAssignmentTarget");
            }
            var property = left[prefix.Length..].Trim();
            if (!IdeaImportProperties.Contains(property, StringComparer.OrdinalIgnoreCase)
                || !properties.TryAdd(property, line[(separator + 1)..].Trim()))
            {
                throw Error(
                    LegacyImportLogFailure.InvalidGrammar,
                    "Legacy IDEA import task property inventory is invalid.",
                    "IdeaPropertyInventory");
            }
            propertyOrder.Add(property);
        }

        if (endIndex < 0
            || properties.Count != IdeaImportProperties.Length
            || !propertyOrder.SequenceEqual(IdeaImportProperties, StringComparer.OrdinalIgnoreCase)
            || lines.Skip(endIndex + 1).Any(line => IdeaImportStartRegex().IsMatch(line)))
        {
            throw Error(
                LegacyImportLogFailure.InvalidGrammar,
                "Legacy IDEA import task is not a single closed task.",
                "IdeaClosedTask");
        }

        var source = ResolveIdeaSource(
            properties["FileToImport"],
            lines.Take(start.Index),
            lines);
        var worksheet = ResolveIdeaScalar(
            properties["SheetToImport"],
            [],
            allowIdentifier: false);
        _ = ResolveIdeaScalar(
            properties["OutputFilePrefix"],
            [],
            allowIdentifier: false);
        var firstRow = ResolveIdeaBoolean(properties["FirstRowIsFieldName"]);
        _ = ResolveIdeaBoolean(properties["EmptyNumericFieldAsZero"]);
        var sourceFileName = Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(sourceFileName)
            || string.IsNullOrWhiteSpace(worksheet))
        {
            throw Error(
                LegacyImportLogFailure.InvalidGrammar,
                "Legacy IDEA import task contains an invalid source binding.",
                "IdeaSourceBinding");
        }

        return new LegacyImportLogParseResult(
            sourceFileName,
            worksheet,
            firstRow,
            IdeaImportProperties.Length,
            fieldBlocksComplete: true,
            taskClosed: true);
    }

    private static string ResolveIdeaScalar(
        string expression,
        IEnumerable<string> precedingLines,
        bool allowIdentifier)
    {
        if (TryUnquote(expression, out var value))
        {
            return value;
        }
        if (!allowIdentifier || !IdeaIdentifierRegex().IsMatch(expression))
        {
            throw Error(
                LegacyImportLogFailure.InvalidGrammar,
                "Legacy IDEA import task scalar is invalid.",
                "IdeaScalar");
        }

        var assignments = precedingLines
            .Where(line => !line.StartsWith("'", StringComparison.Ordinal))
            .Select(line => IdeaScalarAssignmentRegex().Match(line))
            .Where(match => match.Success
                && string.Equals(match.Groups["variable"].Value, expression, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (assignments.Length != 1
            || !TryUnquote(assignments[0].Groups["value"].Value, out value))
        {
            throw Error(
                LegacyImportLogFailure.InvalidGrammar,
                "Legacy IDEA import task scalar binding is invalid.",
                "IdeaScalarBinding");
        }
        return value;
    }

    private static string ResolveIdeaSource(
        string expression,
        IEnumerable<string> precedingLines,
        IEnumerable<string> allLines)
    {
        try
        {
            return ResolveIdeaScalar(expression, precedingLines, allowIdentifier: true);
        }
        catch (LegacyImportLogParseException exception)
            when (string.Equals(
                exception.DiagnosticCode,
                "IdeaScalarBinding",
                StringComparison.Ordinal))
        {
            var workbookLiterals = allLines
                .SelectMany(line => IdeaWorkbookLiteralRegex().Matches(line).Cast<Match>())
                .Select(match => match.Groups["value"].Value.Replace("\"\"", "\"", StringComparison.Ordinal))
                .ToArray();
            if (workbookLiterals.Length != 1)
            {
                throw Error(
                    LegacyImportLogFailure.InvalidGrammar,
                    "Legacy IDEA import task workbook literal inventory is invalid.",
                    "IdeaWorkbookLiteralInventory");
            }
            return workbookLiterals[0];
        }
    }

    private static bool ResolveIdeaBoolean(string expression)
    {
        if (!TryUnquote(expression, out var value)
            || !bool.TryParse(value, out var result))
        {
            throw Error(
                LegacyImportLogFailure.InvalidGrammar,
                "Legacy IDEA import task boolean is invalid.",
                "IdeaBoolean");
        }
        return result;
    }

    private static bool TryUnquote(string expression, out string value)
    {
        value = string.Empty;
        if (expression.Length < 2 || expression[0] != '"' || expression[^1] != '"')
        {
            return false;
        }
        value = expression[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        return value.Length > 0;
    }

    private static bool IsSafeIdeaLocalExpression(string expression, string taskVariable)
    {
        if (TryUnquote(expression, out _))
        {
            return true;
        }
        var prefix = taskVariable + ".";
        return expression.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && expression.Length > prefix.Length
            && !expression.Contains(';')
            && !expression.Contains('\r')
            && !expression.Contains('\n');
    }

    internal static string StructuralFingerprint(ReadOnlySpan<byte> content)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string text;
        try
        {
            text = Encoding.GetEncoding(
                950,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return "V1-EncodingInvalid";
        }

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToArray();
        var equals = lines.Count(static line => line.Contains('='));
        var colon = lines.Count(static line => line.Contains(':'));
        var tab = lines.Count(static line => line.Contains('\t'));
        var comma = lines.Count(static line => line.Contains(','));
        var semicolon = lines.Count(static line => line.Contains(';'));
        var nul = text.Count(static character => character == '\0');
        var quoted = lines.Count(static line => line.Contains('"'));
        var propertyAssignments = lines.Count(static line =>
        {
            var separator = line.IndexOf('=');
            return separator > 0 && line[..separator].Contains('.');
        });
        var knownIdeaMarkers = lines.Count(static line =>
            line.Contains("GetImportTask", StringComparison.OrdinalIgnoreCase)
            || line.Contains("FileToImport", StringComparison.OrdinalIgnoreCase)
            || line.Contains("SheetToImport", StringComparison.OrdinalIgnoreCase)
            || line.Contains("FirstRowIsFieldName", StringComparison.OrdinalIgnoreCase)
            || line.Contains("PerformTask", StringComparison.OrdinalIgnoreCase));
        var importTask = lines.Count(static line =>
            line.Contains("GetImportTask", StringComparison.OrdinalIgnoreCase)
            && line.Contains("ImportExcel", StringComparison.OrdinalIgnoreCase));
        var fileProperty = lines.Count(static line =>
            line.Contains("FileToImport", StringComparison.OrdinalIgnoreCase));
        var sheetProperty = lines.Count(static line =>
            line.Contains("SheetToImport", StringComparison.OrdinalIgnoreCase));
        var firstRowProperty = lines.Count(static line =>
            line.Contains("FirstRowIsFieldName", StringComparison.OrdinalIgnoreCase));
        var performTask = lines.Count(static line =>
            line.Contains("PerformTask", StringComparison.OrdinalIgnoreCase));
        var outputPrefix = lines.Count(static line =>
            line.Contains("OutputFilePrefix", StringComparison.OrdinalIgnoreCase));
        var emptyNumeric = lines.Count(static line =>
            line.Contains("EmptyNumericFieldAsZero", StringComparison.OrdinalIgnoreCase));
        var startIndex = Array.FindIndex(lines, static line =>
            line.Contains("GetImportTask", StringComparison.OrdinalIgnoreCase)
            && line.Contains("ImportExcel", StringComparison.OrdinalIgnoreCase));
        var endIndex = startIndex < 0
            ? -1
            : Array.FindIndex(lines, startIndex + 1, static line =>
                line.Contains("PerformTask", StringComparison.OrdinalIgnoreCase));
        var taskBlockLineCount = startIndex >= 0 && endIndex >= startIndex
            ? endIndex - startIndex + 1
            : 0;
        var taskBlockAssignments = taskBlockLineCount == 0
            ? 0
            : lines.Skip(startIndex).Take(taskBlockLineCount).Count(static line =>
            {
                var separator = line.IndexOf('=');
                return separator > 0 && line[..separator].Contains('.');
            });

        return string.Create(
            CultureInfo.InvariantCulture,
            $"V2-L{lines.Length}-E{equals}-O{colon}-T{tab}-C{comma}-S{semicolon}-N{nul}-Q{quoted}-A{propertyAssignments}-K{knownIdeaMarkers}-I{importTask}-F{fileProperty}-H{sheetProperty}-R{firstRowProperty}-P{performTask}-U{outputPrefix}-Z{emptyNumeric}-B{taskBlockLineCount}-D{taskBlockAssignments}");
    }

    private static string ReadNonEmpty(
        IReadOnlyList<string> lines,
        ref int position,
        string expectedKey,
        LegacyImportLogFailure failure)
    {
        var value = ReadValue(lines, ref position, expectedKey, failure);
        if (value.Length == 0)
        {
            throw Error(failure, "Legacy import log contains an empty required value.");
        }

        return value;
    }

    private static void ReadExact(
        IReadOnlyList<string> lines,
        ref int position,
        string expectedKey,
        string expectedValue,
        LegacyImportLogFailure failure)
    {
        var value = ReadValue(lines, ref position, expectedKey, failure);
        if (!string.Equals(value, expectedValue, StringComparison.OrdinalIgnoreCase))
        {
            throw Error(failure, "Legacy import log task marker is invalid.");
        }
    }

    private static string ReadValue(
        IReadOnlyList<string> lines,
        ref int position,
        string expectedKey,
        LegacyImportLogFailure failure)
    {
        if (position >= lines.Count)
        {
            throw Error(failure, "Legacy import log ended before its required structure was complete.");
        }

        var line = lines[position++];
        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            throw Error(failure, "Legacy import log contains an invalid key-value line.");
        }

        var key = line[..separator].Trim();
        if (!string.Equals(key, expectedKey, StringComparison.OrdinalIgnoreCase))
        {
            throw Error(failure, "Legacy import log key order or allowlist is invalid.");
        }

        return line[(separator + 1)..].Trim();
    }

    private static LegacyImportLogParseException Error(
        LegacyImportLogFailure failure,
        string message,
        string? diagnosticCode = null) => new(failure, message, diagnosticCode);

    [GeneratedRegex(
        "^Set\\s+(?<variable>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*Client\\.GetImportTask\\(\\s*\"ImportExcel\"\\s*\\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdeaImportStartRegex();

    [GeneratedRegex(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdeaIdentifierRegex();

    [GeneratedRegex(
        "^(?<variable>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(?<value>\"(?:[^\"]|\"\")+\")$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IdeaScalarAssignmentRegex();

    [GeneratedRegex(
        "\"(?<value>(?:[^\"]|\"\")+\\.(?:xlsx|xls))\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdeaWorkbookLiteralRegex();
}
