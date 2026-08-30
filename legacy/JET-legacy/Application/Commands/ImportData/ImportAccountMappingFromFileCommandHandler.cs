using JET.Domain.Abstractions;
using JET.Domain.Abstractions.Files;
using JET.Domain.Abstractions.Repositories;

namespace JET.Application.Commands.ImportData
{
    /// <summary>
    /// Drives the streaming AccountMapping ingest pipeline introduced in plan.md
    /// Phase 3 §3.1.d. Resolves the active project via <see cref="IProjectSessionStore"/>,
    /// streams rows via the shared <see cref="IGlFileReader"/>, and persists them
    /// through <see cref="IAccountMappingRepository"/> without ever materialising
    /// the full file in memory or in the bridge response.
    /// </summary>
    public sealed class ImportAccountMappingFromFileCommandHandler
    {
        private readonly IProjectSessionStore _session;
        private readonly IGlFileReader _fileReader;
        private readonly IAccountMappingRepository _accountMappingRepository;

        public ImportAccountMappingFromFileCommandHandler(
            IProjectSessionStore session,
            IGlFileReader fileReader,
            IAccountMappingRepository accountMappingRepository)
        {
            _session = session;
            _fileReader = fileReader;
            _accountMappingRepository = accountMappingRepository;
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
            var result = await _accountMappingRepository
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
