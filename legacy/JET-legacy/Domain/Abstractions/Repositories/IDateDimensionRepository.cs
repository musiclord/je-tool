namespace JET.Domain.Abstractions.Repositories
{
    /// <summary>
    /// Persists holiday / make-up calendar inputs. Phase 3 §6.1 step 6 of <c>plan.md</c>.
    /// </summary>
    /// <remarks>
    /// Calendar inputs are small (≤ a few hundred dates per project per kind), so the
    /// dates list is passed inline. Per <c>docs/jet-guide.md</c> §1.5.3, this is the
    /// <em>only</em> ingest contract that may carry rows in-payload; GL/TB must use
    /// streaming file readers.
    /// </remarks>
    public interface IDateDimensionRepository
    {
        /// <summary>
        /// Replaces all calendar input rows for the given project + kind. Atomic:
        /// prior batches for the same (projectId, kind) are removed before insert.
        /// Returns the new batch id.
        /// </summary>
        /// <param name="kind">
        /// Calendar kind. Use <c>"holiday"</c> or <c>"makeupDay"</c>; persisted under
        /// <c>config_import_batch.dataset_kind = "calendar:" + kind</c> to namespace it
        /// from GL/TB ingest batches.
        /// </param>
        Task<string> ReplaceCalendarInputAsync(string projectId, string kind, IReadOnlyList<string> dates, CancellationToken cancellationToken);
    }
}
