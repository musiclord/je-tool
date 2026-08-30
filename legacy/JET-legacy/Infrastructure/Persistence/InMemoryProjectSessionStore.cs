using JET.Domain.Abstractions;
using JET.Domain.Entities;

namespace JET.Infrastructure.Persistence
{
    public sealed class InMemoryProjectSessionStore : IProjectSessionStore
    {
        private readonly object _lock = new();

        public ProjectInfo? Project { get; private set; }
        public string? CurrentProjectId { get; private set; }
        public Dictionary<string, string> GlMapping { get; private set; } = new();
        public Dictionary<string, string> TbMapping { get; private set; } = new();
        public IReadOnlyList<string> Holidays { get; private set; } = [];
        public IReadOnlyList<string> MakeupDays { get; private set; } = [];
        public IReadOnlyList<int> Weekends { get; private set; } = [6, 0];

        public void SetProject(ProjectInfo project) { lock (_lock) Project = project; }
        public void SetCurrentProjectId(string projectId) { lock (_lock) CurrentProjectId = projectId; }
        public void SetGlMapping(Dictionary<string, string> mapping) { lock (_lock) GlMapping = mapping; }
        public void SetTbMapping(Dictionary<string, string> mapping) { lock (_lock) TbMapping = mapping; }
        public void SetHolidays(IReadOnlyList<string> dates) { lock (_lock) Holidays = dates; }
        public void SetMakeupDays(IReadOnlyList<string> dates) { lock (_lock) MakeupDays = dates; }
        public void SetWeekends(IReadOnlyList<int> weekends) { lock (_lock) Weekends = weekends; }
    }
}
