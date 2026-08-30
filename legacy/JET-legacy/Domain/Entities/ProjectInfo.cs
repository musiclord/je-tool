namespace JET.Domain.Entities
{
    public sealed class ProjectInfo
    {
        public string ProjectCode { get; set; } = string.Empty;
        public string EntityName { get; set; } = string.Empty;
        public string OperatorId { get; set; } = string.Empty;
        public string Industry { get; set; } = string.Empty;
        public string PeriodStart { get; set; } = string.Empty;
        public string PeriodEnd { get; set; } = string.Empty;
        public string LastPeriodStart { get; set; } = string.Empty;
    }
}
