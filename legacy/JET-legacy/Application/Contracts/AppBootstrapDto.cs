namespace JET.Application.Contracts
{
    public sealed record AppBootstrapDto(
        string ApplicationName,
        string StartPage,
        string[] SupportedActions,
        DatabaseBootstrapDto Database,
        DemoBootstrapDto Demo);

    public sealed record DatabaseBootstrapDto(
        string Provider,
        bool IsAvailable,
        string ConnectionTarget,
        string Mode);

    public sealed record DemoBootstrapDto(
        bool Enabled);
}
