using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JET.Tests.Infrastructure;

internal static class LegacyAppearanceRegistry
{
    public const string ScriptRelativePath = "legacy/idea-script.bas";
    public const string ToolRelativePath = "legacy/idea-tool.bas";
    public const string FixtureRelativePath =
        "src/JET/tests/JET.Tests/Infrastructure/Fixtures/legacy-appearance-registry.json";

    public static IReadOnlyList<string> ProcedureNames { get; } =
    [
        "Step1_Export_Excel",
        "Step1_Export_INF_Report",
        "Step3_Export_Excel",
        "Step4_Export_Excel",
        "Step5_Export_Excel_TW"
    ];

    private static readonly Regex AppearancePropertyPattern = new(
        @"\.Font\.(?:Name|Size|Bold|Italic|Underline|ColorIndex|Color)\b|" +
        @"\.Interior\.(?:Color|ColorIndex|Pattern)\b|" +
        @"\.Borders(?:\([^\)]*\)|\[[^\]]*\])?\.(?:LineStyle|Weight|ColorIndex|Color)\b|" +
        @"\.(?:HorizontalAlignment|VerticalAlignment|WrapText|IndentLevel|ShrinkToFit)\b|" +
        @"\.NumberFormat(?:Local)?\b|" +
        @"\.(?:EntireColumn|EntireRow)\.AutoFit\b|" +
        @"\.(?:ColumnWidth|RowHeight)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VbaRgbPattern = new(
        @"^RGB\(\s*(?<red>\d+)\s*,\s*(?<green>\d+)\s*,\s*(?<blue>\d+)\s*\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static LegacyAppearanceRegistryDocument Load()
    {
        var fixturePath = Path.Combine(
            TestRepositoryPaths.RepositoryRoot,
            FixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var json = File.ReadAllText(fixturePath, new UTF8Encoding(false, true));
        return JsonSerializer.Deserialize<LegacyAppearanceRegistryDocument>(json, JsonOptions)
            ?? throw new InvalidDataException($"Legacy appearance registry is empty: {fixturePath}");
    }

    public static LegacyScriptSource ReadScript(string relativePath)
    {
        var fullPath = Path.Combine(
            TestRepositoryPaths.RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        var bytes = File.ReadAllBytes(fullPath);

        var hasBom = bytes.Length >= 3 &&
                     bytes[0] == 0xEF &&
                     bytes[1] == 0xBB &&
                     bytes[2] == 0xBF;
        var offset = hasBom ? 3 : 0;
        var text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        return new LegacyScriptSource(relativePath, "UTF-8", hasBom, lines);
    }

    public static IReadOnlyList<LegacyAppearanceCommand> ExtractAppearanceCommands(
        string relativePath)
    {
        var source = ReadScript(relativePath);
        var commands = new List<LegacyAppearanceCommand>();

        foreach (var procedure in ProcedureNames)
        {
            var (startIndex, endIndex) = FindProcedure(source, procedure);
            for (var index = startIndex; index <= endIndex; index++)
            {
                var line = source.Lines[index];
                if (line.TrimStart().StartsWith("'", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var matches = AppearancePropertyPattern.Matches(line);
                foreach (Match match in matches)
                {
                    var property = NormalizeProperty(match.Value);
                    var value = NormalizeValue(line, match, property);
                    commands.Add(new LegacyAppearanceCommand(
                        source.RelativePath,
                        procedure,
                        index + 1,
                        line.Trim(),
                        NormalizeTargetExpression(line[..match.Index]),
                        property,
                        value,
                        $"{procedure}|{NormalizeCommandText(line)}"));
                }
            }
        }

        return commands;
    }

    public static IReadOnlyList<LegacyExportDatabaseCall> ExtractExportDatabaseCalls(
        string relativePath)
    {
        var source = ReadScript(relativePath);
        var calls = new List<LegacyExportDatabaseCall>();

        foreach (var procedure in ProcedureNames)
        {
            var (startIndex, endIndex) = FindProcedure(source, procedure);
            for (var index = startIndex; index <= endIndex; index++)
            {
                var line = source.Lines[index];
                if (line.TrimStart().StartsWith("'", StringComparison.Ordinal) ||
                    !line.Contains("Call Z_ExportDatabaseXLSX", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                calls.Add(new LegacyExportDatabaseCall(
                    source.RelativePath,
                    procedure,
                    index + 1,
                    line.Trim()));
            }
        }

        return calls;
    }

    private static (int StartIndex, int EndIndex) FindProcedure(
        LegacyScriptSource source,
        string procedure)
    {
        var startPattern = new Regex(
            $@"^\s*Function\s+{Regex.Escape(procedure)}\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var startIndex = Array.FindIndex(source.Lines, line => startPattern.IsMatch(line));
        if (startIndex < 0)
        {
            throw new InvalidDataException(
                $"Procedure {procedure} was not found in {source.RelativePath}.");
        }

        for (var index = startIndex + 1; index < source.Lines.Length; index++)
        {
            if (source.Lines[index].TrimStart().StartsWith(
                    "End Function",
                    StringComparison.OrdinalIgnoreCase))
            {
                return (startIndex, index);
            }
        }

        throw new InvalidDataException(
            $"Procedure {procedure} has no End Function in {source.RelativePath}.");
    }

    private static string NormalizeTargetExpression(string target)
    {
        var normalized = target.Trim();
        const string sheetPrefix = "oSheet.";
        if (normalized.StartsWith(sheetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[sheetPrefix.Length..];
        }

        return WhitespacePattern.Replace(normalized, string.Empty);
    }

    private static string NormalizeCommandText(string line) =>
        WhitespacePattern.Replace(line.Trim(), " ").ToUpperInvariant();

    private static string NormalizeProperty(string token)
    {
        var lower = token.ToLowerInvariant();
        if (lower.EndsWith(".font.colorindex", StringComparison.Ordinal) ||
            lower.EndsWith(".font.color", StringComparison.Ordinal))
        {
            return "font.colorArgb";
        }

        if (lower.EndsWith(".font.name", StringComparison.Ordinal))
        {
            return "font.name";
        }

        if (lower.EndsWith(".font.size", StringComparison.Ordinal))
        {
            return "font.size";
        }

        if (lower.EndsWith(".font.bold", StringComparison.Ordinal))
        {
            return "font.bold";
        }

        if (lower.EndsWith(".font.italic", StringComparison.Ordinal))
        {
            return "font.italic";
        }

        if (lower.EndsWith(".font.underline", StringComparison.Ordinal))
        {
            return "font.underline";
        }

        if (lower.EndsWith(".interior.color", StringComparison.Ordinal) ||
            lower.EndsWith(".interior.colorindex", StringComparison.Ordinal))
        {
            return "fill.foregroundArgb";
        }

        if (lower.EndsWith(".interior.pattern", StringComparison.Ordinal))
        {
            return "fill.pattern";
        }

        if (lower.EndsWith(".horizontalalignment", StringComparison.Ordinal))
        {
            return "alignment.horizontal";
        }

        if (lower.EndsWith(".verticalalignment", StringComparison.Ordinal))
        {
            return "alignment.vertical";
        }

        if (lower.EndsWith(".wraptext", StringComparison.Ordinal))
        {
            return "alignment.wrapText";
        }

        if (lower.EndsWith(".indentlevel", StringComparison.Ordinal))
        {
            return "alignment.indent";
        }

        if (lower.EndsWith(".shrinktofit", StringComparison.Ordinal))
        {
            return "alignment.shrinkToFit";
        }

        if (lower.EndsWith(".numberformatlocal", StringComparison.Ordinal))
        {
            // OpenXML stores the effective format code, not the VBA locale-facing setter name.
            // Normalize both legacy spellings to the fingerprint's semantic property.
            return "numberFormat.code";
        }

        if (lower.EndsWith(".numberformat", StringComparison.Ordinal))
        {
            return "numberFormat.code";
        }

        if (lower.EndsWith(".entirecolumn.autofit", StringComparison.Ordinal))
        {
            return "column.autoFit";
        }

        if (lower.EndsWith(".entirerow.autofit", StringComparison.Ordinal))
        {
            return "row.autoFit";
        }

        if (lower.EndsWith(".columnwidth", StringComparison.Ordinal))
        {
            return "column.width";
        }

        if (lower.EndsWith(".rowheight", StringComparison.Ordinal))
        {
            return "row.height";
        }

        if (lower.Contains(".borders", StringComparison.Ordinal))
        {
            return "border." + lower[(lower.LastIndexOf('.') + 1)..];
        }

        throw new InvalidDataException($"Unsupported appearance property: {token}");
    }

    private static string NormalizeValue(string line, Match propertyMatch, string property)
    {
        if (property is "column.autoFit" or "row.autoFit")
        {
            return "true";
        }

        var equalsIndex = line.IndexOf('=', propertyMatch.Index + propertyMatch.Length);
        if (equalsIndex < 0)
        {
            throw new InvalidDataException($"Appearance assignment has no value: {line.Trim()}");
        }

        var value = line[(equalsIndex + 1)..].Trim();
        if (property is "font.colorArgb" or "fill.foregroundArgb")
        {
            if (propertyMatch.Value.EndsWith("ColorIndex", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeColorIndex(value);
            }

            var rgb = VbaRgbPattern.Match(value);
            if (rgb.Success)
            {
                return NormalizeVbaRgb(
                    int.Parse(rgb.Groups["red"].Value, CultureInfo.InvariantCulture),
                    int.Parse(rgb.Groups["green"].Value, CultureInfo.InvariantCulture),
                    int.Parse(rgb.Groups["blue"].Value, CultureInfo.InvariantCulture));
            }
        }

        if (value.Equals("True", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("False", StringComparison.OrdinalIgnoreCase))
        {
            return value.ToLowerInvariant();
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        return WhitespacePattern.Replace(value, " ");
    }

    private static string NormalizeColorIndex(string value) => value.Trim() switch
    {
        "2" => "FFFFFFFF",
        "3" => "FFFF0000",
        _ => throw new InvalidDataException(
            $"The registered legacy commands use an unsupported ColorIndex: {value.Trim()}")
    };

    private static string NormalizeVbaRgb(int red, int green, int blue)
    {
        if ((uint)red > byte.MaxValue || (uint)green > byte.MaxValue || (uint)blue > byte.MaxValue)
        {
            throw new InvalidDataException($"Invalid VBA RGB({red}, {green}, {blue}).");
        }

        // VBA stores RGB() as an OLE_COLOR BGR integer. Decode that storage order before
        // formatting the actual visual color as ARGB; otherwise blue and red are swapped.
        var oleColor = red | (green << 8) | (blue << 16);
        var actualRed = oleColor & 0xFF;
        var actualGreen = (oleColor >> 8) & 0xFF;
        var actualBlue = (oleColor >> 16) & 0xFF;
        return $"FF{actualRed:X2}{actualGreen:X2}{actualBlue:X2}";
    }
}

internal sealed class LegacyAppearanceRegistryDocument
{
    public int SchemaVersion { get; init; }

    public string ExtractionMode { get; init; } = string.Empty;

    public List<LegacyAppearanceRegistryEntry> Entries { get; init; } = [];

    public List<LegacyScriptUnspecifiedSheet> ScriptUnspecifiedSheets { get; init; } = [];
}

internal sealed class LegacyAppearanceRegistryEntry
{
    public string Id { get; init; } = string.Empty;

    public string Procedure { get; init; } = string.Empty;

    public string Report { get; init; } = string.Empty;

    public string Worksheet { get; init; } = string.Empty;

    public string RangeExpression { get; init; } = string.Empty;

    public string DynamicRowBasis { get; init; } = string.Empty;

    public string Property { get; init; } = string.Empty;

    public string NormalizedValue { get; init; } = string.Empty;

    public string Condition { get; init; } = string.Empty;

    public int BasLine { get; init; }

    public int IsmLine { get; init; }

    public string SourceText { get; init; } = string.Empty;

    public string? JetOutput { get; init; }

    public string? NoCorrespondenceReason { get; init; }
}

internal sealed class LegacyScriptUnspecifiedSheet
{
    public string Report { get; init; } = string.Empty;

    public string Worksheet { get; init; } = string.Empty;

    public string SourceProcedure { get; init; } = string.Empty;

    public int BasLine { get; init; }

    public int IsmLine { get; init; }

    public string JetOutput { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;
}

internal sealed record LegacyScriptSource(
    string RelativePath,
    string EncodingName,
    bool HasBom,
    string[] Lines);

internal sealed record LegacyAppearanceCommand(
    string RelativePath,
    string Procedure,
    int LineNumber,
    string LineText,
    string TargetExpression,
    string Property,
    string NormalizedValue,
    string CommandKey);

internal sealed record LegacyExportDatabaseCall(
    string RelativePath,
    string Procedure,
    int LineNumber,
    string LineText);
