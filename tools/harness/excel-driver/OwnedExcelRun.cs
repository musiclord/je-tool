using System.Security.Cryptography;
using System.Text.Json;

namespace Jet.ExcelDriver;

internal sealed class OwnedExcelRun
{
    private const string OwnerMarkerName = ".jet-harness-owner.json";
    private readonly byte[] _sourceHashBefore;

    private OwnedExcelRun(
        DriverOptions options,
        string sourceWorkbookPath,
        string outputRoot,
        string savedWorkbookPath,
        string pdfPath,
        byte[] sourceHashBefore,
        long sourceWorkbookBytes)
    {
        Options = options;
        SourceWorkbookPath = sourceWorkbookPath;
        OutputRoot = outputRoot;
        SavedWorkbookPath = savedWorkbookPath;
        PdfPath = pdfPath;
        _sourceHashBefore = sourceHashBefore;
        SourceWorkbookBytes = sourceWorkbookBytes;
    }

    internal DriverOptions Options { get; }

    internal string SourceWorkbookPath { get; }

    internal string OutputRoot { get; }

    internal string SavedWorkbookPath { get; }

    internal string PdfPath { get; }

    internal long SourceWorkbookBytes { get; }

    internal static OwnedExcelRun Open(DriverOptions options)
    {
        var manifestDirectory = Path.GetDirectoryName(options.ManifestPath)
            ?? throw new InvalidDataException("Excel manifest path was rejected.");
        var expectedOwnedRoot = Path.GetFullPath(Path.Combine(manifestDirectory, "scratch"));
        var expectedManifestName = $"excel-{options.ReportKind}-manifest.json";
        if (!string.Equals(options.OwnedRoot, expectedOwnedRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(options.ManifestPath),
                expectedManifestName,
                StringComparison.Ordinal)
            || File.Exists(options.ManifestPath)
            || !Directory.Exists(options.OwnedRoot))
        {
            throw new InvalidDataException("Excel owned paths were rejected.");
        }

        EnsureNoReparseSegments(manifestDirectory);
        EnsureNoReparseSegments(options.OwnedRoot);
        ValidateOwnerMarker(options);

        var sourceWorkbookPath = Path.GetFullPath(Path.Combine(
            options.OwnedRoot,
            "excel-source",
            "workbooks",
            options.ReportKind + ".xlsx"));
        EnsureDescendant(options.OwnedRoot, sourceWorkbookPath);
        EnsureRegularFile(sourceWorkbookPath);
        EnsureNoReparseSegments(sourceWorkbookPath);
        var sourceInfo = new FileInfo(sourceWorkbookPath);
        if (sourceInfo.Length <= 0 || sourceInfo.Length > ExcelDriverContract.MaximumWorkbookBytes)
        {
            throw new InvalidDataException("Excel source workbook size was rejected.");
        }

        var outputParent = Path.GetFullPath(Path.Combine(options.OwnedRoot, "excel-output"));
        var outputRoot = Path.GetFullPath(Path.Combine(outputParent, options.ReportKind));
        EnsureDescendant(options.OwnedRoot, outputRoot);
        if (File.Exists(outputRoot) || Directory.Exists(outputRoot))
        {
            throw new InvalidDataException("Excel output path already exists.");
        }

        Directory.CreateDirectory(outputRoot);
        EnsureNoReparseSegments(outputRoot);
        var savedWorkbookPath = Path.Combine(outputRoot, "roundtrip.xlsx");
        var pdfPath = Path.Combine(outputRoot, "preview.pdf");
        var sourceHashBefore = ComputeHash(sourceWorkbookPath);
        return new OwnedExcelRun(
            options,
            sourceWorkbookPath,
            outputRoot,
            savedWorkbookPath,
            pdfPath,
            sourceHashBefore,
            sourceInfo.Length);
    }

    internal bool IsSourceWorkbookUnchanged()
    {
        EnsureRegularFile(SourceWorkbookPath);
        var current = ComputeHash(SourceWorkbookPath);
        return CryptographicOperations.FixedTimeEquals(_sourceHashBefore, current);
    }

    private static byte[] ComputeHash(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        return SHA256.HashData(stream);
    }

    private static void ValidateOwnerMarker(DriverOptions options)
    {
        var markerPath = Path.Combine(options.OwnedRoot, OwnerMarkerName);
        EnsureRegularFile(markerPath);
        using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
        var root = marker.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schemaVersion", out var schemaVersion)
            || schemaVersion.GetInt32() != 1
            || !root.TryGetProperty("kind", out var kind)
            || !string.Equals(kind.GetString(), "jet-harness-scratch", StringComparison.Ordinal)
            || !root.TryGetProperty("runId", out var runId)
            || !string.Equals(runId.GetString(), options.RunId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Excel owner marker was rejected.");
        }
    }

    private static void EnsureRegularFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException("Excel input file is missing.");
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException("Excel input must be a regular file.");
        }
    }

    private static void EnsureDescendant(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Excel path escaped its owned root.");
        }
    }

    private static void EnsureNoReparseSegments(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException("Excel path has no filesystem root.");
        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                throw new InvalidDataException("Excel path contains a missing segment.");
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Excel path contains a reparse point.");
            }
        }
    }
}
