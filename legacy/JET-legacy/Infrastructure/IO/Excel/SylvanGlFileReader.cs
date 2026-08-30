using System.Runtime.CompilerServices;
using JET.Domain.Abstractions.Files;
using JET.Domain.Entities;
using Sylvan.Data.Excel;

namespace JET.Infrastructure.IO.Excel
{
    /// <summary>
    /// Sylvan.Data.Excel-backed streaming implementation of
    /// <see cref="IGlFileReader"/>. Yields rows lazily without loading the
    /// workbook into memory (plan.md §3.1.a / docs/jet-guide.md §1.5.4).
    /// </summary>
    public sealed class SylvanGlFileReader : IGlFileReader
    {
        public async IAsyncEnumerable<GlRawRow> ReadAsync(
            string filePath,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path is required.", nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Spreadsheet file not found.", filePath);
            }

            var options = new ExcelDataReaderOptions
            {
                // Treat sheet as headerless so RowIndex == 0 is the actual header row.
                Schema = ExcelSchema.NoHeaders,
            };

            using var reader = ExcelDataReader.Create(filePath, options);

            int rowIndex = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                int fieldCount = reader.RowFieldCount;
                var values = new string?[fieldCount];
                for (int i = 0; i < fieldCount; i++)
                {
                    if (reader.IsDBNull(i))
                    {
                        values[i] = null;
                        continue;
                    }

                    var raw = reader.GetString(i);
                    values[i] = string.IsNullOrEmpty(raw) ? null : raw;
                }

                yield return new GlRawRow(rowIndex++, values);
            }
        }
    }
}
