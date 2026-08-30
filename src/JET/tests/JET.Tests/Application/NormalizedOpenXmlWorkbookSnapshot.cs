using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace JET.Tests.Application;

/// <summary>
/// Test-only structural snapshot for an OpenXML workbook. ZIP timestamps, XML
/// prefixes, core-property timestamps and relationship ids are deliberately not
/// comparison inputs; logical relationship targets and every non-directory
/// package part remain covered.
/// </summary>
internal sealed class NormalizedOpenXmlWorkbookSnapshot
{
    private const string CorePropertiesNamespace =
        "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private const string DublinCoreTermsNamespace = "http://purl.org/dc/terms/";
    private const string MarkupCompatibilityNamespace =
        "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const string OfficeDocumentRelationshipsNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string StrictOfficeDocumentRelationshipsNamespace =
        "http://purl.oclc.org/ooxml/officeDocument/relationships";
    private const string XmlSchemaInstanceNamespace =
        "http://www.w3.org/2001/XMLSchema-instance";

    private readonly IReadOnlyDictionary<string, string> _componentDigests;

    private NormalizedOpenXmlWorkbookSnapshot(
        string sheetManifest,
        IReadOnlyDictionary<string, string> componentDigests)
    {
        SheetManifest = sheetManifest;
        _componentDigests = componentDigests;
    }

    internal string SheetManifest { get; }

    internal string Fingerprint
    {
        get
        {
            var canonical = new StringBuilder();
            AppendValue(canonical, SheetManifest);
            foreach (var (name, digest) in _componentDigests)
            {
                AppendValue(canonical, name);
                AppendValue(canonical, digest);
            }

            return Digest(canonical.ToString());
        }
    }

    internal static NormalizedOpenXmlWorkbookSnapshot Capture(
        string path,
        IReadOnlyDictionary<string, string>? exactValueReplacements = null)
    {
        ValidateReplacements(exactValueReplacements);

        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToDictionary(
                entry => NormalizeEntryName(entry.FullName),
                StringComparer.Ordinal);
        var relationships = ReadRelationships(entries);
        var workbook = LoadXml(
            RequireEntry(entries, "xl/workbook.xml"),
            "xl/workbook.xml");
        var workbookRelationships = relationships.GetValueOrDefault(
            "xl/workbook.xml",
            RelationshipSet.Empty);
        var sheetManifest = BuildSheetManifest(workbook, workbookRelationships);
        var componentDigests = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (entryName, entry) in entries.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (entryName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                var sourcePart = SourcePartForRelationships(entryName);
                componentDigests[entryName] = Digest(
                    relationships.GetValueOrDefault(sourcePart, RelationshipSet.Empty).Canonical);
                continue;
            }

            if (entryName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                var document = string.Equals(entryName, "xl/workbook.xml", StringComparison.Ordinal)
                    ? workbook
                    : LoadXml(entry, entryName);
                var canonical = CanonicalizeXml(
                    document,
                    relationships.GetValueOrDefault(entryName, RelationshipSet.Empty),
                    sortRootChildren: string.Equals(
                        entryName,
                        "[Content_Types].xml",
                        StringComparison.Ordinal),
                    exactValueReplacements);
                componentDigests[entryName] = Digest(canonical);
                continue;
            }

            using var stream = entry.Open();
            componentDigests[entryName] = Convert.ToHexString(SHA256.HashData(stream));
        }

