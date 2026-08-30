using JET.Domain.Entities;

namespace JET.Domain.Abstractions
{
    /// <summary>
    /// In-memory pointer for the active workflow. After plan §3.4 this store does NOT
    /// hold raw GL/TB/AccountMapping rows; row data lives in <c>staging_*</c> /
    /// <c>target_*</c> / <c>result_*</c> tables and is queried by repositories.
    /// Mission constraint §1.5.5: bridge must never re-hydrate large row sets in
    /// process memory.
    /// </summary>
    public interface IProjectSessionStore
    {
        ProjectInfo? Project { get; }
        string? CurrentProjectId { get; }
        Dictionary<string, string> GlMapping { get; }
        Dictionary<string, string> TbMapping { get; }
        IReadOnlyList<string> Holidays { get; }
        IReadOnlyList<string> MakeupDays { get; }
        IReadOnlyList<int> Weekends { get; }

        void SetProject(ProjectInfo project);
        void SetCurrentProjectId(string projectId);
        void SetGlMapping(Dictionary<string, string> mapping);
        void SetTbMapping(Dictionary<string, string> mapping);
        void SetHolidays(IReadOnlyList<string> dates);
        void SetMakeupDays(IReadOnlyList<string> dates);
        void SetWeekends(IReadOnlyList<int> weekends);
    }
}
