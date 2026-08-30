using System.Globalization;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Fail-closed policy for the only real-sample derivative that may be tracked by Stage 9.
/// The validator never includes rejected JSON keys or values in exceptions because those
/// tokens are untrusted until the closed appearance vocabulary accepts them.
/// </summary>
internal static class SpreadsheetAppearanceSnapshotSchemaGuard
{
    internal const string FixtureRelativeDirectory =
        "src/JET/tests/JET.Tests/Infrastructure/Fixtures/legacy-sample-appearance";

    private const int LegacySchemaVersion = 1;
    internal const int CompressedSchemaVersion = SpreadsheetAppearanceSnapshotCompression.SchemaVersion;
    private const uint ExcelMaximumColumn = 16_384;
    private const uint ExcelMaximumRow = 1_048_576;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly IReadOnlySet<string> LegacyDocumentKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion",
            "entries"
        };

    private static readonly IReadOnlySet<string> CompressedDocumentKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion",
            "signatures",
            "bindings",
            "entries"
        };

    private static readonly IReadOnlySet<string> EntryKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "location",
            "property",
            "value"
        };

    private static readonly IReadOnlySet<string> SignatureKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "id",
            "properties"
        };

    private static readonly IReadOnlySet<string> SignaturePropertyKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "property",
            "value"
        };

    private static readonly IReadOnlySet<string> BindingKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "signatureId",
            "ranges"
        };

    private static readonly IReadOnlySet<string> SemanticProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "font.name", "font.size", "font.bold", "font.italic", "font.colorArgb",
            "fill.pattern", "fill.foregroundArgb",
            "border.left.style", "border.left.colorArgb",
            "border.right.style", "border.right.colorArgb",
            "border.top.style", "border.top.colorArgb",
            "border.bottom.style", "border.bottom.colorArgb",
            "numberFormat.code",
            "alignment.horizontal", "alignment.vertical", "alignment.wrapText",
            "alignment.indent", "alignment.shrinkToFit",
            "protection.locked"
        };

    private static readonly IReadOnlySet<string> BooleanSemanticProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "font.bold", "font.italic", "alignment.wrapText",
            "alignment.shrinkToFit", "protection.locked"
        };

    private static readonly IReadOnlySet<string> ColorSemanticProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "font.colorArgb", "fill.foregroundArgb",
            "border.left.colorArgb", "border.right.colorArgb",
            "border.top.colorArgb", "border.bottom.colorArgb"
        };

    private static readonly IReadOnlySet<string> BorderStyleProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "border.left.style", "border.right.style",
            "border.top.style", "border.bottom.style"
        };

    private static readonly IReadOnlySet<string> AllowedFontNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "細明體", "微软雅黑", "微軟正黑體", "新細明體",
            "Arial", "Cambria", "Microsoft JhengHei", "PMingLiU", "Times New Roman",
            "Aptos Display", "Aptos Narrow", "Calibri", "Calibri Light"
        };

    private static readonly IReadOnlySet<string> AllowedFontSizes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "8", "9", "10", "11", "12", "13", "14", "16", "18"
        };

    private static readonly IReadOnlySet<string> AllowedFillPatterns =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "none", "solid", "mediumGray", "darkGray", "lightGray",
            "darkHorizontal", "darkVertical", "darkDown", "darkUp", "darkGrid",
            "darkTrellis", "lightHorizontal", "lightVertical", "lightDown", "lightUp",
            "lightGrid", "lightTrellis", "gray125", "gray0625"
        };

    private static readonly IReadOnlySet<string> AllowedBorderStyles =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "none", "thin", "medium", "dashed", "dotted", "thick", "double", "hair",
            "mediumDashed", "dashDot", "mediumDashDot", "dashDotDot",
            "mediumDashDotDot", "slantDashDot"
        };

    private static readonly IReadOnlySet<string> AllowedHorizontalAlignments =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "general", "left", "center", "right", "fill", "justify",
            "centerContinuous", "distributed"
        };

    private static readonly IReadOnlySet<string> AllowedVerticalAlignments =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "top", "center", "bottom", "justify", "distributed"
        };

    private static readonly IReadOnlySet<string> AllowedPaneStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "split", "frozen", "frozenSplit"
        };

    private static readonly IReadOnlySet<string> AllowedActivePanes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "topLeft", "topRight", "bottomLeft", "bottomRight"
        };

    private static readonly IReadOnlySet<string> PageSetupBooleanProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "usePrinterDefaults", "blackAndWhite", "draft", "useFirstPageNumber"
        };

    private static readonly IReadOnlySet<string> SheetProtectionBooleanProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "sheet", "objects", "scenarios", "formatCells", "formatColumns", "formatRows",
            "insertColumns", "insertRows", "insertHyperlinks", "deleteColumns", "deleteRows",
            "selectLockedCells", "sort", "autoFilter", "pivotTables", "selectUnlockedCells"
        };

    private static readonly IReadOnlySet<string> ExactCustomNumberFormats =
        new HashSet<string>(StringComparer.Ordinal)
        {
            @"_-* #,##0.00_-;\-* #,##0.00_-;_-* ""-""??_-;_-@_-",
            @"_(* #,##0_);_(* \(#,##0\);_(* ""-""??_);_(@_)",
            "#,##0.0000;[Red]-#,##0.0000",
            "yyyy-mm-dd",
            "hh:mm:ss"
        };

    private static readonly Regex CanonicalDecimalPattern = new(
        @"^(?:0|[1-9][0-9]*)(?:\.[0-9]*[1-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ArgbPattern = new(
        "^[0-9A-F]{8}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Sha256Pattern = new(
        "^[0-9A-F]{64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumberFormatPattern = new(
        @"^(?:0|#,##0)(?:\.0{1,28})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ColumnLocationPattern = new(
        @"^column:(?<min>[1-9][0-9]*)-(?<max>[1-9][0-9]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RowLocationPattern = new(
        @"^row:(?<row>[1-9][0-9]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SheetViewLocationPattern = new(
        @"^sheetView:(?<view>[1-9][0-9]*)(?<pane>\.pane)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyPrescreenAdditionalSheetPattern = new(
        @"^A[234](?: \([2-9][0-9]*\))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (LegacyReportKind Report, string Slug)[] Reports =
    [
        (LegacyReportKind.ValidationReport, "validation-report"),
        (LegacyReportKind.AccountMapping, "account-mapping"),
        (LegacyReportKind.InfReport, "inf-report"),
        (LegacyReportKind.PrescreenReport, "prescreen-report"),
        (LegacyReportKind.CriteriaSelectionReport, "criteria-selection-report"),
        (LegacyReportKind.WorkingPaper, "working-paper")
    ];

    internal static IReadOnlyList<SpreadsheetAppearanceSnapshotIdentity> ExpectedSnapshots { get; } =
        new[] { "case-A", "case-B" }
            .SelectMany(caseAlias => Reports.Select(report =>
                new SpreadsheetAppearanceSnapshotIdentity(
                    $"{caseAlias}-{report.Slug}.appearance.json",
                    report.Report)))
            .ToArray();

    internal static void ValidateRepositoryInventory(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var directory = Path.GetFullPath(Path.Combine(
            root,
            FixtureRelativeDirectory.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidInventory);
        }

        if (!Directory.Exists(directory))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidInventory);
        }
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidInventory);
        }

        var entries = Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var expected = ExpectedSnapshots.ToDictionary(
            item => item.FileName,
            StringComparer.Ordinal);
        var names = entries.Select(Path.GetFileName).ToArray();
        if (names.Length != expected.Count
            || !names.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Keys))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidInventory);
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (!File.Exists(entry)
                || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.InvalidInventory);
            }

            if (!expected.TryGetValue(name, out var identity))
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.InvalidInventory);
            }

            var bytes = File.ReadAllBytes(entry);
            if (ReadSchemaVersion(bytes) != CompressedSchemaVersion)
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.UnsupportedSchemaVersion);
            }
            _ = ReadEntries(bytes, identity.Report);
        }
    }

    internal static void ValidateJson(
        ReadOnlyMemory<byte> bytes,
        LegacyReportKind report) =>
        _ = ReadEntries(bytes, report);

    internal static int ReadSchemaVersion(ReadOnlyMemory<byte> bytes)
    {
        using var document = ParseJson(bytes);
        return ReadSchemaVersion(document.RootElement);
    }

    internal static IReadOnlyList<AppearanceEntry> ReadEntries(
        ReadOnlyMemory<byte> bytes,
        LegacyReportKind report)
    {
        using var document = ParseJson(bytes);
        var root = document.RootElement;
        var schemaVersion = ReadSchemaVersion(root);
        return schemaVersion switch
        {
            LegacySchemaVersion => ReadLegacyEntries(root, bytes, report),
            CompressedSchemaVersion => ReadCompressedEntries(root, bytes, report),
            _ => throw new UnreachableException()
        };
    }

    internal static byte[] CompressJson(
        ReadOnlyMemory<byte> bytes,
        LegacyReportKind report)
    {
        var entries = ReadEntries(bytes, report);
        var compressed = SpreadsheetAppearanceSnapshotCompression.Serialize(entries);
        var roundTripped = ReadEntries(compressed, report);
        if (!entries.SequenceEqual(roundTripped))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
        }
        return compressed;
    }

    private static JsonDocument ParseJson(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Span.StartsWith(Encoding.UTF8.Preamble))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.NonCanonicalJson);
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(bytes.Span);
        }
        catch (DecoderFallbackException)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidUtf8);
            throw new UnreachableException();
        }

        try
        {
            return JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
        }
        catch (JsonException)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidJson);
            throw new UnreachableException();
        }
    }

    private static int ReadSchemaVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
        }

        JsonElement schema = default;
        var foundSchema = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.DuplicateKey);
            }
            if (property.Name == "schemaVersion")
            {
                schema = property.Value;
                foundSchema = true;
            }
        }
        if (!foundSchema)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.MissingKey);
        }
        if (schema.ValueKind != JsonValueKind.Number)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.UnsupportedSchemaVersion);
        }

        var raw = schema.GetRawText();
        if (raw == LegacySchemaVersion.ToString(CultureInfo.InvariantCulture))
        {
            return LegacySchemaVersion;
        }
        if (raw == CompressedSchemaVersion.ToString(CultureInfo.InvariantCulture))
        {
            return CompressedSchemaVersion;
        }
        Reject(SpreadsheetAppearanceSnapshotViolation.UnsupportedSchemaVersion);
        throw new UnreachableException();
    }

    private static IReadOnlyList<AppearanceEntry> ReadLegacyEntries(
        JsonElement root,
        ReadOnlyMemory<byte> bytes,
        LegacyReportKind report)
    {
        ValidateObjectKeys(root, LegacyDocumentKeys);
        var entries = ReadFlatEntries(
            root.GetProperty("entries"),
            report,
            allowDirectCellLocations: true);
        var canonical = Encoding.UTF8.GetBytes(
            SpreadsheetAppearanceFingerprint.SerializeEntries(entries));
        if (!bytes.Span.SequenceEqual(canonical))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.NonCanonicalJson);
        }
        return entries;
    }

    private static IReadOnlyList<AppearanceEntry> ReadCompressedEntries(
        JsonElement root,
        ReadOnlyMemory<byte> bytes,
        LegacyReportKind report)
    {
        ValidateObjectKeys(root, CompressedDocumentKeys);

        var signaturesElement = root.GetProperty("signatures");
        if (signaturesElement.ValueKind != JsonValueKind.Array)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
        }
        var signatures = new List<CompressedAppearanceSignature>(
            signaturesElement.GetArrayLength());
        var expectedSignatureId = 1;
        foreach (var item in signaturesElement.EnumerateArray())
        {
            ValidateObjectKeys(item, SignatureKeys);
            var id = RequireCanonicalPositiveInt(item, "id");
            if (id != expectedSignatureId)
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
            }

            var propertiesElement = item.GetProperty("properties");
            if (propertiesElement.ValueKind != JsonValueKind.Array
                || propertiesElement.GetArrayLength() == 0)
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
            }
            var properties = new List<CompressedAppearanceProperty>(
                propertiesElement.GetArrayLength());
            string? previousProperty = null;
            foreach (var propertyElement in propertiesElement.EnumerateArray())
            {
                ValidateObjectKeys(propertyElement, SignaturePropertyKeys);
                var property = RequireString(propertyElement, "property");
                var value = RequireString(propertyElement, "value");
                ValidateSemanticProperty(property, value);
                if (previousProperty is not null)
                {
                    var order = string.CompareOrdinal(previousProperty, property);
                    if (order > 0)
                    {
                        Reject(SpreadsheetAppearanceSnapshotViolation.NonCanonicalEntryOrder);
                    }
                    if (order == 0)
                    {
                        Reject(SpreadsheetAppearanceSnapshotViolation.DuplicateEntry);
                    }
                }
                properties.Add(new CompressedAppearanceProperty(property, value));
                previousProperty = property;
            }
            signatures.Add(new CompressedAppearanceSignature(id, properties));
            expectedSignatureId++;
        }

        var bindingsElement = root.GetProperty("bindings");
        if (bindingsElement.ValueKind != JsonValueKind.Array
            || bindingsElement.GetArrayLength() != signatures.Count)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
        }
        var cellEntries = new List<AppearanceEntry>();
        var occupiedLocations = new HashSet<string>(StringComparer.Ordinal);
        ulong expandedLocationCount = 0UL;
        ulong expandedAppearanceEntryCount = 0UL;
        var expectedBindingId = 1;
        foreach (var item in bindingsElement.EnumerateArray())
        {
            ValidateObjectKeys(item, BindingKeys);
            var signatureId = RequireCanonicalPositiveInt(item, "signatureId");
            if (signatureId != expectedBindingId
                || signatureId > signatures.Count)
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
            }
            var signature = signatures[signatureId - 1];

            var rangesElement = item.GetProperty("ranges");
            if (rangesElement.ValueKind != JsonValueKind.Array
                || rangesElement.GetArrayLength() == 0)
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
            }
            string? previousRange = null;
            foreach (var rangeElement in rangesElement.EnumerateArray())
            {
                if (rangeElement.ValueKind != JsonValueKind.String)
                {
                    Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
                }
                var encodedRange = rangeElement.GetString()!;
                if (previousRange is not null)
                {
                    var order = string.CompareOrdinal(previousRange, encodedRange);
                    if (order > 0)
                    {
                        Reject(SpreadsheetAppearanceSnapshotViolation.NonCanonicalEntryOrder);
                    }
                    if (order == 0)
                    {
                        Reject(SpreadsheetAppearanceSnapshotViolation.DuplicateEntry);
                    }
                }
                if (!SpreadsheetAppearanceSnapshotCompression.TryParseRange(
                        encodedRange,
                        out var range))
                {
                    Reject(SpreadsheetAppearanceSnapshotViolation.UnknownLocation);
                }
                if (!IsKnownSheet(report, range.SheetName))
                {
                    Reject(SpreadsheetAppearanceSnapshotViolation.UnknownSheet);
                }
                try
                {
                    expandedLocationCount = checked(expandedLocationCount + range.CellCount);
                    expandedAppearanceEntryCount = checked(
                        expandedAppearanceEntryCount
                        + range.CellCount * (ulong)signature.Properties.Count);
                }
                catch (OverflowException)
                {
                    Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
                }
                if (expandedLocationCount
                    > SpreadsheetAppearanceSnapshotCompression.MaximumExpandedCellLocations)
                {
                    Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
                }
                if (expandedAppearanceEntryCount
                    > SpreadsheetAppearanceSnapshotCompression.MaximumAppearanceEntries)
                {
                    Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
                }

                foreach (var reference in SpreadsheetAppearanceSnapshotCompression
                             .ExpandReferences(range))
                {
                    var location = $"{range.SheetName}!{reference}";
                    if (!occupiedLocations.Add(location))
                    {
                        Reject(SpreadsheetAppearanceSnapshotViolation.DuplicateEntry);
                    }
                    foreach (var property in signature.Properties)
                    {
                        cellEntries.Add(new AppearanceEntry(
                            location,
                            property.Property,
                            property.Value));
                    }
                }
                previousRange = encodedRange;
            }
            expectedBindingId++;
        }

        var structuralEntries = ReadFlatEntries(
            root.GetProperty("entries"),
            report,
            allowDirectCellLocations: false);
        if (expandedAppearanceEntryCount + (ulong)structuralEntries.Count
            > SpreadsheetAppearanceSnapshotCompression.MaximumAppearanceEntries)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
        }
        var entries = cellEntries
            .Concat(structuralEntries)
            .OrderBy(entry => entry.Location, StringComparer.Ordinal)
            .ThenBy(entry => entry.Property, StringComparer.Ordinal)
            .ToArray();
        EnsureUniqueEntries(entries);

        var canonical = SpreadsheetAppearanceSnapshotCompression.Serialize(entries);
        if (!bytes.Span.SequenceEqual(canonical))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.NonCanonicalJson);
        }
        return entries;
    }

    private static IReadOnlyList<AppearanceEntry> ReadFlatEntries(
        JsonElement entriesElement,
        LegacyReportKind report,
        bool allowDirectCellLocations)
    {
        if (entriesElement.ValueKind != JsonValueKind.Array)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
        }
        if (entriesElement.GetArrayLength()
            > SpreadsheetAppearanceSnapshotCompression.MaximumAppearanceEntries)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
        }

        var entries = new List<AppearanceEntry>(entriesElement.GetArrayLength());
        AppearanceEntry? previous = null;
        foreach (var item in entriesElement.EnumerateArray())
        {
            ValidateObjectKeys(item, EntryKeys);
            var location = RequireString(item, "location");
            var property = RequireString(item, "property");
            var value = RequireString(item, "value");
            var entry = new AppearanceEntry(location, property, value);
            ValidateEntry(entry, report);
            if (!allowDirectCellLocations
                && SpreadsheetAppearanceSnapshotCompression.TrySplitDirectCellLocation(
                    entry.Location,
                    out _,
                    out _))
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
            }

            if (previous is not null)
            {
                var locationOrder = string.CompareOrdinal(previous.Location, entry.Location);
                var propertyOrder = locationOrder == 0
                    ? string.CompareOrdinal(previous.Property, entry.Property)
                    : 0;
                if (locationOrder > 0 || locationOrder == 0 && propertyOrder > 0)
                {
                    Reject(SpreadsheetAppearanceSnapshotViolation.NonCanonicalEntryOrder);
                }
                if (locationOrder == 0 && propertyOrder == 0)
                {
                    Reject(SpreadsheetAppearanceSnapshotViolation.DuplicateEntry);
                }
            }

            entries.Add(entry);
            previous = entry;
        }
        return entries;
    }

    private static void EnsureUniqueEntries(IReadOnlyList<AppearanceEntry> entries)
    {
        AppearanceEntry? previous = null;
        foreach (var entry in entries)
        {
            if (previous is not null
                && string.Equals(previous.Location, entry.Location, StringComparison.Ordinal)
                && string.Equals(previous.Property, entry.Property, StringComparison.Ordinal))
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.DuplicateEntry);
            }
            previous = entry;
        }
    }

    private static int RequireCanonicalPositiveInt(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        var parsed = 0;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out parsed)
            || parsed <= 0
            || value.GetRawText() != parsed.ToString(CultureInfo.InvariantCulture))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
        }
        return parsed;
    }

    private static void ValidateObjectKeys(
        JsonElement element,
        IReadOnlySet<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.DuplicateKey);
            }
            if (!expected.Contains(property.Name))
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.UnknownKey);
            }
        }
        if (!seen.SetEquals(expected))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.MissingKey);
        }
    }

    private static string RequireString(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.InvalidShape);
        }
        return value.GetString()!;
    }

    private static void ValidateEntry(
        AppearanceEntry entry,
        LegacyReportKind report)
    {
        if (entry.Location == "$workbook")
        {
            const string prefix = "default.";
            if (!entry.Property.StartsWith(prefix, StringComparison.Ordinal))
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.UnknownProperty);
            }
            ValidateSemanticProperty(entry.Property[prefix.Length..], entry.Value);
            return;
        }

        var separator = entry.Location.IndexOf('!');
        if (separator <= 0
            || separator != entry.Location.LastIndexOf('!')
            || separator == entry.Location.Length - 1)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.UnknownLocation);
        }

        var sheetName = entry.Location[..separator];
        if (!IsKnownSheet(report, sheetName))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.UnknownSheet);
        }
        var scope = entry.Location[(separator + 1)..];

        if (scope == "cellStream")
        {
            ValidateCellStream(entry.Property, entry.Value);
            return;
        }
        if (scope == "sheetFormat")
        {
            ValidateSheetFormat(entry.Property, entry.Value);
            return;
        }
        if (scope == "pageSetup")
        {
            ValidatePageSetup(entry.Property, entry.Value);
            return;
        }
        if (scope == "sheetProtection")
        {
            if (!SheetProtectionBooleanProperties.Contains(entry.Property))
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.UnknownProperty);
            }
            ValidateBoolean(entry.Value);
            return;
        }

        var sheetView = SheetViewLocationPattern.Match(scope);
        if (sheetView.Success)
        {
            ValidatePositiveUInt(sheetView.Groups["view"].Value, uint.MaxValue);
            if (sheetView.Groups["pane"].Success)
            {
                ValidatePane(entry.Property, entry.Value);
            }
            else
            {
                ValidateSheetView(entry.Property, entry.Value);
            }
            return;
        }

        var column = ColumnLocationPattern.Match(scope);
        if (column.Success)
        {
            var min = ParsePositiveUInt(column.Groups["min"].Value, ExcelMaximumColumn);
            var max = ParsePositiveUInt(column.Groups["max"].Value, ExcelMaximumColumn);
            if (max < min)
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.UnknownLocation);
            }
            ValidateColumnOrRowProperty(entry.Property, entry.Value, isColumn: true);
            return;
        }

        var row = RowLocationPattern.Match(scope);
        if (row.Success)
        {
            ValidatePositiveUInt(row.Groups["row"].Value, ExcelMaximumRow);
            ValidateColumnOrRowProperty(entry.Property, entry.Value, isColumn: false);
            return;
        }

        if (scope.StartsWith("merge:", StringComparison.Ordinal))
        {
            var range = scope["merge:".Length..];
            var rangeSeparator = range.IndexOf(':');
            if (rangeSeparator <= 0
                || rangeSeparator != range.LastIndexOf(':')
                || !IsCellReference(range[..rangeSeparator])
                || !IsCellReference(range[(rangeSeparator + 1)..]))
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.UnknownLocation);
            }
            if (entry.Property != "present")
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.UnknownProperty);
            }
            ValidateExact(entry.Value, "true");
            return;
        }

        if (!IsCellReference(scope))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.UnknownLocation);
        }
        ValidateSemanticProperty(entry.Property, entry.Value);
    }

    private static bool IsKnownSheet(LegacyReportKind report, string sheetName)
    {
        // 六份正式報表自 2026-08-14 起共用一張 VeryHidden `JET_Metadata`
        // （jet-guide §7.2）；它是 JET-only 的跨報表 sheet，不屬於 legacy 版面。
        if (string.Equals(
                sheetName,
                JET.Domain.ReportWorkbookMetadataFormat.WorksheetName,
                StringComparison.Ordinal))
        {
            return true;
        }

        // A2-A4 are fixed legacy ExportDatabase sheet identities. JET deliberately
        // represents their current outcome in the summary instead of creating those
        // worksheets, but Stage 9 must still be able to validate a legacy oracle that
        // contains them. step1-3 is likewise a fixed legacy conditional sheet that can
        // be absent from both real cases while remaining script-proven.
        if (report == LegacyReportKind.PrescreenReport
            && LegacyPrescreenAdditionalSheetPattern.IsMatch(sheetName))
        {
            return true;
        }
        if (report == LegacyReportKind.WorkingPaper
            && string.Equals(
                sheetName,
                "step1-3 完整性測試之差異說明",
                StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            _ = LegacyWorkbookHeaderCatalog.Rule(report, sheetName);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateColumnOrRowProperty(
        string property,
        string value,
        bool isColumn)
    {
        if (property == (isColumn ? "width" : "height"))
        {
            ValidateCanonicalDecimal(
                value,
                minimum: 0M,
                // OpenXML stores both as double values. The fingerprint canonicalizes
                // them to decimal text, so a non-negative numeric grammar is the closed
                // structural vocabulary; legacy exporters are not required to honor
                // Excel UI caps when writing the package.
                maximum: decimal.MaxValue);
            return;
        }
        if (property == "hidden")
        {
            ValidateExact(value, "true");
            return;
        }

        const string stylePrefix = "style.";
        if (!property.StartsWith(stylePrefix, StringComparison.Ordinal))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.UnknownProperty);
        }
        ValidateSemanticProperty(property[stylePrefix.Length..], value);
    }

    private static void ValidateCellStream(string property, string value)
    {
        switch (property)
        {
            case "cellCount":
                if (!ulong.TryParse(
                        value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var count)
                    || value != count.ToString(CultureInfo.InvariantCulture))
                {
                    Reject(SpreadsheetAppearanceSnapshotViolation.UnknownValue);
                }
                return;
            case "sha256":
                if (!Sha256Pattern.IsMatch(value))
                {
                    Reject(SpreadsheetAppearanceSnapshotViolation.UnknownValue);
                }
                return;
            default:
                Reject(SpreadsheetAppearanceSnapshotViolation.UnknownProperty);
                return;
        }
    }

    private static void ValidateSheetFormat(string property, string value)
    {
        switch (property)
        {
            case "defaultRowHeight":
                ValidateCanonicalDecimal(value, 0M, decimal.MaxValue);
                return;
            case "defaultColumnWidth":
                ValidateCanonicalDecimal(value, 0M, decimal.MaxValue);
                return;
            case "baseColumnWidth":
                ValidateUInt(value, 0U, 255U);
                return;
            case "zeroHeight":
                ValidateExact(value, "true");
                return;
            default:
                Reject(SpreadsheetAppearanceSnapshotViolation.UnknownProperty);
                return;
        }
    }

    private static void ValidateSheetView(string property, string value)
    {
        switch (property)
        {
            case "showGridLines":
                ValidateExact(value, "false");
                return;
            case "zoomScale":
                ValidateUInt(value, 10U, 400U);
                return;
            default:
                Reject(SpreadsheetAppearanceSnapshotViolation.UnknownProperty);
                return;
        }
    }

    private static void ValidatePane(string property, string value)
    {
        switch (property)
        {
            case "state":
                ValidateSet(value, AllowedPaneStates);
                return;
            case "xSplit":
            case "ySplit":
                ValidateCanonicalDecimal(value, 0M, ExcelMaximumRow);
                return;
            case "topLeftCell":
                if (!IsCellReference(value))
                {
                    Reject(SpreadsheetAppearanceSnapshotViolation.UnknownValue);
                }
                return;
            case "activePane":
                ValidateSet(value, AllowedActivePanes);
                return;
            default:
                Reject(SpreadsheetAppearanceSnapshotViolation.UnknownProperty);
                return;
        }
    }

    private static void ValidatePageSetup(string property, string value)
    {
        if (PageSetupBooleanProperties.Contains(property))
        {
            ValidateBoolean(value);
            return;
        }

        switch (property)
        {
            case "paperSize":
                ValidateUInt(value, 1U, 118U);
                return;
            case "scale":
                ValidateUInt(value, 10U, 400U);
                return;
            case "firstPageNumber":
                ValidateUInt(value, 1U, uint.MaxValue);
                return;
            case "fitToWidth":
            case "fitToHeight":
                ValidateUInt(value, 0U, 32_767U);
                return;
            case "horizontalDpi":
            case "verticalDpi":
                // CT_PageSetup declares these as unsignedInt. Keep the complete closed
                // OpenXML value domain rather than assuming a device-specific DPI cap.
                ValidateUInt(value, 0U, uint.MaxValue);
                return;
            case "copies":
                ValidateUInt(value, 1U, 32_767U);
                return;
            case "pageOrder":
                ValidateSet(value, new[] { "downThenOver", "overThenDown" });
                return;
            case "orientation":
                ValidateSet(value, new[] { "default", "portrait", "landscape" });
                return;
            case "cellComments":
                ValidateSet(value, new[] { "none", "asDisplayed", "atEnd" });
                return;
            case "errors":
                ValidateSet(value, new[] { "displayed", "blank", "dash", "NA" });
                return;
            default:
                Reject(SpreadsheetAppearanceSnapshotViolation.UnknownProperty);
                return;
        }
    }

    private static void ValidateSemanticProperty(string property, string value)
    {
        if (!SemanticProperties.Contains(property))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.UnknownProperty);
        }
        if (BooleanSemanticProperties.Contains(property))
        {
            ValidateBoolean(value);
            return;
        }
        if (ColorSemanticProperties.Contains(property))
        {
            if (!ArgbPattern.IsMatch(value))
            {
                Reject(SpreadsheetAppearanceSnapshotViolation.UnknownValue);
            }
            return;
        }
        if (BorderStyleProperties.Contains(property))
        {
            ValidateSet(value, AllowedBorderStyles);
            return;
        }

        switch (property)
        {
            case "font.name":
                ValidateSet(value, AllowedFontNames);
                return;
            case "font.size":
                ValidateSet(value, AllowedFontSizes);
                return;
            case "fill.pattern":
                ValidateSet(value, AllowedFillPatterns);
                return;
            case "numberFormat.code":
                if (!SpreadsheetAppearanceFingerprint.IsKnownBuiltInNumberFormatCode(value)
                    && !ExactCustomNumberFormats.Contains(value)
                    && !NumberFormatPattern.IsMatch(value))
                {
                    Reject(SpreadsheetAppearanceSnapshotViolation.UnknownValue);
                }
                return;
            case "alignment.horizontal":
                ValidateSet(value, AllowedHorizontalAlignments);
                return;
            case "alignment.vertical":
                ValidateSet(value, AllowedVerticalAlignments);
                return;
            case "alignment.indent":
                ValidateUInt(value, 0U, 250U);
                return;
            default:
                Reject(SpreadsheetAppearanceSnapshotViolation.UnknownProperty);
                return;
        }
    }

    private static bool IsCellReference(string reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return false;
        }

        var index = reference[0] == '$' ? 1 : 0;
        uint column = 0U;
        var letters = 0;
        while (index < reference.Length && reference[index] is >= 'A' and <= 'Z')
        {
            if (letters == 3)
            {
                return false;
            }
            column = checked(column * 26U + (uint)(reference[index] - 'A' + 1));
            index++;
            letters++;
        }
        if (index < reference.Length && reference[index] == '$')
        {
            index++;
        }
        return letters is > 0 and <= 3
            && column is > 0 and <= ExcelMaximumColumn
            && uint.TryParse(
                reference.AsSpan(index),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var row)
            && row is > 0 and <= ExcelMaximumRow
            && reference[index] != '0';
    }

    private static uint ParsePositiveUInt(string value, uint maximum)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed == 0U
            || parsed > maximum
            || value != parsed.ToString(CultureInfo.InvariantCulture))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.UnknownLocation);
        }
        return parsed;
    }

    private static void ValidatePositiveUInt(string value, uint maximum) =>
        _ = ParsePositiveUInt(value, maximum);

    private static void ValidateUInt(string value, uint minimum, uint maximum)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum
            || parsed > maximum
            || value != parsed.ToString(CultureInfo.InvariantCulture))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.UnknownValue);
        }
    }

    private static void ValidateCanonicalDecimal(
        string value,
        decimal minimum,
        decimal maximum)
    {
        if (!CanonicalDecimalPattern.IsMatch(value)
            || !decimal.TryParse(
                value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.UnknownValue);
        }
    }

    private static void ValidateBoolean(string value)
    {
        if (value is not "true" and not "false")
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.UnknownValue);
        }
    }

    private static void ValidateExact(string value, string expected)
    {
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.UnknownValue);
        }
    }

    private static void ValidateSet(string value, IEnumerable<string> allowed)
    {
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            Reject(SpreadsheetAppearanceSnapshotViolation.UnknownValue);
        }
    }

    [DoesNotReturn]
    private static void Reject(SpreadsheetAppearanceSnapshotViolation violation) =>
        throw new SpreadsheetAppearanceSnapshotSchemaException(violation);
}

internal sealed record SpreadsheetAppearanceSnapshotIdentity(
    string FileName,
    LegacyReportKind Report);

internal enum SpreadsheetAppearanceSnapshotViolation
{
    InvalidInventory,
    InvalidUtf8,
    InvalidJson,
    InvalidShape,
    UnknownKey,
    MissingKey,
    DuplicateKey,
    UnsupportedSchemaVersion,
    NonCanonicalEntryOrder,
    DuplicateEntry,
    NonCanonicalJson,
    UnknownLocation,
    UnknownSheet,
    UnknownProperty,
    UnknownValue
}

internal sealed class SpreadsheetAppearanceSnapshotSchemaException(
    SpreadsheetAppearanceSnapshotViolation violation)
    : Exception($"Appearance snapshot rejected ({violation}).")
{
    internal SpreadsheetAppearanceSnapshotViolation Violation { get; } = violation;
}