        return new NormalizedOpenXmlWorkbookSnapshot(
            sheetManifest,
            new ReadOnlyDictionary<string, string>(componentDigests));
    }

    /// <summary>Returns null when both normalized structures are equivalent.</summary>
    internal string? DescribeDifference(NormalizedOpenXmlWorkbookSnapshot actual)
    {
        if (!string.Equals(SheetManifest, actual.SheetManifest, StringComparison.Ordinal))
        {
            return $"worksheet set/order/visibility differs. expected={SheetManifest}; actual={actual.SheetManifest}";
        }

        var expectedNames = _componentDigests.Keys.ToArray();
        var actualNames = actual._componentDigests.Keys.ToArray();
        if (!expectedNames.SequenceEqual(actualNames, StringComparer.Ordinal))
        {
            return "package part set differs. "
                + $"expected=[{string.Join(", ", expectedNames)}]; "
                + $"actual=[{string.Join(", ", actualNames)}]";
        }

        foreach (var name in expectedNames)
        {
            var expectedDigest = _componentDigests[name];
            var actualDigest = actual._componentDigests[name];
            if (!string.Equals(expectedDigest, actualDigest, StringComparison.Ordinal))
            {
                return $"normalized part '{name}' differs "
                    + $"(expected SHA-256 {expectedDigest}, actual {actualDigest}).";
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, RelationshipSet> ReadRelationships(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        var result = new Dictionary<string, RelationshipSet>(StringComparer.Ordinal);
        foreach (var (entryName, entry) in entries)
        {
            if (!entryName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourcePart = SourcePartForRelationships(entryName);
            var document = LoadXml(entry, entryName);
            var byId = new Dictionary<string, RelationshipDescriptor>(StringComparer.Ordinal);
            var descriptors = new List<RelationshipDescriptor>();
            foreach (var element in document.Descendants()
                         .Where(item => item.Name.LocalName == "Relationship"))
            {
                var id = (string?)element.Attribute("Id")
                    ?? throw new InvalidDataException($"OpenXML relationship in '{entryName}' has no Id.");
                var type = (string?)element.Attribute("Type")
                    ?? throw new InvalidDataException(
                        $"OpenXML relationship '{id}' in '{entryName}' has no Type.");
                var target = (string?)element.Attribute("Target")
                    ?? throw new InvalidDataException(
                        $"OpenXML relationship '{id}' in '{entryName}' has no Target.");
                var targetMode = (string?)element.Attribute("TargetMode") ?? "Internal";
                var descriptor = new RelationshipDescriptor(
                    type,
                    NormalizeRelationshipTarget(sourcePart, target, targetMode),
                    targetMode);
                if (!byId.TryAdd(id, descriptor))
                {
                    throw new InvalidDataException(
                        $"OpenXML relationship id '{id}' is duplicated in '{entryName}'.");
                }

                descriptors.Add(descriptor);
            }

            var canonical = string.Concat(
                descriptors
                    .Select(RelationshipToken)
                    .Order(StringComparer.Ordinal));
            result.Add(sourcePart, new RelationshipSet(byId, canonical));
        }

        return result;
    }

    private static string BuildSheetManifest(
        XDocument workbook,
        RelationshipSet relationships)
    {
        var sheets = workbook.Descendants()
            .Where(element => element.Name.LocalName == "sheet")
            .ToArray();
        if (sheets.Length == 0)
        {
            throw new InvalidDataException("OpenXML workbook has no worksheets.");
        }

        var manifest = new StringBuilder();
        for (var index = 0; index < sheets.Length; index++)
        {
            var sheet = sheets[index];
            var name = (string?)sheet.Attribute("name")
                ?? throw new InvalidDataException($"OpenXML worksheet at position {index + 1} has no name.");
            var state = (string?)sheet.Attribute("state") ?? "visible";
            var relationshipAttribute = sheet.Attributes()
                .SingleOrDefault(attribute =>
                    IsRelationshipReference(attribute)
                    && relationships.ById.ContainsKey(attribute.Value));
            if (relationshipAttribute is null)
            {
                throw new InvalidDataException(
                    $"OpenXML worksheet '{name}' has no resolvable relationship.");
            }

            manifest.Append(index + 1).Append(':');
            AppendValue(manifest, name);
            AppendValue(manifest, state);
            AppendValue(
                manifest,
                RelationshipToken(relationships.ById[relationshipAttribute.Value]));
        }

        return manifest.ToString();
    }

    private static string CanonicalizeXml(
        XDocument document,
        RelationshipSet relationships,
        bool sortRootChildren,
        IReadOnlyDictionary<string, string>? exactValueReplacements)
    {
        var root = document.Root
            ?? throw new InvalidDataException("OpenXML XML part has no root element.");
        var output = new StringBuilder();
        AppendElement(output, root, relationships, sortRootChildren);
        var canonical = output.ToString();
        if (exactValueReplacements is null)
        {
            return canonical;
        }

        foreach (var replacement in exactValueReplacements.OrderBy(
                     item => item.Key,
                     StringComparer.Ordinal))
        {
            canonical = canonical.Replace(
                replacement.Key,
                replacement.Value,
                StringComparison.Ordinal);
        }

        return canonical;
    }

    private static void AppendElement(
        StringBuilder output,
        XElement element,
        RelationshipSet relationships,
        bool sortChildren = false)
    {
        output.Append('E');
        AppendValue(output, element.Name.NamespaceName);
        AppendValue(output, element.Name.LocalName);

        var attributes = element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .Select(attribute => new
            {
                attribute.Name.NamespaceName,
                attribute.Name.LocalName,
                Value = NormalizeAttributeValue(element, attribute, relationships)
            })
            .OrderBy(attribute => attribute.NamespaceName, StringComparer.Ordinal)
            .ThenBy(attribute => attribute.LocalName, StringComparer.Ordinal)
            .ToArray();
        output.Append(attributes.Length).Append(':');
        foreach (var attribute in attributes)
        {
            AppendValue(output, attribute.NamespaceName);
            AppendValue(output, attribute.LocalName);
            AppendValue(output, attribute.Value);
        }

        if (IsVolatileCoreProperty(element))
        {
            output.Append('V');
            AppendValue(output, "volatile-package-metadata");
            output.Append('e');
            return;
        }

        if (sortChildren)
        {
            var children = element.Elements()
                .Select(child =>
                {
                    var canonical = new StringBuilder();
                    AppendElement(canonical, child, relationships);
                    return canonical.ToString();
                })
                .Order(StringComparer.Ordinal)
                .ToArray();
            output.Append(children.Length).Append(':');
            foreach (var child in children)
            {
                AppendValue(output, child);
            }

            output.Append('e');
            return;
        }

        var hasElementChildren = element.Elements().Any();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    AppendElement(output, child, relationships);
                    break;
                case XText text when !hasElementChildren || !string.IsNullOrWhiteSpace(text.Value):
                    output.Append('T');
                    AppendValue(output, text.Value);
                    break;
            }
        }

        output.Append('e');
    }

    private static string NormalizeAttributeValue(
        XElement element,
        XAttribute attribute,
        RelationshipSet relationships)
    {
        if (IsRelationshipReference(attribute)
            && relationships.ById.TryGetValue(attribute.Value, out var relationship))
        {
            return RelationshipToken(relationship);
        }

        if (attribute.Name.NamespaceName == MarkupCompatibilityNamespace
            && attribute.Name.LocalName == "Ignorable")
        {
            return string.Join(
                "\n",
                attribute.Value
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(prefix => element.GetNamespaceOfPrefix(prefix)?.NamespaceName ?? $"prefix:{prefix}")
                    .Order(StringComparer.Ordinal));
        }

        if (attribute.Name.NamespaceName == XmlSchemaInstanceNamespace
            && attribute.Name.LocalName == "type")
        {
            var separator = attribute.Value.IndexOf(':');
            if (separator > 0)
            {
                var prefix = attribute.Value[..separator];
                var namespaceName = element.GetNamespaceOfPrefix(prefix)?.NamespaceName;
                if (namespaceName is not null)
                {
                    return $"{{{namespaceName}}}{attribute.Value[(separator + 1)..]}";
                }
            }
        }

        return attribute.Value;
    }

    private static bool IsRelationshipReference(XAttribute attribute) =>
        attribute.Name.NamespaceName is OfficeDocumentRelationshipsNamespace
            or StrictOfficeDocumentRelationshipsNamespace;

    private static bool IsVolatileCoreProperty(XElement element) =>
        (element.Name.NamespaceName == DublinCoreTermsNamespace
            && element.Name.LocalName is "created" or "modified")
        || (element.Name.NamespaceName == CorePropertiesNamespace
            && element.Name.LocalName == "lastPrinted");

    private static string NormalizeRelationshipTarget(
        string sourcePart,
        string target,
        string targetMode)
    {
        target = target.Replace('\\', '/');
        if (string.Equals(targetMode, "External", StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        var segments = target.StartsWith("/", StringComparison.Ordinal)
            ? new List<string>()
            : SourceDirectorySegments(sourcePart);
        foreach (var segment in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    break;
                case ".." when segments.Count > 0:
                    segments.RemoveAt(segments.Count - 1);
                    break;
                case "..":
                    throw new InvalidDataException(
                        $"OpenXML relationship target '{target}' escapes the package root.");
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return string.Join("/", segments);
    }

    private static List<string> SourceDirectorySegments(string sourcePart)
    {
        var separator = sourcePart.LastIndexOf('/');
        return separator < 0
            ? []
            : sourcePart[..separator]
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
    }

    private static string SourcePartForRelationships(string relationshipEntry)
    {
        if (string.Equals(relationshipEntry, "_rels/.rels", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        const string marker = "/_rels/";
        var markerIndex = relationshipEntry.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0 || !relationshipEntry.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"'{relationshipEntry}' is not a valid OpenXML relationships part name.");
        }

        var directory = relationshipEntry[..markerIndex];
        var relatedFile = relationshipEntry[(markerIndex + marker.Length)..^".rels".Length];
        return $"{directory}/{relatedFile}";
    }

    private static string RelationshipToken(RelationshipDescriptor relationship)
    {
        var output = new StringBuilder("R");
        AppendValue(output, relationship.Type);
        AppendValue(output, relationship.TargetMode);
        AppendValue(output, relationship.Target);
        return output.ToString();
    }

    private static void AppendValue(StringBuilder output, string value) =>
        output.Append(value.Length).Append(':').Append(value);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ValidateReplacements(
        IReadOnlyDictionary<string, string>? replacements)
    {
        if (replacements is null)
        {
            return;
        }

        foreach (var (source, target) in replacements)
        {
            if (source.Length == 0 || source.Length != target.Length)
            {
                throw new ArgumentException(
                    "OpenXML exact-value replacements must be non-empty and length preserving.",
                    nameof(replacements));
            }
        }
    }

    private static string NormalizeEntryName(string name) => name.Replace('\\', '/');

    private static ZipArchiveEntry RequireEntry(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string name) =>
        entries.TryGetValue(name, out var entry)
            ? entry
            : throw new InvalidDataException($"OpenXML package has no '{name}' part.");

    private static XDocument LoadXml(ZipArchiveEntry entry, string entryName)
    {
        try
        {
            using var stream = entry.Open();
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is not InvalidDataException)
        {
            throw new InvalidDataException(
                $"OpenXML package part '{entryName}' is not valid XML.",
                exception);
        }
    }

    private sealed record RelationshipDescriptor(
        string Type,
        string Target,
        string TargetMode);

    private sealed record RelationshipSet(
        IReadOnlyDictionary<string, RelationshipDescriptor> ById,
        string Canonical)
    {
        internal static RelationshipSet Empty { get; } = new(
            new ReadOnlyDictionary<string, RelationshipDescriptor>(
                new Dictionary<string, RelationshipDescriptor>(StringComparer.Ordinal)),
            string.Empty);
    }
}
