using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jet.ExcelDriver;

internal sealed class PdfStructureInspector : IPdfInspector
{
    private const long MaximumPdfBytes = 512L * 1024L * 1024L;
    private const int InspectionWindowBytes = 65536;
    private static readonly Regex StartXrefPattern = new(
        @"startxref\s+(?<offset>[0-9]+)\s+%%EOF",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex XrefStreamPattern = new(
        @"\b[0-9]+\s+[0-9]+\s+obj\s*<<(?=[\s\S]*?/Type\s*/XRef)(?=[\s\S]*?/Root\s+)[\s\S]*?>>",
        RegexOptions.CultureInvariant);

    public PdfInspection Inspect(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            return new PdfInspection(false, 0);
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.RandomAccess);
            var length = stream.Length;
            if (length <= 0 || length > MaximumPdfBytes)
            {
                return new PdfInspection(false, length);
            }

            Span<byte> header = stackalloc byte[5];
            if (stream.Read(header) != header.Length || !header.SequenceEqual("%PDF-"u8))
            {
                return new PdfInspection(false, length);
            }

            var tailLength = (int)Math.Min(length, InspectionWindowBytes);
            var tail = new byte[tailLength];
            stream.Position = length - tailLength;
            stream.ReadExactly(tail);
            var tailText = Encoding.ASCII.GetString(tail);
            var matches = StartXrefPattern.Matches(tailText);
            var match = matches.Count == 0 ? null : matches[^1];
            if (match is null
                || !long.TryParse(
                    match.Groups["offset"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var xrefOffset)
                || xrefOffset < 5
                || xrefOffset >= length)
            {
                return new PdfInspection(false, length);
            }

            var xrefLength = (int)Math.Min(InspectionWindowBytes, length - xrefOffset);
            var xrefBytes = new byte[xrefLength];
            stream.Position = xrefOffset;
            stream.ReadExactly(xrefBytes);
            var xrefText = Encoding.ASCII.GetString(xrefBytes);
            var classicXref = xrefText.StartsWith("xref", StringComparison.Ordinal)
                && tailText.Contains("trailer", StringComparison.Ordinal)
                && tailText.Contains("/Root", StringComparison.Ordinal);
            return new PdfInspection(classicXref || XrefStreamPattern.IsMatch(xrefText), length);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return new PdfInspection(false, 0);
        }
    }
}
