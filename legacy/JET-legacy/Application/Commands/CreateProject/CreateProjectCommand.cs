namespace JET.Application.Commands.CreateProject
{
    public sealed record CreateProjectCommand(
        string ProjectCode,
        string EntityName,
        string OperatorId,
        string Industry,
        string PeriodStart,
        string PeriodEnd,
        string LastPeriodStart);
}
