using System.Text;

namespace JET.Infrastructure;

/// <summary>
/// OpenXML 沒辦法執行 Excel 的 <c>EntireColumn.AutoFit</c>，所以這裡自己估欄寬；同樣的文字每次估出來的寬度都相同。
/// 做法照舊版 JET，用全部資料裡最寬的內容決定欄寬，並多留一些寬度，讓微軟正黑體與套了格式的數字不會被切掉。
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

    /// <summary>
    /// 欄寬有上限、文字換行時大約要幾行。和 <see cref="ToColumnWidth"/> 留同樣的字形餘裕，
    /// 寧可多估一行，也不讓最後一行被切掉。
    /// </summary>
    internal static int WrappedLineCount(string? value, double columnWidth)
    {
        if (!double.IsFinite(columnWidth) || columnWidth <= HorizontalPadding)
        {
            throw new ArgumentOutOfRangeException(nameof(columnWidth));
        }
        if (string.IsNullOrEmpty(value))
        {
            return 1;
        }

        var usable = (columnWidth - HorizontalPadding) / GlyphSafetyFactor;
        return value
            .Split('\n')
            .Sum(line => Math.Max(
                1,
                (int)Math.Ceiling(line.TrimEnd('\r').EnumerateRunes().Sum(RuneWidth) / usable)));
    }

    private static double RuneWidth(Rune rune) =>
        rune.Value switch
        {
            '\t' => 4D,
            <= 0x7f => 1D,
            _ => 2D
        };
}

/// <summary>呼叫端逐列寫出或先寫到暫存檔時，記下固定幾個欄位各自出現過的最寬內容，最後換算成欄寬。</summary>
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
