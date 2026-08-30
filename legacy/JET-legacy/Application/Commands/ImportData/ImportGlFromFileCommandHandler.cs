using JET.Domain.Abstractions;
using JET.Domain.Abstractions.Files;
using JET.Domain.Abstractions.Repositories;

namespace JET.Application.Commands.ImportData
{
    /// <summary>
    /// Drives the streaming GL ingest pipeline introduced in plan.md Phase 3
    /// §3.1.b. Resolves the active project via <see cref="IProjectSessionStore"/>,
    /// streams rows via <see cref="IGlFileReader"/>, and persists them through
    /// <see cref="IGlRepository"/> without ever materialising the full file in
    /// memory or in the bridge response.
    /// </summary>
    public sealed class ImportGlFromFileCommandHandler
    {
        private readonly IProjectSessionStore _session;
        private readonly IGlFileReader _fileReader;
        private readonly IGlRepository _glRepository;

        public ImportGlFromFileCommandHandler(
            IProjectSessionStore session,
            IGlFileReader fileReader,
            IGlRepository glRepository)
        {
            _session = session;
            _fileReader = fileReader;
            _glRepository = glRepository;
        }

        public async Task<object> HandleAsync(string filePath, string? fileName, string? mode, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            var projectId = _session.CurrentProjectId;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                throw new InvalidOperationException("No active project; call project.create first.");
            }

            var resolvedFileName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(filePath) : fileName!;
            var resolvedMode = string.IsNullOrWhiteSpace(mode) ? "replace" : mode!;

            var rows = _fileReader.ReadAsync(filePath, cancellationToken);
            var result = await _glRepository
                .BulkInsertStagingAsync(projectId, resolvedFileName, rows, resolvedMode, cancellationToken)
                .ConfigureAwait(false);

            return new
            {
                batchId = result.BatchId,
                rowCount = result.RowCount,
                columns = result.Columns,
            };
        }
    }
}
