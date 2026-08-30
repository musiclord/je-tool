using System.Text;

namespace JET.Infrastructure;

/// <summary>
/// OpenXML cannot execute Excel's <c>EntireColumn.AutoFit</c>. This deterministic
/// measurement keeps the legacy full-data behavior while leaving enough glyph
/// headroom for Microsoft JhengHei and formatted numeric display.
/// </summary>
internal static class ExcelDisplayWidth
{
    private const double MinimumColumnWidth = 8D;
    private const double MaximumColumnWidth = 255D;
    private const double HorizontalPadding = 2D;
    private const double GlyphSafetyFactor = 1.15D;

    internal static double Measure(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0D;
        }

        return value
            .Split('\n')
            .Select(line => line.TrimEnd('\r').EnumerateRunes().Sum(RuneWidth))
            .DefaultIfEmpty(0D)
            .Max();
    }

    internal static double ToColumnWidth(double measuredWidth)
    {
        if (!double.IsFinite(measuredWidth) || measuredWidth < 0D)
        {
            throw new ArgumentOutOfRangeException(nameof(measuredWidth));
        }

        var padded = Math.Ceiling(measuredWidth * GlyphSafetyFactor + HorizontalPadding);
        return Math.Min(MaximumColumnWidth, Math.Max(MinimumColumnWidth, padded));
    }

    private static double RuneWidth(Rune rune) =>
        rune.Value switch
        {
            '\t' => 4D,
            <= 0x7f => 1D,
            _ => 2D
        };
}

/// <summary>Tracks a bounded column schema while callers stream or spool rows.</summary>
internal sealed class ExcelDisplayWidthTracker
{
    private readonly double[] _maximums;

    internal ExcelDisplayWidthTracker(IReadOnlyList<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (headers.Count == 0)
        {
            throw new ArgumentException("At least one display column is required.", nameof(headers));
        }

        _maximums = headers.Select(ExcelDisplayWidth.Measure).ToArray();
    }

    internal int ColumnCount => _maximums.Length;

    internal IReadOnlyList<double> Widths =>
        _maximums.Select(ExcelDisplayWidth.ToColumnWidth).ToArray();

    internal void Observe(IReadOnlyList<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != _maximums.Length)
        {
            throw new InvalidDataException(
                $"Display row column count {values.Count} does not match schema count {_maximums.Length}.");
        }

        for (var index = 0; index < values.Count; index++)
        {
            _maximums[index] = Math.Max(
                _maximums[index],
                ExcelDisplayWidth.Measure(values[index]));
        }
    }
}
