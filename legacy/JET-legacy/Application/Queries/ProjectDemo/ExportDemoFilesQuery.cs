using System.Data;
using JET.Application.Contracts;
using JET.Application.DemoData;
using Sylvan.Data.Excel;

namespace JET.Application.Queries.ProjectDemo
{
    public sealed record ExportDemoGlFileQuery;

    public sealed record ExportDemoTbFileQuery;

    public sealed record ExportDemoAccountMappingFileQuery;

    public sealed class ExportDemoGlFileQueryHandler
    {
        private readonly IDemoProjectDataGenerator _generator;

        public ExportDemoGlFileQueryHandler()
            : this(new DeterministicDemoProjectDataGenerator())
        {
        }

        public ExportDemoGlFileQueryHandler(IDemoProjectDataGenerator generator)
        {
            _generator = generator;
        }

        public Task<DemoExportFileDto> HandleAsync(ExportDemoGlFileQuery query, CancellationToken cancellationToken)
        {
            var demo = _generator.Generate().Gl;
            return Task.FromResult(DemoExcelExporter.Write(demo.FileName, demo.Columns, demo.Rows, cancellationToken));
        }
    }

    public sealed class ExportDemoTbFileQueryHandler
    {
        private readonly IDemoProjectDataGenerator _generator;

        public ExportDemoTbFileQueryHandler()
            : this(new DeterministicDemoProjectDataGenerator())
        {
        }

        public ExportDemoTbFileQueryHandler(IDemoProjectDataGenerator generator)
        {
            _generator = generator;
        }

        public Task<DemoExportFileDto> HandleAsync(ExportDemoTbFileQuery query, CancellationToken cancellationToken)
        {
            var demo = _generator.Generate().Tb;
            return Task.FromResult(DemoExcelExporter.Write(demo.FileName, demo.Columns, demo.Rows, cancellationToken));
        }
    }

    public sealed class ExportDemoAccountMappingFileQueryHandler
    {
        private readonly IDemoProjectDataGenerator _generator;

        public ExportDemoAccountMappingFileQueryHandler()
            : this(new DeterministicDemoProjectDataGenerator())
        {
        }

        public ExportDemoAccountMappingFileQueryHandler(IDemoProjectDataGenerator generator)
        {
            _generator = generator;
        }

        public Task<DemoExportFileDto> HandleAsync(ExportDemoAccountMappingFileQuery query, CancellationToken cancellationToken)
        {
            var demo = _generator.Generate().AccountMapping;
            var columns = demo.Rows.Count == 0 ? Array.Empty<string>() : demo.Rows[0].Keys.ToArray();
            return Task.FromResult(DemoExcelExporter.Write(demo.FileName, columns, demo.Rows, cancellationToken));
        }
    }

    internal static class DemoExcelExporter
    {
        public static DemoExportFileDto Write(
            string fileName,
            IReadOnlyList<string> columns,
            IReadOnlyList<Dictionary<string, object?>> rows,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = Path.Combine(Path.GetTempPath(), "jet-demo");
            Directory.CreateDirectory(directory);
            var filePath = Path.Combine(directory, fileName);

            using var table = new DataTable("Demo");
            foreach (var column in columns)
            {
                table.Columns.Add(column, typeof(string));
            }

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = columns.Select(column => row.TryGetValue(column, out var value) ? Convert.ToString(value) ?? string.Empty : string.Empty).ToArray();
                table.Rows.Add(values);
            }

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            using var writer = ExcelDataWriter.Create(filePath);
            using var reader = table.CreateDataReader();
            writer.Write(reader);

            return new DemoExportFileDto(filePath, fileName);
        }
    }
}
